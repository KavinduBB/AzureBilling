using System.Globalization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Mlcp.Persistence;
using Mlcp.Shared;
using Mlcp.Web.Infrastructure;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()

    // Redaction is a pipeline stage, not a call-site discipline: a bearer token or SAS URL
    // reaching a sink because one developer forgot is not an acceptable failure mode.
    .Enrich.With<Mlcp.Shared.Logging.RedactionEnricher>()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

builder.Services.AddMlcpShared();

var connectionString = builder.Configuration.GetConnectionString("MlcpDatabase");

if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddMlcpPersistence(connectionString);
}

// Entra sign-in. The app is registered AzureADMultipleOrgs, so the authority is
// /organizations and the tenant is whichever one the user signs in from; the validated tid
// claim is the only thing that ever selects a tenant's data.
var clientId = builder.Configuration["AzureAd:ClientId"];

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
        // MSAL token cache and read cache share one Redis. No token ever reaches SQL
        // (CLAUDE.md rule 3).
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redis;
            options.InstanceName = "mlcp:";
        });
    }
    else
    {
        builder.Services.AddDistributedMemoryCache();
    }
}
else
{
    // No app registration configured. The host still starts so that a developer can run
    // migrations and health checks, but every authenticated path will challenge and fail.
    builder.Services.AddAuthentication();
}

builder.Services.AddAuthorization();

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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseSerilogRequestLogging();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
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
app.MapHealthChecks("/health");

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
