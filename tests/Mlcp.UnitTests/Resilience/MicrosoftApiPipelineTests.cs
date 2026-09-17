using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mlcp.Shared.Http;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;
using Polly.CircuitBreaker;

namespace Mlcp.UnitTests.Resilience;

/// <summary>Records every call and answers from a script.</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> _respond;
    private int _calls;

    public ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        : this((request, call) => Task.FromResult(respond(request, call)))
    {
    }

    public ScriptedHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public int Calls => _calls;

    public ConcurrentQueue<(HttpRequestMessage Request, DateTimeOffset At)> Requests { get; } = new();

    public TimeProvider Clock { get; set; } = TimeProvider.System;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        Requests.Enqueue((request, Clock.GetUtcNow()));
        return _respond(request, call);
    }

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("{}") };
}

/// <summary>A token provider that hands out tokens or throws a scripted failure, and records evictions.</summary>
internal sealed class FakeTokenProvider : ITenantTokenProvider
{
    public Func<Guid, TokenAudience, string> Issue { get; set; } = (_, audience) => $"token-{audience}";

    public List<(Guid TenantId, MicrosoftApp App, TokenAudience Audience)> Evictions { get; } = [];

    public int Issued { get; private set; }

    public Task<string> GetAccessTokenAsync(Guid tenantId, TokenAudience audience, CancellationToken cancellationToken)
    {
        Issued++;
        return Task.FromResult(Issue(tenantId, audience));
    }

    public void Evict(Guid tenantId, MicrosoftApp app, TokenAudience audience) => Evictions.Add((tenantId, app, audience));
}

/// <summary>Pipeline behaviour through the real client, executor and pipelines (ADR-016 rules 4, 6, 7, 8).</summary>
public sealed class MicrosoftApiPipelineTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeTokenProvider _tokens = new();

    private MicrosoftApiResilienceOptions _options = MicrosoftApiResilienceOptions.Default with
    {
        MaxRetryAttempts = 3,
        BaseDelay = TimeSpan.FromSeconds(1),
        CircuitMinimumThroughput = 100,
    };

    private MicrosoftApiPipelines? _pipelines;

    public void Dispose() => _pipelines?.Dispose();

    private MicrosoftApiClient Client(ScriptedHandler handler)
    {
        handler.Clock = _clock;
        _pipelines?.Dispose();
        _pipelines = new MicrosoftApiPipelines(_options, _clock, NullLogger<MicrosoftApiPipelines>.Instance);
        var executor = new MicrosoftApiExecutor(_pipelines, _clock, NullLogger<MicrosoftApiExecutor>.Instance);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://microsoft.test/"), Timeout = Timeout.InfiniteTimeSpan };
        return new MicrosoftApiClient(http, _tokens, executor);
    }

    private static MicrosoftRequest Floor(Guid tenant) => new(tenant, MicrosoftProvider.Graph, TokenAudience.Graph, "v1.0/subscribedSkus") { IsFloorCall = true };

    private static MicrosoftRequest Arm(Guid tenant) => new(tenant, MicrosoftProvider.ResourceManager, TokenAudience.ResourceManager, "subscriptions");

    /// <summary>Runs <paramref name="work"/> while advancing fake time in one-second steps.</summary>
    private async Task<T> RunWithClock<T>(Func<Task<T>> work)
    {
        var task = work();

        for (var i = 0; i < 100_000 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
            await Task.Delay(1);
        }

        return await task;
    }

    [Fact]
    public async Task A_403_on_a_floor_call_is_not_retried_and_evicts_the_token()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Status(HttpStatusCode.Forbidden));

        var response = await Client(handler).ProbeAsync(Floor(TenantA), CancellationToken.None);

        response.Kind.Should().Be(MicrosoftFailureKind.FloorPermissionRemoved);
        response.StatusCode.Should().Be(403);
        handler.Calls.Should().Be(1);
        _tokens.Evictions.Should().Equal((TenantA, MicrosoftApp.Core, TokenAudience.Graph));
    }

    [Fact]
    public async Task A_401_is_retried_once_with_a_fresh_token_and_then_means_grant_revoked()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Status(HttpStatusCode.Unauthorized));

        var response = await Client(handler).ProbeAsync(Floor(TenantA), CancellationToken.None);

        response.Kind.Should().Be(MicrosoftFailureKind.GrantRevoked);
        handler.Calls.Should().Be(2, "one attempt, then one with a freshly acquired token — never the retry policy");
        _tokens.Issued.Should().Be(2);
        _tokens.Evictions.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_stale_token_401_recovers_on_the_fresh_token()
    {
        var handler = new ScriptedHandler((_, call) => ScriptedHandler.Status(call == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK));

        var response = await Client(handler).ProbeAsync(Floor(TenantA), CancellationToken.None);

        response.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_403_on_a_non_floor_call_is_capability_denied_and_throws_a_refusal_not_reconsent()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Status(HttpStatusCode.Forbidden));
        var client = Client(handler);

        var act = () => client.SendAsync(Arm(TenantA), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<MicrosoftCallRefusedException>();
        thrown.Which.Should().NotBeOfType<NeedsReconsentException>();
        thrown.Which.Kind.Should().Be(MicrosoftFailureKind.CapabilityDenied);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_floor_refusal_throws_needs_reconsent_from_the_throwing_api()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Status(HttpStatusCode.Forbidden));
        var client = Client(handler);

        var act = () => client.SendAsync(Floor(TenantA), CancellationToken.None);

        (await act.Should().ThrowAsync<NeedsReconsentException>()).Which.Kind.Should().Be(MicrosoftFailureKind.FloorPermissionRemoved);
    }

    [Fact]
    public async Task A_429_is_retried_after_the_hinted_delay()
    {
        var handler = new ScriptedHandler((_, call) =>
        {
            if (call > 1)
            {
                return ScriptedHandler.Status(HttpStatusCode.OK);
            }

            var throttled = ScriptedHandler.Status(HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return throttled;
        });

        var client = Client(handler);
        var start = _clock.GetUtcNow();

        var response = await RunWithClock(() => client.ProbeAsync(Arm(TenantA), CancellationToken.None));

        response.Succeeded.Should().BeTrue();
        handler.Calls.Should().Be(2);
        var secondAt = handler.Requests.ToArray()[1].At;
        (secondAt - start).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30)).And.BeLessThan(TimeSpan.FromSeconds(32));
    }

    [Fact]
    public async Task Retry_after_zero_falls_back_to_backoff()
    {
        var handler = new ScriptedHandler((_, call) =>
        {
            if (call > 1)
            {
                return ScriptedHandler.Status(HttpStatusCode.OK);
            }

            var throttled = ScriptedHandler.Status(HttpStatusCode.TooManyRequests);
            throttled.Headers.TryAddWithoutValidation("Retry-After", "0");
            return throttled;
        });

        var client = Client(handler);
        var start = _clock.GetUtcNow();

        var response = await RunWithClock(() => client.ProbeAsync(Arm(TenantA), CancellationToken.None));

        response.Succeeded.Should().BeTrue();
        (handler.Requests.ToArray()[1].At - start).Should().BePositive("a zero hint must not cause an immediate retry");
    }

    [Fact]
    public async Task A_hint_above_the_interactive_budget_fails_fast_as_throttled()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var throttled = ScriptedHandler.Status(HttpStatusCode.TooManyRequests);
            throttled.Headers.TryAddWithoutValidation(RetryAfterReader.CostManagementQpuRetryAfterHeader, "120");
            return throttled;
        });

        var client = Client(handler);

        var act = () => client.SendAsync(Arm(TenantA), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<MicrosoftThrottledException>();
        thrown.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(120));
        handler.Calls.Should().Be(1, "a two-minute hint is above the 60-second interactive budget");
    }

    [Fact]
    public async Task A_worker_sleeps_through_a_hint_inside_its_budget()
    {
        _options = _options with { Budget = MicrosoftCallBudget.Worker };

        var handler = new ScriptedHandler((_, call) =>
        {
            if (call > 1)
            {
                return ScriptedHandler.Status(HttpStatusCode.OK);
            }

            var throttled = ScriptedHandler.Status(HttpStatusCode.TooManyRequests);
            throttled.Headers.TryAddWithoutValidation(RetryAfterReader.ConsumptionRetryAfterHeader, "120");
            return throttled;
        });

        var client = Client(handler);

        var response = await RunWithClock(() => client.ProbeAsync(Arm(TenantA), CancellationToken.None));

        response.Succeeded.Should().BeTrue();
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Persistent_5xx_is_retried_then_reported_transient()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Status(HttpStatusCode.ServiceUnavailable));
        var client = Client(handler);

        var response = await RunWithClock(() => client.ProbeAsync(Arm(TenantA), CancellationToken.None));

        response.Kind.Should().Be(MicrosoftFailureKind.Transient);
        handler.Calls.Should().Be(1 + _options.MaxRetryAttempts);
    }

    [Fact]
    public async Task A_non_idempotent_post_is_not_retried()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Status(HttpStatusCode.ServiceUnavailable));
        var client = Client(handler);

        var response = await client.ProbeAsync(
            MicrosoftRequest.PostJson(TenantA, MicrosoftProvider.AzureBilling, TokenAudience.ResourceManager, "x", new { }, isIdempotent: false),
            CancellationToken.None);

        response.Kind.Should().Be(MicrosoftFailureKind.Transient);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task An_idempotent_post_is_retried_with_a_fresh_body()
    {
        var bodies = new ConcurrentBag<string>();
        var handler = new ScriptedHandler(async (request, call) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            return ScriptedHandler.Status(call == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        });

        var client = Client(handler);

        var response = await RunWithClock(() => client.ProbeAsync(
            MicrosoftRequest.PostJson(TenantA, MicrosoftProvider.CostManagement, TokenAudience.ResourceManager, "query", new { type = "ActualCost" }, isIdempotent: true),
            CancellationToken.None));

        response.Succeeded.Should().BeTrue();
        bodies.Should().HaveCount(2).And.OnlyContain(b => b.Contains("ActualCost"));
    }

    [Fact]
    public async Task ClientType_is_sent_on_cost_management_calls_only()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Status(HttpStatusCode.OK));
        var client = Client(handler);

        await client.ProbeAsync(
            MicrosoftRequest.PostJson(TenantA, MicrosoftProvider.CostManagement, TokenAudience.ResourceManager, "query", new { }, true),
            CancellationToken.None);
        await client.ProbeAsync(Arm(TenantA), CancellationToken.None);
        await client.ProbeAsync(Floor(TenantA), CancellationToken.None);

        var requests = handler.Requests.Select(r => r.Request).ToArray();
        requests[0].Headers.GetValues(MicrosoftApiClient.ClientTypeHeader).Should().Equal("Mlcp");
        requests[1].Headers.Contains(MicrosoftApiClient.ClientTypeHeader).Should().BeFalse();
        requests[2].Headers.Contains(MicrosoftApiClient.ClientTypeHeader).Should().BeFalse();
        requests[0].Headers.Authorization!.Parameter.Should().Be("token-ResourceManager");
    }

    [Fact]
    public async Task The_breaker_is_keyed_per_tenant()
    {
        _options = _options with { CircuitMinimumThroughput = 2, CircuitFailureRatio = 0.5 };

        var handler = new ScriptedHandler((request, _) => ScriptedHandler.Status(
            request.RequestUri!.AbsolutePath.Contains("bad", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        var client = Client(handler);

        MicrosoftRequest Post(Guid tenant, string path) => MicrosoftRequest.PostJson(
            tenant, MicrosoftProvider.AzureBilling, TokenAudience.ResourceManager, path, new { }, isIdempotent: false);

        await client.ProbeAsync(Post(TenantA, "bad"), CancellationToken.None);
        await client.ProbeAsync(Post(TenantA, "bad"), CancellationToken.None);

        _pipelines!.GetCircuitState(TenantA, MicrosoftProvider.AzureBilling).Should().Be(CircuitState.Open);

        var blocked = await client.ProbeAsync(Post(TenantA, "good"), CancellationToken.None);
        blocked.Kind.Should().Be(MicrosoftFailureKind.Transient);
        blocked.RetryAfter.Should().NotBeNull();
        handler.Calls.Should().Be(2, "an open circuit does not reach Microsoft");

        var other = await client.ProbeAsync(Post(TenantB, "good"), CancellationToken.None);
        other.Succeeded.Should().BeTrue("tenant B has its own breaker");
        _pipelines.GetCircuitState(TenantB, MicrosoftProvider.AzureBilling).Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task The_concurrency_limit_is_shared_across_tenants_and_a_rejection_is_transient()
    {
        _options = _options with { MaxConcurrencyPerProvider = 1, ConcurrencyQueueLimit = 0 };

        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler((_, call) => call == 1 ? release.Task : Task.FromResult(ScriptedHandler.Status(HttpStatusCode.OK)));
        var client = Client(handler);

        MicrosoftRequest Post(Guid tenant) => MicrosoftRequest.PostJson(
            tenant, MicrosoftProvider.CostManagement, TokenAudience.ResourceManager, "query", new { }, isIdempotent: false);

        var first = client.ProbeAsync(Post(TenantA), CancellationToken.None);
        await Task.Delay(50);

        var second = await client.ProbeAsync(Post(TenantB), CancellationToken.None);

        second.Kind.Should().Be(MicrosoftFailureKind.Transient, "tenant B shares tenant A's Cost Management slot");
        handler.Calls.Should().Be(1);

        release.SetResult(ScriptedHandler.Status(HttpStatusCode.OK));
        (await first).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Idle_pipelines_are_evicted()
    {
        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Status(HttpStatusCode.OK));
        var client = Client(handler);

        await client.ProbeAsync(Arm(TenantA), CancellationToken.None);
        _pipelines!.CachedPipelineCount.Should().Be(1);

        _clock.Advance(TimeSpan.FromHours(2));
        await client.ProbeAsync(Arm(TenantB), CancellationToken.None);

        _pipelines.CachedPipelineCount.Should().Be(1, "tenant A's pipeline was idle for over an hour");
        _pipelines.GetCircuitState(TenantA, MicrosoftProvider.ResourceManager).Should().BeNull();
    }

    [Theory]
    [InlineData(1, MicrosoftFailureKind.Transient)]
    [InlineData(30, MicrosoftFailureKind.GrantRevoked)]
    public async Task A_missing_principal_is_classified_with_the_propagation_window(int minutesAgo, MicrosoftFailureKind expected)
    {
        _tokens.Issue = (_, _) => throw new TokenAcquisitionException(
            MicrosoftApp.Core, new TokenFailure("AADSTS700016", TokenFailureCategory.Refused), "no principal");

        var handler = new ScriptedHandler((_, _) => ScriptedHandler.Status(HttpStatusCode.OK));
        var client = Client(handler);

        var response = await client.ProbeAsync(
            Floor(TenantA) with { ConsentCallbackUtc = _clock.GetUtcNow().AddMinutes(-minutesAgo) },
            CancellationToken.None);

        response.Kind.Should().Be(expected);
        response.ErrorCode.Should().Be("AADSTS700016");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_usage_app_without_a_principal_is_capability_denied()
    {
        _tokens.Issue = (_, _) => throw new TokenAcquisitionException(
            MicrosoftApp.UsageInsights, new TokenFailure("AADSTS7000229", TokenFailureCategory.Refused), "no principal");

        var client = Client(new ScriptedHandler((_, _) => ScriptedHandler.Status(HttpStatusCode.OK)));

        var response = await client.ProbeAsync(
            new MicrosoftRequest(TenantA, MicrosoftProvider.Graph, TokenAudience.GraphReports, "v1.0/reports/x"),
            CancellationToken.None);

        response.Kind.Should().Be(MicrosoftFailureKind.CapabilityDenied);
    }

    [Fact]
    public async Task A_paused_platform_credential_throws_a_platform_exception_with_the_pause()
    {
        _tokens.Issue = (_, _) => throw new TokenAcquisitionException(
            MicrosoftApp.Core, new TokenFailure("PlatformCredentialPaused", TokenFailureCategory.Paused), "paused")
        {
            PausedUntilUtc = _clock.GetUtcNow().AddMinutes(3),
        };

        var client = Client(new ScriptedHandler((_, _) => ScriptedHandler.Status(HttpStatusCode.OK)));

        var act = () => client.SendAsync(Arm(TenantA), CancellationToken.None);

        (await act.Should().ThrowAsync<MicrosoftPlatformCredentialException>()).Which.RetryAfter.Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task A_network_failure_is_transient()
    {
        var client = Client(new ScriptedHandler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("reset"))));

        var response = await RunWithClock(() => client.ProbeAsync(Arm(TenantA), CancellationToken.None));

        response.Kind.Should().Be(MicrosoftFailureKind.Transient);
        response.StatusCode.Should().BeNull();
    }

    [Fact]
    public async Task Not_found_is_returned_to_the_caller_not_thrown()
    {
        var client = Client(new ScriptedHandler((_, _) => ScriptedHandler.Status(HttpStatusCode.NotFound)));

        using var response = await client.SendAsync(Arm(TenantA), CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
