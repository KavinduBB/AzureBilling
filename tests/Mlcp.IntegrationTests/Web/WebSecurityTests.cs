using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Web;
using FluentAssertions;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;

namespace Mlcp.IntegrationTests.Web;

/// <summary>
/// The web host's security behaviour through real HTTP requests: fallback authorization,
/// antiforgery, ADR-018's consent hardening and destructive-action gates, isolation layer 4,
/// and ADR-021 routing.
/// </summary>
[Trait("Category", "TenantIsolation")]
public sealed partial class WebSecurityTests : IClassFixture<MlcpWebFactory>
{
    private readonly MlcpWebFactory _factory;

    public WebSecurityTests(MlcpWebFactory factory)
    {
        _factory = factory;
    }

    // ------------------------------------------------------------ authentication

    [Fact]
    public async Task An_anonymous_visitor_to_a_protected_page_is_challenged()
    {
        using var client = _factory.CreateUserClient(null);

        using var response = await client.GetAsync("/onboarding");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(TestAuthHandler.ChallengePath);
    }

    [Fact]
    public async Task An_anonymous_visitor_to_an_unmarked_endpoint_is_challenged_by_the_fallback_policy()
    {
        using var client = _factory.CreateUserClient(null);

        using var response = await client.GetAsync("/prices");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(TestAuthHandler.ChallengePath);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/guides/connect-organisation")]
    [InlineData("/health/live")]
    public async Task Public_pages_stay_public(string path)
    {
        using var client = _factory.CreateUserClient(null);

        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------ consent callback (ADR-018)

    [Fact]
    public async Task A_forged_callback_without_a_nonce_is_refused_and_changes_nothing()
    {
        var tenantId = Guid.NewGuid();
        var admin = TestUser.NewAdmin(tenantId);
        using var client = _factory.CreateUserClient(admin);

        using var response = await client.GetAsync(
            $"/onboarding/consent-callback?admin_consent=True&tenant={tenantId}&state=forged");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("expired or was already used");
        _factory.Verifier.CallsFor(tenantId).Should().Be(0);
        _factory.Jobs.Jobs.Should().NotContain(j => j.TenantId == tenantId);

        if (_factory.Repository.Tenants.TryGetValue(tenantId, out var tenant))
        {
            tenant.ConsentGrantedUtc.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_callback_without_state_is_refused()
    {
        var tenantId = Guid.NewGuid();
        using var client = _factory.CreateUserClient(TestUser.NewAdmin(tenantId));

        using var response = await client.GetAsync($"/onboarding/consent-callback?admin_consent=True&tenant={tenantId}");

        (await response.Content.ReadAsStringAsync()).Should().Contain("expired or was already used");
        _factory.Verifier.CallsFor(tenantId).Should().Be(0);
    }

    [Fact]
    public async Task A_real_state_works_once_only_for_its_admin_and_only_with_the_matching_tenant()
    {
        var tenantId = Guid.NewGuid();
        var admin = TestUser.NewAdmin(tenantId);
        using var adminClient = _factory.CreateUserClient(admin);

        var state = await StartConnectAsync(adminClient);

        // Another admin in the same tenant cannot use it.
        using (var otherAdmin = _factory.CreateUserClient(TestUser.NewAdmin(tenantId)))
        {
            using var stolen = await otherAdmin.GetAsync(Callback(tenantId, state));
            (await stolen.Content.ReadAsStringAsync()).Should().Contain("expired or was already used");
        }

        // Microsoft's tenant parameter must match the caller's tid.
        using (var mismatched = await adminClient.GetAsync(Callback(Guid.NewGuid(), state)))
        {
            (await mismatched.Content.ReadAsStringAsync()).Should().Contain("different organisation");
        }

        _factory.Verifier.CallsFor(tenantId).Should().Be(0);

        // The mismatched attempt consumed the nonce, so even the genuine callback now fails:
        // a state is good for one callback, whatever its outcome.
        using (var afterMismatch = await adminClient.GetAsync(Callback(tenantId, state)))
        {
            (await afterMismatch.Content.ReadAsStringAsync()).Should().Contain("expired or was already used");
        }

        // A fresh attempt succeeds once and is verified with Microsoft, not assumed.
        var fresh = await StartConnectAsync(adminClient);

        using (var ok = await adminClient.GetAsync(Callback(tenantId, fresh)))
        {
            ok.StatusCode.Should().Be(HttpStatusCode.Redirect);
            ok.Headers.Location!.OriginalString.Should().Be("/onboarding");
        }

        _factory.Verifier.CallsFor(tenantId).Should().Be(1);
        _factory.Repository.Tenants[tenantId].Status.Should().Be(TenantStatus.Provisioning);
        _factory.Jobs.Jobs.Should().Contain((tenantId, SyncJobType.CapabilityDiscovery));

        using (var replay = await adminClient.GetAsync(Callback(tenantId, fresh)))
        {
            (await replay.Content.ReadAsStringAsync()).Should().Contain("expired or was already used");
        }

        _factory.Verifier.CallsFor(tenantId).Should().Be(1);
    }

    [Fact]
    public async Task Connecting_requires_confirming_the_region()
    {
        var tenantId = Guid.NewGuid();
        using var client = _factory.CreateUserClient(TestUser.NewAdmin(tenantId));
        var token = await AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/onboarding/connect", Form(token));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/onboarding");
        _factory.Regions.Regions.Should().NotContainKey(tenantId);
    }

    [Fact]
    public async Task A_tenant_registered_in_another_region_is_sent_there_instead_of_to_entra()
    {
        var tenantId = Guid.NewGuid();
        var admin = TestUser.NewAdmin(tenantId);
        using var client = _factory.CreateUserClient(admin);
        var token = await AntiforgeryTokenAsync(client);
        _factory.Regions.Regions[tenantId] = "us";

        using var response = await client.PostAsync("/onboarding/connect", Form(token, ("confirmRegion", MlcpWebFactory.ThisRegion)));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.Host.Should().Be("us.app.example.com");
    }

    // ------------------------------------------------------------ admin-only actions

    [Fact]
    public async Task A_non_admin_cannot_start_admin_consent()
    {
        var tenantId = Guid.NewGuid();
        using var client = _factory.CreateUserClient(TestUser.NewMember(tenantId));
        var token = await AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/onboarding/connect", Form(token, ("confirmRegion", MlcpWebFactory.ThisRegion)));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Regions.Regions.Should().NotContainKey(tenantId);
    }

    [Fact]
    public async Task A_non_admin_is_not_offered_the_connect_button()
    {
        using var client = _factory.CreateUserClient(TestUser.NewMember(Guid.NewGuid()));

        var page = await client.GetStringAsync("/onboarding");

        page.Should().Contain("Ask my administrator");
        page.Should().NotContain("action=\"/onboarding/connect\"");
    }

    [Fact]
    public async Task A_non_admin_cannot_disconnect()
    {
        var tenantId = Guid.NewGuid();
        _factory.SeedConnectedTenant(tenantId);
        using var client = _factory.CreateUserClient(TestUser.NewMember(tenantId));
        var token = await AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/onboarding/disconnect", Form(token, ("confirmed", "true")));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Repository.Tenants[tenantId].Status.Should().Be(TenantStatus.Active);
    }

    [Fact]
    public async Task An_admin_without_a_recent_sign_in_is_asked_to_sign_in_again_before_disconnecting()
    {
        var tenantId = Guid.NewGuid();
        _factory.SeedConnectedTenant(tenantId);
        using var client = _factory.CreateUserClient(TestUser.NewAdmin(tenantId, DateTimeOffset.UtcNow.AddHours(-2)));
        var token = await AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/onboarding/disconnect", Form(token, ("confirmed", "true")));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be($"{TestAuthHandler.ChallengePath}?prompt=login");
        _factory.Repository.Tenants[tenantId].Status.Should().Be(TenantStatus.Active);
    }

    [Fact]
    public async Task A_recently_signed_in_admin_can_disconnect_and_cancel_it()
    {
        var tenantId = Guid.NewGuid();
        _factory.SeedConnectedTenant(tenantId);
        using var client = _factory.CreateUserClient(TestUser.NewAdmin(tenantId, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var token = await AntiforgeryTokenAsync(client);

        var confirmPage = await client.GetStringAsync("/onboarding/disconnect");
        confirmPage.Should().Contain("Disconnect Contoso?").And.NotContain("onsubmit");

        using (var disconnect = await client.PostAsync("/onboarding/disconnect", Form(token, ("confirmed", "true"))))
        {
            disconnect.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        _factory.Repository.Tenants[tenantId].Status.Should().Be(TenantStatus.GracePeriod);

        using (var cancel = await client.PostAsync("/onboarding/cancel-disconnect", Form(token)))
        {
            cancel.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        _factory.Repository.Tenants[tenantId].Status.Should().Be(TenantStatus.Active);
        _factory.Repository.Tenants[tenantId].DeleteScheduledUtc.Should().BeNull();
    }

    [Fact]
    public async Task A_non_admin_cannot_cancel_a_disconnect()
    {
        var tenantId = Guid.NewGuid();
        var tenant = _factory.SeedConnectedTenant(tenantId);
        tenant.BeginGracePeriod(DateTimeOffset.UtcNow, TimeSpan.FromDays(30));
        using var client = _factory.CreateUserClient(TestUser.NewMember(tenantId));
        var token = await AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/onboarding/cancel-disconnect", Form(token));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        tenant.Status.Should().Be(TenantStatus.GracePeriod);
    }

    [Fact]
    public async Task A_post_without_an_antiforgery_token_is_rejected()
    {
        var tenantId = Guid.NewGuid();
        _factory.SeedConnectedTenant(tenantId);
        using var client = _factory.CreateUserClient(TestUser.NewAdmin(tenantId, DateTimeOffset.UtcNow));

        using var response = await client.PostAsync("/onboarding/disconnect", new FormUrlEncodedContent([new("confirmed", "true")]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Repository.Tenants[tenantId].Status.Should().Be(TenantStatus.Active);
    }

    // ------------------------------------------------------------ isolation layer 4

    [Fact]
    public async Task Naming_another_tenant_in_the_route_is_a_404()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        _factory.SeedConnectedTenant(tenantId);
        _factory.SeedConnectedTenant(otherTenantId);
        using var client = _factory.CreateUserClient(TestUser.NewMember(tenantId));

        using var other = await client.GetAsync($"/api/v1/tenants/{otherTenantId}/connection");
        using var own = await client.GetAsync($"/api/v1/tenants/{tenantId}/connection");

        other.StatusCode.Should().Be(HttpStatusCode.NotFound, "404, not 403: another tenant's data is indistinguishable from none");
        own.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await own.Content.ReadFromJsonAsync<ConnectionStatus>();
        dto!.TenantId.Should().Be(tenantId);
        dto.Status.Should().Be("Active");
    }

    [Fact]
    public async Task Naming_another_tenant_in_a_form_model_is_a_404()
    {
        var tenantId = Guid.NewGuid();
        var tenant = _factory.SeedConnectedTenant(tenantId);
        tenant.MarkNeedsReconsent("GrantRevoked: test", DateTimeOffset.UtcNow);
        using var client = _factory.CreateUserClient(TestUser.NewAdmin(tenantId));

        var page = await client.GetStringAsync("/onboarding");
        page.Should().Contain("Check again");
        var token = TokenFrom(page);

        using var foreign = await client.PostAsync("/onboarding/check-again", Form(token, ("TenantId", Guid.NewGuid().ToString())));

        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.Jobs.Jobs.Should().NotContain((tenantId, SyncJobType.ReconsentProbe));
    }

    [Fact]
    public async Task Check_again_queues_a_reprobe_once_per_five_minutes()
    {
        var tenantId = Guid.NewGuid();
        var tenant = _factory.SeedConnectedTenant(tenantId);
        tenant.MarkNeedsReconsent("GrantRevoked: test", DateTimeOffset.UtcNow);
        using var client = _factory.CreateUserClient(TestUser.NewAdmin(tenantId));
        var token = await AntiforgeryTokenAsync(client);

        using var first = await client.PostAsync("/onboarding/check-again", Form(token, ("TenantId", tenantId.ToString())));
        using var second = await client.PostAsync("/onboarding/check-again", Form(token, ("TenantId", tenantId.ToString())));

        first.StatusCode.Should().Be(HttpStatusCode.Redirect);
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        _factory.Jobs.Jobs.Count(j => j == (tenantId, SyncJobType.ReconsentProbe)).Should().Be(1);
    }

    [Fact]
    public async Task Check_again_is_not_offered_to_a_non_admin()
    {
        var tenantId = Guid.NewGuid();
        var tenant = _factory.SeedConnectedTenant(tenantId);
        tenant.MarkNeedsReconsent("GrantRevoked: test", DateTimeOffset.UtcNow);
        using var client = _factory.CreateUserClient(TestUser.NewMember(tenantId));

        var page = await client.GetStringAsync("/onboarding");
        var token = await AntiforgeryTokenAsync(client);
        using var response = await client.PostAsync("/onboarding/check-again", Form(token, ("TenantId", tenantId.ToString())));

        page.Should().NotContain("Check again");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------ regional routing (ADR-021)

    [Fact]
    public async Task A_user_whose_tenant_lives_in_another_region_is_redirected_there()
    {
        var tenantId = Guid.NewGuid();
        _factory.Regions.Regions[tenantId] = "us";
        using var client = _factory.CreateUserClient(TestUser.NewMember(tenantId));

        using var response = await client.GetAsync("/onboarding?from=test");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("https://us.app.example.com/onboarding?from=test"));
        _factory.Repository.Tenants.Should().NotContainKey(tenantId, "nothing is written in the wrong region");
    }

    // ------------------------------------------------------------ helpers

    private static async Task<string> StartConnectAsync(HttpClient client)
    {
        var token = await AntiforgeryTokenAsync(client);

        using var response = await client.PostAsync("/onboarding/connect", Form(token, ("confirmRegion", MlcpWebFactory.ThisRegion)));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        var location = response.Headers.Location!;
        location.Host.Should().Be("login.microsoftonline.com");

        return HttpUtility.ParseQueryString(location.Query)["state"]
            ?? throw new InvalidOperationException("The consent URL carried no state.");
    }

    private static string Callback(Guid tenantParameter, string state)
        => $"/onboarding/consent-callback?admin_consent=True&tenant={tenantParameter}&state={Uri.EscapeDataString(state)}";

    private static Task<string> AntiforgeryTokenAsync(HttpClient client)
        => client.GetStringAsync(MlcpWebFactory.AntiforgeryPath);

    private static string TokenFrom(string html)
        => TokenPattern().Match(html) is { Success: true } match
            ? WebUtility.HtmlDecode(match.Groups["token"].Value)
            : throw new InvalidOperationException("The page carried no antiforgery token.");

    private static FormUrlEncodedContent Form(string token, params (string Key, string Value)[] fields)
        => new([new("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))]);

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"")]
    private static partial Regex TokenPattern();

    private sealed record ConnectionStatus(Guid TenantId, string Status, string AgreementType, DateTimeOffset? ConsentGrantedUtc);
}
