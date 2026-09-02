using System.Globalization;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Mlcp.Application;
using Mlcp.Application.Onboarding;
using Mlcp.Integration.Azure;
using Mlcp.Integration.Graph;
using Mlcp.Persistence;
using Mlcp.Shared;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Logging;
using Mlcp.Web.Infrastructure;
using Mlcp.Web.Models;
using Mlcp.Web.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------------
// Configuration. In Azure, secrets come from Key Vault through the app's Managed Identity, so
// nothing sensitive exists in app settings or environment variables (CLAUDE.md rule 4).
// ---------------------------------------------------------------------------------------------
var keyVaultUri = builder.Configuration["Mlcp:KeyVaultUri"];

if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()

    // Redaction is a pipeline stage, not a call-site discipline: a bearer token or SAS URL
    // reaching a sink because one developer forgot is not an acceptable failure mode.
    .Enrich.With<RedactionEnricher>()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

var deployment = new DeploymentOptions(builder.Configuration["Mlcp:Region"] ?? DeploymentOptions.Default.Region);
builder.Services.AddSingleton(deployment);

var clientId = builder.Configuration["AzureAd:ClientId"];

var identityOptions = new MlcpIdentityOptions
{
    ClientId = clientId ?? string.Empty,
    CertificateName = builder.Configuration["Mlcp:ClientCertificateName"],
    KeyVaultUri = string.IsNullOrWhiteSpace(keyVaultUri) ? null : new Uri(keyVaultUri),
    ClientSecret = builder.Configuration["AzureAd:ClientSecret"],
};

builder.Services.AddMlcpShared(string.IsNullOrWhiteSpace(clientId) ? null : identityOptions);
builder.Services.AddMlcpApplication();

// The database is not optional. An earlier draft registered it conditionally so the host would
// still start without one; all that produced was a confusing dependency-injection failure at the
// first request instead of a clear message at startup. Migrations use the design-time factory
// and never need the running host, so there is nothing to gain from starting without a database.
var connectionString = builder.Configuration.GetConnectionString("MlcpDatabase")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:MlcpDatabase is required. Set it in Key Vault, or locally with "
        + "dotnet user-secrets set \"ConnectionStrings:MlcpDatabase\" \"...\" -p src/Mlcp.Web.");

builder.Services.AddMlcpPersistence(connectionString);

builder.Services.AddGraphIntegration();
builder.Services.AddAzureIntegration();

// ---------------------------------------------------------------------------------------------
// Data Protection. Keys back the auth cookie, the MSAL token cache and the consent state, so
// they must be shared across instances and survive a restart. In Azure they live in Blob
// Storage and are encrypted with a Key Vault key; locally the default on-disk store is used.
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
// ---------------------------------------------------------------------------------------------
if (!string.IsNullOrWhiteSpace(clientId))
{
    builder.Services
        .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
        .EnableTokenAcquisitionToCallDownstreamApi(["User.Read"])
        .AddDistributedTokenCaches();

    var redis = builder.Configuration.GetConnectionString("Redis");

    if (!string.IsNullOrWhiteSpace(redis))
    {
        // MSAL token cache and read cache share one Redis. No user token ever reaches SQL
        // (CLAUDE.md rule 3).
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redis;
            options.InstanceName = "mlcp:";
        });
    }
    else
    {
        // Single-instance fallback so a developer can sign in without running Redis. Sessions
        // do not survive a restart and do not work behind more than one instance.
        builder.Services.AddDistributedMemoryCache();
    }
}
else
{
    // No app registration configured. The host still starts so migrations and health checks
    // work, but every authenticated path will challenge and fail.
    builder.Services.AddAuthentication();
}

builder.Services.AddAuthorization();

builder.Services.AddSingleton(new AdminConsentOptions { ClientId = clientId ?? string.Empty });
builder.Services.AddSingleton<AdminConsentUrlBuilder>();

// ---------------------------------------------------------------------------------------------
// Outbound email for admin consent requests.
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

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

builder.Services
    .AddControllersWithViews(options => options.Filters.Add<TenantScopedAttribute>())
    .AddMicrosoftIdentityUI();

builder.Services.AddRazorPages();
builder.Services.AddHealthChecks();

builder.Host.UseDefaultServiceProvider((_, options) =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

var app = builder.Build();

if (emailOptions.IsConfigured is false && !app.Environment.IsDevelopment())
{
    app.Logger.LogWarning(
        "No email sender is configured. Consent request links will be written to the log instead of being sent, "
        + "which writes a live token to the log stream. Configure Mlcp:Email before serving customers.");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();

    // Azure Container Apps and App Service terminate TLS at the edge, so the scheme and client
    // address arrive in forwarded headers. Without this, redirect URIs are built as http and
    // the OIDC flow breaks.
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
    });
}

app.UseSerilogRequestLogging();

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

// Must follow authentication so the principal exists, and precede anything touching the
// database so no query runs before the tenant is bound.
app.UseTenantResolution();

app.UseAuthorization();

app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapRazorPages();
app.MapHealthChecks("/health").AllowAnonymous();

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
