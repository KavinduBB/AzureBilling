using System.Globalization;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Mlcp.Application.Onboarding;
using Mlcp.Web.Infrastructure;

namespace Mlcp.UnitTests.Web;

public class WebHelpersTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal Principal(params Claim[] claims)
        => new(new ClaimsIdentity(
            [new Claim("tid", Guid.NewGuid().ToString()), new Claim("oid", Guid.NewGuid().ToString()), .. claims],
            authenticationType: "Test"));

    // ------------------------------------------------------------ ADR-018 owner derivation

    [Theory]
    [InlineData("62e90394-69f5-4237-9190-012177145e10", true)]
    [InlineData("E8611AB8-C189-46E8-94E1-60213AB1F814", true)]
    [InlineData("fdd7a751-b60b-444a-984c-02652fe8fa1c", false)] // Groups Administrator
    [InlineData("not-a-guid", false)]
    public void Only_roles_that_can_grant_tenant_wide_consent_make_an_owner(string wid, bool expected)
    {
        var principal = Principal(new Claim("wids", wid));

        principal.IsDirectoryAdmin().Should().Be(expected);
        principal.TryGetSignedInUser(out var user).Should().BeTrue();
        user.IsDirectoryAdmin.Should().Be(expected);
    }

    [Fact]
    public void Any_one_admin_role_among_several_is_enough()
    {
        var principal = Principal(
            new Claim("wids", "b79fbf4d-3ef9-4689-8143-76b194e85509"),
            new Claim("wids", AdminRoles.GlobalAdministrator.ToString()));

        principal.IsDirectoryAdmin().Should().BeTrue();
    }

    [Fact]
    public void No_wids_claim_means_not_an_admin()
    {
        Principal().IsDirectoryAdmin().Should().BeFalse();
        ((ClaimsPrincipal?)null).IsDirectoryAdmin().Should().BeFalse();
    }

    // ------------------------------------------------------------ ADR-023

    [Fact]
    public void The_subscription_manager_app_role_is_read_from_the_roles_claim()
    {
        Principal(new Claim("roles", "SubscriptionManager")).HasSubscriptionManagerRole().Should().BeTrue();
        Principal(new Claim(ClaimTypes.Role, "SubscriptionManager")).HasSubscriptionManagerRole().Should().BeTrue();
    }

    [Theory]
    [InlineData("subscriptionmanager")]
    [InlineData("Owner")]
    public void Anything_else_is_not_the_subscription_manager_role(string role)
    {
        Principal(new Claim("roles", role)).HasSubscriptionManagerRole().Should().BeFalse();
    }

    [Fact]
    public void Being_a_directory_admin_does_not_imply_subscription_manager()
    {
        // Separation of duties (ADR-023).
        Principal(new Claim("wids", AdminRoles.GlobalAdministrator.ToString())).HasSubscriptionManagerRole().Should().BeFalse();
    }

    // ------------------------------------------------------------ recent authentication

    [Fact]
    public void An_auth_time_within_fifteen_minutes_is_recent()
    {
        var principal = Principal(new Claim("auth_time", Unix(Now.AddMinutes(-14))));

        RecentAuthentication.IsRecent(principal, Now).Should().BeTrue();
    }

    [Fact]
    public void An_older_auth_time_is_not_recent()
    {
        var principal = Principal(new Claim("auth_time", Unix(Now.AddMinutes(-16))));

        RecentAuthentication.IsRecent(principal, Now).Should().BeFalse();
    }

    [Fact]
    public void A_forced_reauthentication_counts_when_auth_time_is_absent_or_older()
    {
        var principal = Principal(
            new Claim("auth_time", Unix(Now.AddHours(-3))),
            new Claim(RecentAuthentication.ReauthenticatedAtClaim, Unix(Now.AddMinutes(-2))));

        RecentAuthentication.IsRecent(principal, Now).Should().BeTrue();
        RecentAuthentication.IsRecent(Principal(), Now).Should().BeFalse("no evidence is not recent");
    }

    [Fact]
    public void The_step_up_challenge_forces_a_fresh_sign_in()
    {
        var properties = RecentAuthentication.ChallengeProperties("/onboarding/disconnect");

        properties.Prompt.Should().Be("login");
        properties.MaxAge.Should().Be(TimeSpan.FromMinutes(15));
        properties.RedirectUri.Should().Be("/onboarding/disconnect");
        properties.Items.Should().ContainKey(RecentAuthentication.ForcedReauthenticationItem);
    }

    // ------------------------------------------------------------ consent URLs

    [Fact]
    public void The_admin_consent_url_targets_the_callers_tenant_and_escapes_every_value()
    {
        var tenant = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
        var builder = new AdminConsentUrlBuilder(new AdminConsentOptions { ClientId = "core-id", UsageInsightsClientId = "usage-id" });

        var url = builder.Build(tenant, "https://eu.app.example.com/onboarding/consent-callback", "a+b/c=");

        url.Should().StartWith($"https://login.microsoftonline.com/{tenant}/v2.0/adminconsent?client_id=core-id&");
        url.Should().Contain("scope=https%3A%2F%2Fgraph.microsoft.com%2F.default");
        url.Should().Contain("redirect_uri=https%3A%2F%2Feu.app.example.com%2Fonboarding%2Fconsent-callback");
        url.Should().EndWith("state=a%2Bb%2Fc%3D");

        builder.BuildUsageInsights(tenant, "https://x.example/cb", "s").Should().Contain("client_id=usage-id");
    }

    [Fact]
    public void Usage_insights_consent_needs_its_own_registration()
    {
        var builder = new AdminConsentUrlBuilder(new AdminConsentOptions { ClientId = "core-id" });

        builder.HasUsageInsights.Should().BeFalse();
        var act = () => builder.BuildUsageInsights(Guid.NewGuid(), "https://x.example/cb", "s");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_deploy_to_azure_link_encodes_the_raw_template_url()
    {
        var options = new MlcpWebOptions
        {
            CustomerRbacTemplateUrl = new Uri("https://raw.example.com/mlcp/customer-rbac.json"),
        };

        options.DeployToAzureUrl.Should().Be(
            "https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.example.com%2Fmlcp%2Fcustomer-rbac.json");
        new MlcpWebOptions().DeployToAzureUrl.Should().BeNull();
    }

    [Theory]
    [InlineData("us.app.example.com", "/onboarding?x=1", "https://us.app.example.com/onboarding?x=1")]
    [InlineData("https://us.app.example.com", "/", "https://us.app.example.com/")]
    [InlineData("http://us.app.example.com", "/", null)]
    [InlineData("", "/", null)]
    public void Region_urls_are_https_on_the_configured_host(string host, string path, string? expected)
    {
        var options = new MlcpWebOptions { RegionHosts = new Dictionary<string, string> { ["us"] = host } };

        options.RegionUrl("us", path)?.AbsoluteUri.Should().Be(expected);
        if (expected is null)
        {
            options.RegionUrl("us", path).Should().BeNull();
        }
    }

    // ------------------------------------------------------------ startup requirements

    [Fact]
    public void A_bare_configuration_is_not_deployable_and_every_gap_is_named()
    {
        var problems = StartupRequirements.FindProblems(new ConfigurationBuilder().Build());

        string.Join('\n', problems).Should()
            .Contain("AzureAd:ClientId")
            .And.Contain("AzureAd:ClientCredentials")
            .And.Contain("ConnectionStrings:Redis")
            .And.Contain("Mlcp:DataProtection:BlobUri")
            .And.Contain("Mlcp:Email:Endpoint")
            .And.Contain("Mlcp:PublicBaseUrl")
            .And.Contain("AllowedHosts")
            .And.Contain("Mlcp:Regions:DirectoryTableUri");

        var act = () => StartupRequirements.ThrowIfAny(problems);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_complete_configuration_is_deployable()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAd:ClientId"] = Guid.NewGuid().ToString(),
            ["AzureAd:ClientCredentials:0:SourceType"] = "KeyVault",
            ["ConnectionStrings:Redis"] = "redis:6380",
            ["Mlcp:DataProtection:BlobUri"] = "https://st.blob.core.windows.net/dp/keys.xml",
            ["Mlcp:DataProtection:KeyUri"] = "https://kv.vault.azure.net/keys/dp",
            ["Mlcp:Email:Endpoint"] = "https://acs.communication.azure.com",
            ["Mlcp:Email:SenderAddress"] = "noreply@mlcp.example",
            ["Mlcp:Region"] = "eu",
            ["Mlcp:Regions:DirectoryTableUri"] = "https://g.table.core.windows.net/TenantRegions",
            ["Mlcp:PublicBaseUrl"] = "https://eu.app.example.com/",
            ["AllowedHosts"] = "eu.app.example.com;*.azurecontainerapps.io",
        }).Build();

        StartupRequirements.FindProblems(configuration).Should().BeEmpty();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("eu.app.example.com;*")]
    public void A_wildcard_host_filter_is_not_deployable(string allowedHosts)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AllowedHosts"] = allowedHosts })
            .Build();

        StartupRequirements.FindProblems(configuration).Should().Contain(p => p.StartsWith("AllowedHosts", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ request logging

    [Fact]
    public void Request_logs_use_the_route_template_so_the_consent_token_never_appears()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/onboarding/consent/SECRET-TOKEN";
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("onboarding/consent/{token}"),
            order: 0,
            EndpointMetadataCollection.Empty,
            displayName: null));

        RequestLogPath.For(context).Should().Be("/onboarding/consent/{token}");
    }

    [Fact]
    public void Unrouted_requests_log_their_path_without_the_query()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/css/site.css";
        context.Request.QueryString = new QueryString("?v=secret");

        RequestLogPath.For(context).Should().Be("/css/site.css");
    }

    private static string Unix(DateTimeOffset value) => value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
