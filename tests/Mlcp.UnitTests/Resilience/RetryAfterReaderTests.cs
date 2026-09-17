using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Mlcp.Shared.Resilience;

namespace Mlcp.UnitTests.Resilience;

/// <summary>ADR-016 rule 8: every hint header Microsoft uses, and what counts as no hint.</summary>
public class RetryAfterReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Now);

    private static HttpResponseMessage Throttled() => new(HttpStatusCode.TooManyRequests);

    [Fact]
    public void Delta_seconds_are_read()
    {
        using var response = Throttled();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));

        RetryAfterReader.Read(response, _clock).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void An_http_date_is_converted_to_a_delay_from_now()
    {
        using var response = Throttled();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddMinutes(2));

        RetryAfterReader.Read(response, _clock).Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void A_date_in_the_past_is_no_hint()
    {
        using var response = Throttled();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddMinutes(-2));

        RetryAfterReader.Read(response, _clock).Should().BeNull();
    }

    [Fact]
    public void The_consumption_header_is_read()
    {
        using var response = Throttled();
        response.Headers.TryAddWithoutValidation(RetryAfterReader.ConsumptionRetryAfterHeader, "45");

        RetryAfterReader.Read(response, _clock).Should().Be(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void The_cost_management_qpu_header_is_read()
    {
        using var response = Throttled();
        response.Headers.TryAddWithoutValidation("x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after", "12");

        RetryAfterReader.Read(response, _clock).Should().Be(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public void The_longest_hint_wins_when_several_are_present()
    {
        using var response = Throttled();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
        response.Headers.TryAddWithoutValidation(RetryAfterReader.ConsumptionRetryAfterHeader, "20");
        response.Headers.TryAddWithoutValidation(RetryAfterReader.CostManagementQpuRetryAfterHeader, "15.5");

        RetryAfterReader.Read(response, _clock).Should().Be(TimeSpan.FromSeconds(20));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("soon")]
    [InlineData("")]
    public void Zero_negative_or_garbage_is_no_hint_so_backoff_applies(string value)
    {
        using var response = Throttled();
        response.Headers.TryAddWithoutValidation(RetryAfterReader.ConsumptionRetryAfterHeader, value);
        response.Headers.TryAddWithoutValidation(RetryAfterReader.CostManagementQpuRetryAfterHeader, value);

        RetryAfterReader.Read(response, _clock).Should().BeNull();
    }

    [Fact]
    public void Retry_after_zero_is_no_hint()
    {
        using var response = Throttled();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);

        RetryAfterReader.Read(response, _clock).Should().BeNull();
    }

    [Fact]
    public void Hints_above_any_budget_are_returned_unclamped()
    {
        // Whether to sleep or re-queue is the pipeline's decision, so the reader reports the truth.
        using var response = Throttled();
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(2));

        RetryAfterReader.Read(response, _clock).Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void No_headers_and_no_response_mean_no_hint()
    {
        using var response = Throttled();

        RetryAfterReader.Read(response, _clock).Should().BeNull();
        RetryAfterReader.Read(null, _clock).Should().BeNull();
    }
}
