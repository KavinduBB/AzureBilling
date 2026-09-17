using FluentAssertions;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Mlcp.UnitTests.Resilience;

/// <summary>The ADR-016 classification table, exhaustively.</summary>
public class MicrosoftFailureClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    // Success and redirects (usage reports answer 302).
    [InlineData(200, MicrosoftApp.Core, true, MicrosoftFailureKind.None)]
    [InlineData(204, MicrosoftApp.Core, false, MicrosoftFailureKind.None)]
    [InlineData(302, MicrosoftApp.UsageInsights, false, MicrosoftFailureKind.None)]
    // Floor calls with the core app.
    [InlineData(401, MicrosoftApp.Core, true, MicrosoftFailureKind.GrantRevoked)]
    [InlineData(403, MicrosoftApp.Core, true, MicrosoftFailureKind.FloorPermissionRemoved)]
    // Non-floor calls: only the capability degrades.
    [InlineData(401, MicrosoftApp.Core, false, MicrosoftFailureKind.CapabilityDenied)]
    [InlineData(403, MicrosoftApp.Core, false, MicrosoftFailureKind.CapabilityDenied)]
    // The usage app can never flag the tenant, even if a caller marks the call as floor.
    [InlineData(401, MicrosoftApp.UsageInsights, false, MicrosoftFailureKind.CapabilityDenied)]
    [InlineData(403, MicrosoftApp.UsageInsights, false, MicrosoftFailureKind.CapabilityDenied)]
    [InlineData(403, MicrosoftApp.UsageInsights, true, MicrosoftFailureKind.CapabilityDenied)]
    // Transient.
    [InlineData(408, MicrosoftApp.Core, true, MicrosoftFailureKind.Transient)]
    [InlineData(429, MicrosoftApp.Core, false, MicrosoftFailureKind.Transient)]
    [InlineData(500, MicrosoftApp.Core, true, MicrosoftFailureKind.Transient)]
    [InlineData(502, MicrosoftApp.Core, false, MicrosoftFailureKind.Transient)]
    [InlineData(503, MicrosoftApp.UsageInsights, false, MicrosoftFailureKind.Transient)]
    [InlineData(504, MicrosoftApp.Core, false, MicrosoftFailureKind.Transient)]
    [InlineData(507, MicrosoftApp.Core, false, MicrosoftFailureKind.Transient)]
    // Not found and other client errors.
    [InlineData(404, MicrosoftApp.Core, true, MicrosoftFailureKind.NotFound)]
    [InlineData(404, MicrosoftApp.Core, false, MicrosoftFailureKind.NotFound)]
    [InlineData(400, MicrosoftApp.Core, false, MicrosoftFailureKind.OtherClientError)]
    [InlineData(409, MicrosoftApp.Core, false, MicrosoftFailureKind.OtherClientError)]
    [InlineData(422, MicrosoftApp.Core, true, MicrosoftFailureKind.OtherClientError)]
    public void Http_statuses_are_classified(int status, MicrosoftApp app, bool floor, MicrosoftFailureKind expected)
    {
        MicrosoftFailureClassifier.ClassifyStatus(status, app, floor).Should().Be(expected);
    }

    [Theory]
    // Missing service principal: propagation window decides for the core app.
    [InlineData("AADSTS700016", MicrosoftApp.Core, 1, MicrosoftFailureKind.Transient)]
    [InlineData("AADSTS700016", MicrosoftApp.Core, 9, MicrosoftFailureKind.Transient)]
    [InlineData("AADSTS700016", MicrosoftApp.Core, 10, MicrosoftFailureKind.GrantRevoked)]
    [InlineData("AADSTS700016", MicrosoftApp.Core, 60, MicrosoftFailureKind.GrantRevoked)]
    [InlineData("AADSTS700016", MicrosoftApp.Core, null, MicrosoftFailureKind.GrantRevoked)]
    [InlineData("AADSTS7000229", MicrosoftApp.Core, 2, MicrosoftFailureKind.Transient)]
    [InlineData("AADSTS7000229", MicrosoftApp.Core, 30, MicrosoftFailureKind.GrantRevoked)]
    // Usage Insights: no principal means tier 2 was never granted, whatever the timing.
    [InlineData("AADSTS700016", MicrosoftApp.UsageInsights, 1, MicrosoftFailureKind.CapabilityDenied)]
    [InlineData("AADSTS7000229", MicrosoftApp.UsageInsights, null, MicrosoftFailureKind.CapabilityDenied)]
    // Lost grant regardless of timing.
    [InlineData("AADSTS7000112", MicrosoftApp.Core, 1, MicrosoftFailureKind.GrantRevoked)]
    [InlineData("AADSTS90002", MicrosoftApp.Core, null, MicrosoftFailureKind.GrantRevoked)]
    [InlineData("AADSTS65001", MicrosoftApp.Core, null, MicrosoftFailureKind.GrantRevoked)]
    [InlineData("invalid_grant", MicrosoftApp.Core, null, MicrosoftFailureKind.GrantRevoked)]
    [InlineData("AADSTS7000112", MicrosoftApp.UsageInsights, null, MicrosoftFailureKind.CapabilityDenied)]
    [InlineData("invalid_grant", MicrosoftApp.UsageInsights, null, MicrosoftFailureKind.CapabilityDenied)]
    // Any other customer-side refusal: no tenant effect, no retry.
    [InlineData("AADSTS53003", MicrosoftApp.Core, null, MicrosoftFailureKind.OtherClientError)]
    public void Refused_token_errors_are_classified(string code, MicrosoftApp app, int? minutesSinceConsent, MicrosoftFailureKind expected)
    {
        DateTimeOffset? consent = minutesSinceConsent is { } m ? Now.AddMinutes(-m) : null;

        MicrosoftFailureClassifier
            .ClassifyTokenFailure(new TokenFailure(code, TokenFailureCategory.Refused), app, consent, Now)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("AADSTS7000215")]
    [InlineData("AADSTS7000222")]
    [InlineData("AADSTS700027")]
    [InlineData("invalid_client")]
    public void Our_own_credential_failures_are_platform_failures_for_both_apps(string code)
    {
        foreach (var app in new[] { MicrosoftApp.Core, MicrosoftApp.UsageInsights })
        {
            MicrosoftFailureClassifier
                .ClassifyTokenFailure(new TokenFailure(code, TokenFailureCategory.PlatformCredential), app, null, Now)
                .Should().Be(MicrosoftFailureKind.PlatformCredential);

            // Even if a caller mislabels the category, the code wins.
            MicrosoftFailureClassifier
                .ClassifyTokenFailure(new TokenFailure(code, TokenFailureCategory.Refused), app, null, Now)
                .Should().Be(MicrosoftFailureKind.PlatformCredential);
        }
    }

    [Theory]
    [InlineData(TokenFailureCategory.Transport, null, MicrosoftFailureKind.Transient)]
    [InlineData(TokenFailureCategory.Transport, 503, MicrosoftFailureKind.Transient)]
    [InlineData(TokenFailureCategory.Paused, null, MicrosoftFailureKind.PlatformCredential)]
    [InlineData(TokenFailureCategory.NotConfigured, null, MicrosoftFailureKind.PlatformCredential)]
    [InlineData(TokenFailureCategory.PlatformCredential, null, MicrosoftFailureKind.PlatformCredential)]
    [InlineData(TokenFailureCategory.Unknown, null, MicrosoftFailureKind.Transient)]
    [InlineData(TokenFailureCategory.Unknown, 500, MicrosoftFailureKind.Transient)]
    [InlineData(TokenFailureCategory.Unknown, 429, MicrosoftFailureKind.Transient)]
    [InlineData(TokenFailureCategory.Unknown, 400, MicrosoftFailureKind.OtherClientError)]
    public void Token_failure_categories_are_classified(TokenFailureCategory category, int? status, MicrosoftFailureKind expected)
    {
        MicrosoftFailureClassifier
            .ClassifyTokenFailure(new TokenFailure(null, category, status), MicrosoftApp.Core, null, Now)
            .Should().Be(expected);
    }

    [Fact]
    public void Pipeline_exceptions_are_transient()
    {
        Exception[] exceptions =
        [
            new HttpRequestException("reset"),
            new TimeoutRejectedException(),
            new BrokenCircuitException(),
            new Polly.RateLimiting.RateLimiterRejectedException(),
            new TimeoutException(),
            new TaskCanceledException(),
        ];

        foreach (var exception in exceptions)
        {
            MicrosoftFailureClassifier.ClassifyException(exception).Should().Be(MicrosoftFailureKind.Transient, exception.GetType().Name);
        }
    }

    [Fact]
    public void Token_exceptions_must_go_through_the_token_classifier()
    {
        var act = () => MicrosoftFailureClassifier.ClassifyException(new TokenAcquisitionException("x"));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(MicrosoftFailureKind.GrantRevoked, true, false)]
    [InlineData(MicrosoftFailureKind.FloorPermissionRemoved, true, false)]
    [InlineData(MicrosoftFailureKind.CapabilityDenied, false, false)]
    [InlineData(MicrosoftFailureKind.Transient, false, true)]
    [InlineData(MicrosoftFailureKind.PlatformCredential, false, false)]
    [InlineData(MicrosoftFailureKind.NotFound, false, false)]
    [InlineData(MicrosoftFailureKind.OtherClientError, false, false)]
    [InlineData(MicrosoftFailureKind.None, false, false)]
    public void Only_floor_kinds_change_the_tenant_and_only_transient_is_retried(MicrosoftFailureKind kind, bool reconsent, bool retry)
    {
        MicrosoftFailureClassifier.RequiresReconsent(kind).Should().Be(reconsent);
        MicrosoftFailureClassifier.IsRetryable(kind).Should().Be(retry);
    }

    [Fact]
    public void The_needs_reconsent_exception_keeps_its_compatibility_constructor()
    {
        var tenant = Guid.NewGuid();

        var revoked = new NeedsReconsentException(tenant, MicrosoftProvider.Graph, 401);
        var removed = new NeedsReconsentException(tenant, MicrosoftProvider.Graph, 403);

        revoked.Kind.Should().Be(MicrosoftFailureKind.GrantRevoked);
        removed.Kind.Should().Be(MicrosoftFailureKind.FloorPermissionRemoved);
        removed.RequiresReconsent.Should().BeTrue();
        removed.Should().BeAssignableTo<MicrosoftCallRefusedException>();
        removed.Reason.Should().Be("FloorPermissionRemoved:Graph:403");
    }

    [Fact]
    public void A_needs_reconsent_exception_cannot_carry_a_non_floor_kind()
    {
        var act = () => new NeedsReconsentException(MicrosoftFailureKind.CapabilityDenied, Guid.NewGuid(), MicrosoftProvider.Graph, 403, null, "x");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
