using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mlcp.Shared;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;

namespace Mlcp.UnitTests.Identity;

/// <summary>ADR-015 two-app token acquisition and ADR-016 token failure handling.</summary>
public class TenantTokenProviderTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private const string CoreClientId = "c0000000-0000-0000-0000-00000000000c";
    private const string UsageClientId = "u0000000-0000-0000-0000-00000000000u";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeCredentialFactory _factory;
    private readonly X509Certificate2 _callerCertificate;

    public TenantTokenProviderTests()
    {
        _factory = new FakeCredentialFactory(_clock);
        _callerCertificate = CreateCertificate(_clock.GetUtcNow().AddYears(1));
    }

    public void Dispose()
    {
        _callerCertificate.Dispose();
        GC.SuppressFinalize(this);
    }

    private TenantTokenProvider Provider(MlcpIdentityOptions? options = null, IClientCertificateLoader? loader = null)
        => new(
            options ?? new MlcpIdentityOptions
            {
                ClientId = CoreClientId,
                UsageInsightsClientId = UsageClientId,
                ClientCertificate = _callerCertificate,
            },
            _clock,
            NullLogger<TenantTokenProvider>.Instance,
            _factory,
            loader);

    [Theory]
    [InlineData("AADSTS700016: Application with identifier 'x' was not found in the directory.", "AADSTS700016", TokenFailureCategory.Refused)]
    [InlineData("invalid_client: AADSTS7000229: The client application is missing service principal in the tenant.", "AADSTS7000229", TokenFailureCategory.Refused)]
    [InlineData("AADSTS7000112: Application is disabled.", "AADSTS7000112", TokenFailureCategory.Refused)]
    [InlineData("AADSTS90002: Tenant not found.", "AADSTS90002", TokenFailureCategory.Refused)]
    [InlineData("invalid_grant", "invalid_grant", TokenFailureCategory.Refused)]
    [InlineData("AADSTS7000215: Invalid client secret provided.", "AADSTS7000215", TokenFailureCategory.PlatformCredential)]
    [InlineData("AADSTS7000222: The provided client secret keys are expired.", "AADSTS7000222", TokenFailureCategory.PlatformCredential)]
    [InlineData("AADSTS700027: Client assertion failed signature validation.", "AADSTS700027", TokenFailureCategory.PlatformCredential)]
    [InlineData("invalid_client: bad assertion", "invalid_client", TokenFailureCategory.PlatformCredential)]
    [InlineData("something unexpected", null, TokenFailureCategory.Unknown)]
    public void Token_endpoint_errors_are_reduced(string message, string? code, TokenFailureCategory category)
    {
        var failure = TokenFailure.FromException(new AuthenticationFailedException(message));

        failure.ErrorCode.Should().Be(code);
        failure.Category.Should().Be(category);
    }

    [Fact]
    public void Network_failures_are_transport()
    {
        TokenFailure.FromException(new AuthenticationFailedException("failed", new HttpRequestException("reset")))
            .Category.Should().Be(TokenFailureCategory.Transport);
    }

    [Fact]
    public void An_unavailable_credential_is_a_platform_failure()
    {
        TokenFailure.FromException(new CredentialUnavailableException("no managed identity"))
            .Category.Should().Be(TokenFailureCategory.PlatformCredential);
    }

    [Fact]
    public async Task Tokens_are_cached_per_tenant_app_and_audience()
    {
        using var provider = Provider();

        var graph = await provider.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);
        var graphAgain = await provider.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);
        var arm = await provider.GetAccessTokenAsync(Tenant, TokenAudience.ResourceManager, CancellationToken.None);
        var reports = await provider.GetAccessTokenAsync(Tenant, TokenAudience.GraphReports, CancellationToken.None);

        graphAgain.Should().Be(graph);
        arm.Should().NotBe(graph);
        reports.Should().StartWith(UsageClientId, "GraphReports is issued to the Usage Insights app");
        graph.Should().StartWith(CoreClientId);
        _factory.TokenRequests.Should().Be(3);
        _factory.Created.Should().BeEquivalentTo([CoreClientId, UsageClientId]);
    }

    [Fact]
    public async Task Eviction_forces_a_new_credential_and_token()
    {
        using var provider = Provider();

        await provider.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);
        provider.Evict(Tenant, MicrosoftApp.Core, TokenAudience.Graph);
        await provider.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);

        _factory.TokenRequests.Should().Be(2);
        _factory.Created.Should().HaveCount(2, "the old credential's MSAL cache would return the same token");
    }

    [Fact]
    public async Task An_expiring_token_is_refreshed_before_use()
    {
        using var provider = Provider();

        await provider.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(56));
        await provider.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);

        _factory.TokenRequests.Should().Be(2);
    }

    [Fact]
    public async Task A_refusal_is_reported_raw_and_drops_the_credential()
    {
        using var provider = Provider();
        _factory.Fail = _ => new AuthenticationFailedException("AADSTS700016: not found");

        var act = () => provider.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<TokenAcquisitionException>();
        thrown.Which.App.Should().Be(MicrosoftApp.Core);
        thrown.Which.Failure.ErrorCode.Should().Be("AADSTS700016");

        _factory.Fail = null;
        await provider.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);
        _factory.Created.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_platform_failure_pauses_that_app_for_five_minutes_only()
    {
        using var provider = Provider();
        _factory.Fail = clientId => clientId == CoreClientId ? new AuthenticationFailedException("AADSTS700027: bad signature") : null;

        var first = () => provider.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);
        (await first.Should().ThrowAsync<TokenAcquisitionException>()).Which.Failure.Category.Should().Be(TokenFailureCategory.PlatformCredential);

        // Paused for every tenant, without asking Entra again.
        _factory.Fail = null;
        var requestsBefore = _factory.TokenRequests;
        var otherTenant = () => provider.GetAccessTokenAsync(Guid.NewGuid(), TokenAudience.ResourceManager, CancellationToken.None);
        var paused = await otherTenant.Should().ThrowAsync<TokenAcquisitionException>();
        paused.Which.Failure.Category.Should().Be(TokenFailureCategory.Paused);
        paused.Which.PausedUntilUtc.Should().Be(_clock.GetUtcNow().AddMinutes(5));
        _factory.TokenRequests.Should().Be(requestsBefore);

        // The Usage Insights app is unaffected.
        await provider.GetAccessTokenAsync(Tenant, TokenAudience.GraphReports, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(5));
        await provider.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);
    }

    [Fact]
    public async Task A_missing_usage_registration_is_a_not_configured_failure()
    {
        using var provider = Provider(new MlcpIdentityOptions { ClientId = CoreClientId, ClientCertificate = _callerCertificate });

        var act = () => provider.GetAccessTokenAsync(Tenant, TokenAudience.GraphReports, CancellationToken.None);

        (await act.Should().ThrowAsync<TokenAcquisitionException>()).Which.Failure.Category.Should().Be(TokenFailureCategory.NotConfigured);
    }

    [Fact]
    public async Task A_caller_supplied_certificate_is_not_disposed()
    {
        var provider = Provider();
        await provider.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);

        provider.Dispose();

        _callerCertificate.Handle.Should().NotBe(IntPtr.Zero);
    }

    [Fact]
    public async Task A_key_vault_certificate_near_expiry_is_reloaded()
    {
        var expiring = CreateCertificate(_clock.GetUtcNow().AddDays(3));
        var renewed = CreateCertificate(_clock.GetUtcNow().AddYears(1));
        var loader = new QueueLoader(expiring, renewed);

        using var provider = Provider(
            new MlcpIdentityOptions { ClientId = CoreClientId, KeyVaultUri = new Uri("https://kv.test/"), CertificateName = "mlcp" },
            loader);

        await provider.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);
        _factory.Certificates.Should().Equal(expiring.Thumbprint);

        _clock.Advance(TimeSpan.FromHours(1));
        provider.Evict(Tenant, MicrosoftApp.Core, TokenAudience.Graph);
        await provider.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);

        loader.Loads.Should().Be(2);
        _factory.Certificates.Last().Should().Be(renewed.Thumbprint);
    }

    [Fact]
    public async Task A_certificate_load_failure_pauses_both_apps()
    {
        var loader = new QueueLoader();

        using var provider = Provider(
            new MlcpIdentityOptions
            {
                ClientId = CoreClientId,
                UsageInsightsClientId = UsageClientId,
                KeyVaultUri = new Uri("https://kv.test/"),
                CertificateName = "mlcp",
            },
            loader);

        var core = () => provider.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);
        (await core.Should().ThrowAsync<TokenAcquisitionException>()).Which.Failure.ErrorCode.Should().Be("CertificateLoadFailed");

        var usage = () => provider.GetAccessTokenAsync(Tenant, TokenAudience.GraphReports, CancellationToken.None);
        (await usage.Should().ThrowAsync<TokenAcquisitionException>()).Which.Failure.Category.Should().Be(TokenFailureCategory.Paused);
    }

    [Fact]
    public void The_certificate_chain_is_not_sent_by_default()
    {
        new MlcpIdentityOptions().SendCertificateChain.Should().BeFalse();
    }

    [Fact]
    public void Startup_fails_outside_development_without_the_registrations()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AzureAd:ClientId"] = CoreClientId })
            .Build();

        var act = () => new ServiceCollection().AddMlcpShared(configuration, isDevelopment: false, MicrosoftCallBudget.Worker);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*UsageInsightsClientId*")
            .WithMessage("*certificate*");
    }

    [Fact]
    public void Startup_succeeds_outside_development_with_both_registrations_and_a_certificate()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureAd:ClientId"] = CoreClientId,
                ["AzureAd:UsageInsightsClientId"] = UsageClientId,
                ["Mlcp:KeyVaultUri"] = "https://kv.test/",
                ["Mlcp:ClientCertificateName"] = "mlcp",
            })
            .Build();

        var services = new ServiceCollection().AddLogging().AddMlcpShared(configuration, isDevelopment: false, MicrosoftCallBudget.Worker);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        provider.GetRequiredService<ITenantTokenProvider>().Should().BeOfType<TenantTokenProvider>();
        provider.GetRequiredService<MicrosoftApiResilienceOptions>().Budget.Should().Be(MicrosoftCallBudget.Worker);
    }

    [Fact]
    public async Task Development_without_a_registration_starts_and_fails_calls_clearly()
    {
        var services = new ServiceCollection().AddLogging().AddMlcpShared(new ConfigurationBuilder().Build(), isDevelopment: true, MicrosoftCallBudget.Interactive);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var tokens = provider.GetRequiredService<ITenantTokenProvider>();
        var act = () => tokens.GetAccessTokenAsync(Tenant, TokenAudience.Graph, CancellationToken.None);

        (await act.Should().ThrowAsync<TokenAcquisitionException>()).Which.Failure.Category.Should().Be(TokenFailureCategory.NotConfigured);
    }

    private static X509Certificate2 CreateCertificate(DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=mlcp-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(notAfter.AddYears(-2), notAfter);
    }

    private sealed class FakeCredentialFactory : IClientCredentialFactory
    {
        private readonly TimeProvider _clock;
        private int _tokens;

        public FakeCredentialFactory(TimeProvider clock)
        {
            _clock = clock;
        }

        public List<string> Created { get; } = [];

        public List<string> Certificates { get; } = [];

        public Func<string, Exception?>? Fail { get; set; }

        public int TokenRequests => _tokens;

        public TokenCredential Create(Guid tenantId, string clientId, X509Certificate2? certificate, string? clientSecret)
        {
            Created.Add(clientId);

            if (certificate is not null)
            {
                Certificates.Add(certificate.Thumbprint);
            }

            return new Credential(this, clientId);
        }

        private sealed class Credential : TokenCredential
        {
            private readonly FakeCredentialFactory _owner;
            private readonly string _clientId;

            public Credential(FakeCredentialFactory owner, string clientId)
            {
                _owner = owner;
                _clientId = clientId;
            }

            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            {
                if (_owner.Fail?.Invoke(_clientId) is { } failure)
                {
                    throw failure;
                }

                var n = Interlocked.Increment(ref _owner._tokens);
                return ValueTask.FromResult(new AccessToken(
                    $"{_clientId}|{requestContext.Scopes[0]}|{n}",
                    _owner._clock.GetUtcNow().AddHours(1)));
            }
        }
    }

    private sealed class QueueLoader : IClientCertificateLoader
    {
        private readonly Queue<X509Certificate2> _certificates;

        public QueueLoader(params X509Certificate2[] certificates)
        {
            _certificates = new Queue<X509Certificate2>(certificates);
        }

        public int Loads { get; private set; }

        public Task<X509Certificate2> LoadAsync(CancellationToken cancellationToken)
        {
            Loads++;

            return _certificates.TryDequeue(out var certificate)
                ? Task.FromResult(certificate)
                : throw new InvalidOperationException("Key Vault unavailable");
        }
    }
}
