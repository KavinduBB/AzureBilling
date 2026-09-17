using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Tenancy;
using Mlcp.Integration.Azure;
using Mlcp.Integration.Graph;
using Mlcp.Shared;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;
using WireMock;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace Mlcp.IntegrationTests.MicrosoftApi;

/// <summary>
/// Real probes, client, executor and pipelines against a WireMock stand-in for Graph and ARM.
/// No SQL and no real Microsoft: the store is in memory and tokens are stubbed.
/// </summary>
public sealed class MicrosoftApiHarness : IDisposable
{
    public static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private readonly ServiceProvider _services;

    public MicrosoftApiHarness(Tenant? tenant = null, MicrosoftApiResilienceOptions? resilience = null)
    {
        Server = WireMockServer.Start();
        Tenant = tenant ?? ProvisioningTenant();
        Store = new InMemoryStore(Tenant);

        // 127.0.0.1 rather than localhost: WireMock listens on IPv4, and an IPv6-first lookup of
        // localhost can stall a connection long enough to look like a transient failure.
        var baseAddress = new Uri(Server.Url!.Replace("localhost", "127.0.0.1", StringComparison.Ordinal).TrimEnd('/') + "/");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantTokenProvider>(Tokens);
        services.AddMlcpShared(
            identityOptions: null,
            resilience ?? MicrosoftApiResilienceOptions.Default with
            {
                MaxRetryAttempts = 2,
                BaseDelay = TimeSpan.FromMilliseconds(20),
                MaxBackoffDelay = TimeSpan.FromMilliseconds(100),
                AttemptTimeout = TimeSpan.FromSeconds(10),
                Budget = MicrosoftCallBudget.Worker,
            });
        services.AddGraphIntegration(baseAddress);
        services.AddAzureIntegration(baseAddress);
        services.AddSingleton<ITenantOnboardingStore>(Store);
        services.AddScoped(sp => new CapabilityDiscoveryService(
            Store,
            sp.GetRequiredService<IGraphCapabilityProbe>(),
            sp.GetRequiredService<IAzureCapabilityProbe>(),
            sp.GetRequiredService<IBillingCapabilityProbe>(),
            TimeProvider.System,
            NullLogger<CapabilityDiscoveryService>.Instance));

        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public WireMockServer Server { get; }

    public Tenant Tenant { get; }

    public InMemoryStore Store { get; }

    public StubTokenProvider Tokens { get; } = new();

    public static Tenant ProvisioningTenant()
    {
        var registered = DateTimeOffset.UtcNow.AddDays(-1);
        var tenant = Tenant.Register(TenantId, "Contoso", null, "westeurope", registered);
        tenant.AwaitConsentVerification(Guid.NewGuid(), registered);
        tenant.ConfirmConsent(Guid.NewGuid(), registered);
        return tenant;
    }

    public async Task<DiscoveryOutcome> DiscoverAsync()
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CapabilityDiscoveryService>()
            .DiscoverAsync(TenantId, CancellationToken.None);
    }

    public async Task<ConsentVerificationResult> VerifyConsentAsync()
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IConsentVerifier>().VerifyAsync(TenantId, CancellationToken.None);
    }

    public static string Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Mlcp.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Could not find the repository root (Mlcp.sln).");
        }

        return File.ReadAllText(Path.Combine(directory.FullName, "tests", "Fixtures", "MicrosoftApi", name));
    }

    /// <summary>Answers <paramref name="method"/> on a wildcard path with a fixture body.</summary>
    public MicrosoftApiHarness Stub(string method, string pathPattern, int status, string? fixture = null, params (string Name, string Value)[] headers)
    {
        var response = Response.Create().WithStatusCode(status);

        if (fixture is not null)
        {
            response = response.WithHeader("Content-Type", "application/json").WithBody(Fixture(fixture));
        }

        foreach (var (name, value) in headers)
        {
            response = response.WithHeader(name, value);
        }

        Server.Given(Request.Create().WithPath(new WildcardMatcher(pathPattern)).UsingMethod(method)).RespondWith(response);
        return this;
    }

    /// <summary>
    /// Answers the first call with <paramref name="first"/> and every later call with
    /// <paramref name="then"/>. A counting callback rather than a WireMock scenario, so the order
    /// is deterministic however the mappings are prioritised.
    /// </summary>
    public MicrosoftApiHarness StubSequence(
        string pathPattern,
        (int Status, string? Fixture, (string Name, string Value)[] Headers) first,
        (int Status, string? Fixture) then)
    {
        var calls = 0;
        var firstBody = first.Fixture is null ? null : Fixture(first.Fixture);
        var thenBody = then.Fixture is null ? null : Fixture(then.Fixture);

        Server.Given(Request.Create().WithPath(new WildcardMatcher(pathPattern)).UsingGet())
            .RespondWith(Response.Create().WithCallback(_ =>
            {
                var isFirst = Interlocked.Increment(ref calls) == 1;
                var message = new ResponseMessage
                {
                    StatusCode = isFirst ? first.Status : then.Status,
                    BodyData = new BodyData
                    {
                        BodyAsString = (isFirst ? firstBody : thenBody) ?? string.Empty,
                        DetectedBodyType = BodyType.String,
                    },
                };

                message.AddHeader("Content-Type", "application/json");

                if (isFirst)
                {
                    foreach (var (name, value) in first.Headers)
                    {
                        message.AddHeader(name, value);
                    }
                }

                return message;
            }));

        return this;
    }

    /// <summary>The standard readable Graph floor.</summary>
    public MicrosoftApiHarness GraphFloor(string directoryFixture = "graph/directorySubscriptions-mca.json")
        => Stub("GET", "/v1.0/subscribedSkus", 200, "graph/subscribedSkus-mca.json")
            .Stub("GET", "/v1.0/directory/subscriptions", 200, directoryFixture)
            .Stub("GET", "/v1.0/organization", 200, "graph/organization-mca.json");

    public MicrosoftApiHarness UsageRefused()
        => Stub("GET", "/v1.0/reports/*", 403, "graph/error-forbidden-403.json");

    public MicrosoftApiHarness NoAzure()
        => Stub("GET", "/subscriptions", 200, "arm/subscriptions-empty-200.json");

    public MicrosoftApiHarness NoBilling()
        => Stub("GET", "/providers/Microsoft.Billing/billingAccounts", 200, "billing/billingAccounts-empty-200.json");

    public IReadOnlyList<IRequestMessage> Requests(string pathContains)
        => Server.LogEntries
            .Select(e => e.RequestMessage)
            .OfType<IRequestMessage>()
            .Where(m => m.Path.Contains(pathContains, StringComparison.Ordinal))
            .ToList();

    public void Dispose()
    {
        _services.Dispose();
        Server.Stop();
        Server.Dispose();
    }
}

/// <summary>An in-memory onboarding store for one tenant.</summary>
public sealed class InMemoryStore : ITenantOnboardingStore
{
    private readonly Dictionary<OnboardingStepName, OnboardingStep> _steps = [];

    public InMemoryStore(Tenant tenant)
    {
        Tenant = tenant;
    }

    public Tenant Tenant { get; }

    public TenantCapabilityProfile? Profile { get; private set; }

    public Task<Tenant?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult<Tenant?>(Tenant.TenantId == tenantId ? Tenant : null);

    public Task<TenantCapabilityProfile?> FindCapabilityProfileAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(Profile);

    public Task AddCapabilityProfileAsync(TenantCapabilityProfile profile, CancellationToken cancellationToken)
    {
        Profile = profile;
        return Task.CompletedTask;
    }

    public Task<OnboardingStep> GetOrCreateStepAsync(Guid tenantId, OnboardingStepName step, CancellationToken cancellationToken)
    {
        if (!_steps.TryGetValue(step, out var existing))
        {
            existing = OnboardingStep.Pending(tenantId, step, DateTimeOffset.UtcNow);
            _steps[step] = existing;
        }

        return Task.FromResult(existing);
    }

    public Task AddAuditAsync(Mlcp.Domain.Audit.AuditLog entry, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Hands out fixed tokens, or a scripted token-endpoint failure per audience.</summary>
public sealed class StubTokenProvider : ITenantTokenProvider
{
    public Dictionary<TokenAudience, TokenAcquisitionException> Failures { get; } = [];

    public Task<string> GetAccessTokenAsync(Guid tenantId, TokenAudience audience, CancellationToken cancellationToken)
        => Failures.TryGetValue(audience, out var failure)
            ? Task.FromException<string>(failure)
            : Task.FromResult($"stub-{audience}");

    public void Evict(Guid tenantId, MicrosoftApp app, TokenAudience audience)
    {
    }
}
