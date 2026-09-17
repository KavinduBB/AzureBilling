using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Tenancy;
using Mlcp.Shared.Tenancy;
using Mlcp.UnitTests.Onboarding;
using Mlcp.Web.Infrastructure;
using Mlcp.Web.Models;
using Mlcp.Web.Services;

namespace Mlcp.UnitTests.Web;

/// <summary>The work done inside the OpenID Connect handler at every interactive sign-in.</summary>
public sealed class SignInEventsTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid AdminId = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private readonly OnboardingHarness _harness = new();
    private readonly ServiceProvider _services;

    public SignInEventsTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_harness.Clock);
        services.AddSingleton(new DeploymentOptions("eu"));
        services.AddSingleton<IRegionDirectory>(_harness.Regions);
        services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
        services.AddSingleton(new MlcpWebOptions
        {
            RegionHosts = new Dictionary<string, string> { ["us"] = "us.app.example.com" },
        });
        services.AddScoped<TenantContext>();
        services.AddSingleton(_harness.Service);
        _services = services.BuildServiceProvider();
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task Signing_in_records_the_user_and_derives_owner_from_wids()
    {
        var context = Context(admin: true);

        await SignInEvents.OnTokenValidatedAsync(context);

        context.Result.Should().BeNull("sign-in continues");
        _harness.User(AdminId).Role.Should().Be(AppRole.Owner);
        _harness.Tenant(TenantA).Status.Should().Be(TenantStatus.NotConnected);
        context.HttpContext.RequestServices.GetRequiredService<TenantContext>().TenantId
            .Should().Be(TenantA, "the scope is bound to the validated tid");
    }

    [Fact]
    public async Task Signing_in_without_the_admin_role_downgrades_a_former_owner()
    {
        await SignInEvents.OnTokenValidatedAsync(Context(admin: true));

        await SignInEvents.OnTokenValidatedAsync(Context(admin: false));

        _harness.User(AdminId).Role.Should().Be(AppRole.Viewer);
    }

    [Fact]
    public async Task A_tenant_that_lives_in_another_region_is_sent_there_before_any_cookie_is_issued()
    {
        _harness.Regions.Regions[TenantA] = "us";
        var context = Context(admin: true);

        await SignInEvents.OnTokenValidatedAsync(context);

        context.Result!.Handled.Should().BeTrue();
        context.Response.Headers.Location.ToString().Should().Be("https://us.app.example.com/");
        _harness.Repository.Users.Should().BeEmpty("nothing is written in the wrong region");
    }

    [Fact]
    public async Task A_tenant_in_a_region_with_no_host_fails_the_sign_in()
    {
        _harness.Regions.Regions[TenantA] = "apac";
        var context = Context(admin: true);

        await SignInEvents.OnTokenValidatedAsync(context);

        context.Result!.Failure.Should().NotBeNull();
        _harness.Repository.Users.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sign_in_that_answered_a_forced_reauthentication_is_marked_recent()
    {
        var properties = RecentAuthentication.ChallengeProperties("/onboarding/disconnect");
        var context = Context(admin: true, properties);

        await SignInEvents.OnTokenValidatedAsync(context);

        RecentAuthentication.IsRecent(context.Principal, _harness.Clock.GetUtcNow()).Should().BeTrue();
    }

    [Fact]
    public async Task An_ordinary_sign_in_is_not_marked_as_a_reauthentication()
    {
        var context = Context(admin: true);

        await SignInEvents.OnTokenValidatedAsync(context);

        context.Principal!.FindFirst(RecentAuthentication.ReauthenticatedAtClaim).Should().BeNull();
    }

    [Fact]
    public void The_logging_email_sender_refuses_to_exist_outside_development()
    {
        var act = () => new LoggingConsentEmailSender(
            new FakeEnvironment("Production"),
            NullLogger<LoggingConsentEmailSender>.Instance);

        act.Should().Throw<InvalidOperationException>();

        new LoggingConsentEmailSender(new FakeEnvironment("Development"), NullLogger<LoggingConsentEmailSender>.Instance)
            .Should().NotBeNull();
    }

    private TokenValidatedContext Context(bool admin, AuthenticationProperties? properties = null)
    {
        var claims = new List<Claim>
        {
            new("tid", TenantA.ToString()),
            new("oid", AdminId.ToString()),
            new("name", "Adam Admin"),
            new("preferred_username", "admin@contoso.example"),
        };

        if (admin)
        {
            claims.Add(new Claim("wids", AdminRoles.PrivilegedRoleAdministrator.ToString()));
        }

        var http = new DefaultHttpContext { RequestServices = _services.CreateScope().ServiceProvider };

        return new TokenValidatedContext(
            http,
            new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(),
            new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc")),
            properties ?? new AuthenticationProperties());
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Mlcp.Web";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
