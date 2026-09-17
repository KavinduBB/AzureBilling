using System.Globalization;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.TokenCacheProviders.Distributed;
using Microsoft.Identity.Web.UI;
using Mlcp.Application;
using Mlcp.Application.Onboarding;
using Mlcp.Integration.Azure;
using Mlcp.Integration.Azure.Messaging;
using Mlcp.Integration.Azure.Regions;
using Mlcp.Integration.Graph;
using Mlcp.Persistence;
using Mlcp.Shared;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Logging;
using Mlcp.Shared.Resilience;
using Mlcp.Web.Infrastructure;
using Mlcp.Web.Models;
using Mlcp.Web.Services;
using Serilog;
using Serilog.Events;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();

// ---------------------------------------------------------------------------------------------
// Configuration. In Azure, secrets come from Key Vault through the app's Managed Identity, so
// nothing sensitive exists in app settings or environment variables (CLAUDE.md rule 4).
// ---------------------------------------------------------------------------------------------
var keyVaultUri = builder.Configuration["Mlcp:KeyVaultUri"];

if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

// Outside Development every silent fallback below is a deployment bug, so the host refuses to
// start and names what is missing.
if (!isDevelopment)
{
    StartupRequirements.ThrowIfAny(StartupRequirements.FindProblems(builder.Configuration));
}

// Redaction (properties, message templates and exceptions) is built into the shared pipeline,
// so no sink, whether configured here or in appsettings, can receive a secret (rule 14).
builder.Services.AddMlcpSerilog(builder.Configuration);

var deployment = new DeploymentOptions(builder.Configuration["Mlcp:Region"] ?? DeploymentOptions.Default.Region);
builder.Services.AddSingleton(deployment);

var webOptions = MlcpWebOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(webOptions);
builder.Services.AddSingleton<RegionPicker>();

var clientId = builder.Configuration["AzureAd:ClientId"];

// Outside Development this refuses to start when either app registration or the certificate is
// missing. Interactive budget: a long Retry-After fails fast instead of holding a request.
builder.Services.AddMlcpShared(
    builder.Configuration,
    builder.Environment.IsDevelopment(),
    MicrosoftCallBudget.Interactive);
builder.Services.AddMlcpApplication();

// The database is not optional. Migrations use the design-time factory and never need the
// running host, so there is nothing to gain from starting without one.
var connectionString = builder.Configuration.GetConnectionString("MlcpDatabase")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:MlcpDatabase is required. Set it in Key Vault, or locally with "
        + "dotnet user-secrets set \"ConnectionStrings:MlcpDatabase\" \"...\" -p src/Mlcp.Web.");

builder.Services.AddMlcpPersistence(connectionString);

builder.Services.AddGraphIntegration(builder.Configuration);
builder.Services.AddAzureIntegration(builder.Configuration);

// The web host only sends: discovery, consent verification and re-consent probes run in the
// worker (CLAUDE.md rule 5). The same sender implementation serves both hosts.
builder.Services.AddMlcpServiceBusEnqueuer(builder.Configuration);

// Global tenant → region directory (ADR-021). In-memory only in Development, where a single
// region is assumed; StartupRequirements insists on the table everywhere else.
builder.Services.AddRegionDirectory(
    Uri.TryCreate(builder.Configuration["Mlcp:Regions:DirectoryTableUri"], UriKind.Absolute, out var regionTable)
        ? regionTable
        : null);

builder.Services.AddMemoryCache();

// ---------------------------------------------------------------------------------------------
// Forwarded headers. Container Apps and Front Door terminate TLS, so scheme and host arrive in
// X-Forwarded-*. The proxy addresses are not stable, so the known-proxy lists are cleared; that
// is why nothing security-relevant is built from the request host (emailed links use
// Mlcp:PublicBaseUrl, and AllowedHosts filters the Host header).
// ---------------------------------------------------------------------------------------------
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// ---------------------------------------------------------------------------------------------
// Redis: the MSAL token cache, the consent nonces and the read cache share it. No user token
// ever reaches SQL (CLAUDE.md rule 3).
// ---------------------------------------------------------------------------------------------
var redisConnectionString = builder.Configuration.GetConnectionString("Redis");

if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    var redis = new Lazy<Task<IConnectionMultiplexer>>(
        async () => await ConnectionMultiplexer.ConnectAsync(redisConnectionString).ConfigureAwait(false));

    builder.Services.AddSingleton(redis);
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.ConnectionMultiplexerFactory = () => redis.Value;
        options.InstanceName = "mlcp:";
    });
}
else
{
    // Development only (enforced above): sessions and nonces do not survive a restart.
    builder.Services.AddDistributedMemoryCache();
}

// ---------------------------------------------------------------------------------------------
// Data Protection. Keys back the auth cookie, the MSAL token cache encryption and the consent
// state, so they must be shared across instances and survive a restart. In Azure they live in
// Blob Storage and are wrapped with a Key Vault key; Development uses the local key ring.
// ---------------------------------------------------------------------------------------------
var dataProtectionBlobUri = builder.Configuration["Mlcp:DataProtection:BlobUri"];
var dataProtectionKeyUri = builder.Configuration["Mlcp:DataProtection:KeyUri"];

if (!string.IsNullOrWhiteSpace(dataProtectionBlobUri) && !string.IsNullOrWhiteSpace(dataProtectionKeyUri))
{
    builder.Services
        .AddDataProtection()
        .SetApplicationName("Mlcp")
        .PersistKeysToAzureBlobStorage(new Uri(dataProtectionBlobUri), new DefaultAzureCredential())
        .ProtectKeysWithAzureKeyVault(new Uri(dataProtectionKeyUri), new DefaultAzureCredential());
}
else
{
    builder.Services.AddDataProtection().SetApplicationName("Mlcp");
}

// ---------------------------------------------------------------------------------------------
// Entra sign-in. The app is registered AzureADMultipleOrgs, so the authority is /organizations
// and the tenant is whichever one the user signs in from. The validated tid claim is the only
// thing that ever selects a tenant's data (CLAUDE.md rule 2).
//
// The sign-in credential comes from AzureAd:ClientCredentials (a Key Vault certificate in Azure,
// ADR-026; a client secret is acceptable in Development). Token acquisition is enabled only so
// the authorization code can be redeemed; no downstream API is called with user tokens in
// Phase 0. The distributed token cache is encrypted with the Data Protection keys (rule 3).
// ---------------------------------------------------------------------------------------------
if (!string.IsNullOrWhiteSpace(clientId))
{
    builder.Services
        .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
        .EnableTokenAcquisitionToCallDownstreamApi()
        .AddDistributedTokenCaches();

    builder.Services.Configure<MsalDistributedTokenCacheAdapterOptions>(options => options.Encrypt = true);

    builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
    {
        // Keep the short claim names: wids (ADR-018), roles (ADR-023), tid, oid, auth_time.
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "roles";
        options.ClaimActions.Remove(RecentAuthentication.AuthTimeClaim);

        SignInEvents.Attach(options);
    });
}
else
{
    // Development without an app registration: the host starts, and every protected page
    // challenges with nothing to answer it.
    builder.Services.AddAuthentication();
}

// The session cookie. Lax, not Strict: the OIDC response and the regional redirect are
// cross-site navigations, and a Strict cookie would be missing on the first request after them.
builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

// Every endpoint requires a signed-in user unless it opts out with [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddSingleton(new AdminConsentOptions
{
    ClientId = clientId ?? string.Empty,
    UsageInsightsClientId = webOptions.UsageInsightsClientId ?? string.Empty,
});
builder.Services.AddSingleton<AdminConsentUrlBuilder>();
builder.Services.AddSingleton<ConsentStateProtector>();
builder.Services.AddMlcpRateLimiting();

// ---------------------------------------------------------------------------------------------
// Outbound email for admin consent requests. A real sender everywhere except Development.
// ---------------------------------------------------------------------------------------------
var emailOptions = new EmailOptions
{
    CommunicationServiceEndpoint = builder.Configuration["Mlcp:Email:Endpoint"] is { Length: > 0 } endpoint
        ? new Uri(endpoint)
        : null,
    SenderAddress = builder.Configuration["Mlcp:Email:SenderAddress"],
};

builder.Services.AddSingleton(emailOptions);

if (emailOptions.IsConfigured)
{
    builder.Services.AddSingleton<IConsentEmailSender, AzureCommunicationConsentEmailSender>();
}
else
{
    builder.Services.AddSingleton<IConsentEmailSender, LoggingConsentEmailSender>();
}

builder.Services
    .AddControllersWithViews(options =>
    {
        options.Filters.Add<TenantScopedAttribute>();
        options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    })
    .AddMicrosoftIdentityUI();

builder.Services.AddRazorPages();

// /health/live is process-only; /health/ready gates traffic on SQL and Redis (ADR-026).
var healthChecks = builder.Services.AddHealthChecks()
    .AddDbContextCheck<MlcpDbContext>(tags: [HealthCheckTags.Ready]);

if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    healthChecks.AddCheck<RedisHealthCheck>("redis", tags: [HealthCheckTags.Ready]);
}

if (!isDevelopment)
{
    builder.Services.AddHsts(options =>
    {
        options.MaxAge = TimeSpan.FromDays(365);
        options.IncludeSubDomains = true;
    });
}

builder.Host.UseDefaultServiceProvider((_, options) =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

var app = builder.Build();

// Forwarded headers first, so everything after sees the client's scheme and host.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseSerilogRequestLogging(options =>
{
    // Log the route template, not the raw path: /onboarding/consent/{token} carries a live
    // token that must not reach the log stream.
    options.GetMessageTemplateProperties = (context, requestPath, elapsed, statusCode) =>
    [
        new LogEventProperty("RequestMethod", new ScalarValue(context.Request.Method)),
        new LogEventProperty("RequestPath", new ScalarValue(RequestLogPath.For(context))),
        new LogEventProperty("StatusCode", new ScalarValue(statusCode)),
        new LogEventProperty("Elapsed", new ScalarValue(elapsed)),
    ];
});

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self' https://login.microsoftonline.com";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();

// After authentication, before anything that reads tenant data: a tenant that lives in another
// region is sent there before this stack touches its rows (ADR-021).
app.UseRegionRouting();

// Must follow authentication so the principal exists, and precede anything touching the
// database so no query runs before the tenant is bound.
app.UseTenantResolution();

app.UseAuthorization();
app.UseRateLimiter();

app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapRazorPages();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(HealthCheckTags.Ready) })
    .AllowAnonymous();

// Kept for the current container probes (infra/modules/apps.bicep); same as /health/live.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();

await app.RunAsync();

/// <summary>
/// Exposed so <c>WebApplicationFactory</c> in the integration tests can boot this host.
/// </summary>
public partial class Program
{
    protected Program()
    {
    }
}
