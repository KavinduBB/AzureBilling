using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mlcp.Application.Onboarding;
using Mlcp.Application.Sync;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;
using Mlcp.Web.Infrastructure;

namespace Mlcp.IntegrationTests.Web;

/// <summary>
/// Boots the real web host with Microsoft and the database replaced by in-memory fakes, and a
/// test authentication scheme that turns a request header into validated-looking claims.
/// </summary>
/// <remarks>
/// These tests exercise the HTTP surface — authorization, antiforgery, the tenant-scope filter,
/// the consent state and the controllers — so they need no SQL Server and no Docker. Row-level
/// security is covered by the SQL-backed suites in <c>TenantIsolation/</c>.
/// </remarks>
public sealed class MlcpWebFactory : WebApplicationFactory<Program>
{
    public const string ThisRegion = "eu";

    public const string AntiforgeryPath = AntiforgeryTokenEndpoint.Path;

    public InMemoryWebRepository Repository { get; } = new();

    public CountingConsentVerifier Verifier { get; } = new();

    public RecordingEnqueuer Jobs { get; } = new();

    public TestRegionDirectory Regions { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        // Read by Program before the host is built, so they must be host settings.
        builder.UseSetting("ConnectionStrings:MlcpDatabase", "Server=unused.invalid;Database=Unused;Encrypt=True");
        builder.UseSetting("ConnectionStrings:Redis", string.Empty);
        builder.UseSetting("AzureAd:ClientId", string.Empty);
        builder.UseSetting("Mlcp:Region", ThisRegion);
        builder.UseSetting("Mlcp:PublicBaseUrl", "https://eu.app.example.com/");
        builder.UseSetting("Mlcp:Regions:Hosts:eu", "eu.app.example.com");
        builder.UseSetting("Mlcp:Regions:Hosts:us", "us.app.example.com");

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();

            services.RemoveAll<IOnboardingRepository>();
            services.AddSingleton<IOnboardingRepository>(Repository);

            services.RemoveAll<ITenantOnboardingStore>();
            services.AddSingleton<ITenantOnboardingStore>(new NoProfileStore(Repository));

            services.RemoveAll<ITenantDirectoryInfo>();
            services.AddSingleton<ITenantDirectoryInfo, UnknownTenantDirectoryInfo>();

            services.RemoveAll<IConsentVerifier>();
            services.AddSingleton<IConsentVerifier>(Verifier);

            services.RemoveAll<ISyncJobEnqueuer>();
            services.AddSingleton<ISyncJobEnqueuer>(Jobs);

            services.RemoveAll<IRegionDirectory>();
            services.AddSingleton<IRegionDirectory>(Regions);

            services.RemoveAll<IConsentEmailSender>();
            services.AddSingleton<IConsentEmailSender, DiscardingEmailSender>();

            services.RemoveAll<AdminConsentOptions>();
            services.AddSingleton(new AdminConsentOptions { ClientId = "11111111-1111-1111-1111-111111111111" });

            services.AddTransient<IStartupFilter, AntiforgeryTokenEndpoint>();
        });
    }

    /// <summary>
    /// A test-only endpoint that issues an antiforgery token for the signed-in test user, for
    /// pages whose current state renders no form.
    /// </summary>
    private sealed class AntiforgeryTokenEndpoint : IStartupFilter
    {
        public const string Path = "/test/antiforgery";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                // Not app.Map: that would set PathBase, and the antiforgery cookie would be scoped
                // to this path and never sent to the real endpoints.
                app.Use(async (context, nextMiddleware) =>
                {
                    if (!context.Request.Path.Equals(Path, StringComparison.OrdinalIgnoreCase))
                    {
                        await nextMiddleware(context);
                        return;
                    }

                    var result = await context.AuthenticateAsync(TestAuthHandler.SchemeName);

                    if (result.Succeeded)
                    {
                        context.User = result.Principal;
                    }

                    var tokens = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
                    await context.Response.WriteAsync(tokens.RequestToken!);
                });

                next(app);
            };
    }

    /// <summary>A client that keeps cookies and does not follow redirects.</summary>
    public HttpClient CreateUserClient(TestUser? user)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("https://eu.app.example.com/"),
        });

        if (user is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.Header, user.ToHeader());
        }

        return client;
    }

    /// <summary>Registers a tenant that has verified consent.</summary>
    public Tenant SeedConnectedTenant(Guid tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = Tenant.Register(tenantId, "Contoso", "contoso.example", ThisRegion, now);
        tenant.ConfirmConsent(Guid.NewGuid(), now);
        tenant.Activate(now);
        Repository.Tenants[tenantId] = tenant;
        return tenant;
    }
}

/// <summary>Who the test request is signed in as.</summary>
public sealed record TestUser(Guid TenantId, Guid ObjectId, bool IsAdmin, DateTimeOffset? AuthTime = null)
{
    public static TestUser NewAdmin(Guid tenantId, DateTimeOffset? authTime = null) => new(tenantId, Guid.NewGuid(), true, authTime);

    public static TestUser NewMember(Guid tenantId) => new(tenantId, Guid.NewGuid(), false);

    public string ToHeader()
        => string.Join(
            '|',
            TenantId.ToString(),
            ObjectId.ToString(),
            IsAdmin ? "admin" : "member",
            AuthTime?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? string.Empty);
}

/// <summary>
/// Stands in for OpenID Connect: the header describes the claims a validated ID token would
/// carry. Challenge redirects to a marker path so tests can tell a challenge from a 401.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string Header = "X-Test-User";
    public const string ChallengePath = "/test-challenge";

    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var parts = value.ToString().Split('|');

        var claims = new List<Claim>
        {
            new("tid", parts[0]),
            new("oid", parts[1]),
            new("name", "Test User"),
            new("preferred_username", $"user-{parts[1][..8]}@contoso.example"),
        };

        if (parts[2] == "admin")
        {
            claims.Add(new Claim("wids", AdminRoles.GlobalAdministrator.ToString()));
        }

        if (parts.Length > 3 && parts[3].Length > 0)
        {
            claims.Add(new Claim("auth_time", parts[3]));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, "name", "roles"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var prompt = properties.Parameters.TryGetValue("prompt", out var p) ? p?.ToString() : null;
        Response.Redirect(prompt is null ? ChallengePath : $"{ChallengePath}?prompt={prompt}");
        return Task.CompletedTask;
    }
}

public sealed class InMemoryWebRepository : IOnboardingRepository
{
    public ConcurrentDictionary<Guid, Tenant> Tenants { get; } = new();

    public ConcurrentBag<AppUser> Users { get; } = [];

    public ConcurrentBag<PendingConsentRequest> Requests { get; } = [];

    public ConcurrentBag<AuditLog> Audits { get; } = [];

    public Task<Tenant?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(Tenants.TryGetValue(tenantId, out var tenant) ? tenant : null);

    public Task AddTenantAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        Tenants.TryAdd(tenant.TenantId, tenant);
        return Task.CompletedTask;
    }

    public Task<AppUser?> FindAppUserAsync(Guid tenantId, Guid entraObjectId, CancellationToken cancellationToken)
        => Task.FromResult(Users.FirstOrDefault(u => u.TenantId == tenantId && u.EntraObjectId == entraObjectId));

    public Task AddAppUserAsync(AppUser user, CancellationToken cancellationToken)
    {
        Users.Add(user);
        return Task.CompletedTask;
    }

    public Task<PendingConsentRequest?> FindOpenConsentRequestAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(Requests.FirstOrDefault(r => r.TenantId == tenantId && r.IsUsable(DateTimeOffset.UtcNow)));

    public Task<PendingConsentRequest?> FindOpenConsentRequestAsync(
        Guid tenantId,
        Guid requestedByObjectId,
        string sentToEmail,
        CancellationToken cancellationToken)
        => Task.FromResult(Requests.FirstOrDefault(r =>
            r.TenantId == tenantId && r.RequestedByObjectId == requestedByObjectId && r.IsAddressedTo(sentToEmail)));

    public Task<IReadOnlyList<PendingConsentRequest>> ListConsentRequestsSentSinceAsync(
        Guid tenantId,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PendingConsentRequest>>(
            [.. Requests.Where(r => r.TenantId == tenantId && r.LastSentUtc >= sinceUtc)]);

    public Task<PendingConsentRequest?> FindConsentRequestByTokenAsync(string token, CancellationToken cancellationToken)
        => Task.FromResult(Requests.FirstOrDefault(r => r.Token == token));

    public Task AddConsentRequestAsync(PendingConsentRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.CompletedTask;
    }

    public Task AddAuditAsync(AuditLog entry, CancellationToken cancellationToken)
    {
        Audits.Add(entry);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class NoProfileStore(InMemoryWebRepository repository) : ITenantOnboardingStore
{
    public Task<Tenant?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken)
        => repository.FindTenantAsync(tenantId, cancellationToken);

    public Task<TenantCapabilityProfile?> FindCapabilityProfileAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult<TenantCapabilityProfile?>(null);

    public Task AddCapabilityProfileAsync(TenantCapabilityProfile profile, CancellationToken cancellationToken)
        => throw new NotSupportedException("Discovery never runs in a request.");

    public Task<OnboardingStep> GetOrCreateStepAsync(Guid tenantId, OnboardingStepName step, CancellationToken cancellationToken)
        => throw new NotSupportedException("Discovery never runs in a request.");

    public Task AddAuditAsync(AuditLog entry, CancellationToken cancellationToken)
        => repository.AddAuditAsync(entry, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class CountingConsentVerifier : IConsentVerifier
{
    private readonly ConcurrentDictionary<Guid, int> _calls = new();

    public int CallsFor(Guid tenantId) => _calls.TryGetValue(tenantId, out var count) ? count : 0;

    public Task<ConsentVerificationResult> VerifyAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        _calls.AddOrUpdate(tenantId, 1, (_, count) => count + 1);
        return Task.FromResult(new ConsentVerificationResult(ConsentVerificationStatus.Verified));
    }
}

public sealed class RecordingEnqueuer : ISyncJobEnqueuer
{
    public ConcurrentBag<(Guid TenantId, SyncJobType JobType)> Jobs { get; } = [];

    public Task EnqueueAsync(
        Guid tenantId,
        SyncJobType jobType,
        string deduplicationKey,
        DateTimeOffset? notBeforeUtc,
        string correlationId,
        CancellationToken cancellationToken)
    {
        Jobs.Add((tenantId, jobType));
        return Task.CompletedTask;
    }
}

public sealed class TestRegionDirectory : IRegionDirectory
{
    public ConcurrentDictionary<Guid, string> Regions { get; } = new();

    public Task<string?> GetRegionAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(Regions.TryGetValue(tenantId, out var region) ? region : null);

    public Task<RegionRegistration> TryRegisterAsync(Guid tenantId, string region, CancellationToken cancellationToken)
    {
        var added = Regions.TryAdd(tenantId, region);
        return Task.FromResult(new RegionRegistration(Regions[tenantId], added));
    }
}

public sealed class DiscardingEmailSender : IConsentEmailSender
{
    public Task SendConsentRequestAsync(PendingConsentRequest request, string consentLandingUrl, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
