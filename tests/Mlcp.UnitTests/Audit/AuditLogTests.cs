using System.Reflection;
using FluentAssertions;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Common;

namespace Mlcp.UnitTests.Audit;

/// <summary>
/// ADR-019: audit rows are immutable. An action that calls Microsoft is an attempt row plus an
/// outcome row, never an update.
/// </summary>
public class AuditLogTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Actor = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Simulates the id the database assigns on insert.</summary>
    internal static AuditLog Saved(AuditLog row, long id)
    {
        typeof(AuditLog).GetProperty(nameof(AuditLog.AuditLogId))!.SetValue(row, id);
        return row;
    }

    private static AuditLog SavedAttempt(long id = 41)
        => Saved(
            AuditLog.Attempt(TenantId, Actor, "ann@contoso.example", AuditAction.AutoRenewChanged, "Subscription", "sub-1", "corr", Now)
                .WithSourceIp("203.0.113.9")
                .WithFinancialImpact(99.99m, "usd"),
            id);

    [Fact]
    public void An_attempt_is_pending()
    {
        var attempt = SavedAttempt();

        attempt.Outcome.Should().Be(AuditOutcome.Pending);
        attempt.IsAttempt.Should().BeTrue();
        attempt.AttemptAuditLogId.Should().BeNull();
    }

    [Fact]
    public void An_outcome_is_a_new_row_pointing_at_its_attempt()
    {
        var attempt = SavedAttempt();

        var outcome = AuditLog.OutcomeOf(attempt, AuditOutcome.Succeeded, "HTTP 200", Now.AddSeconds(3));

        outcome.Should().NotBeSameAs(attempt);
        outcome.AttemptAuditLogId.Should().Be(41);
        outcome.Outcome.Should().Be(AuditOutcome.Succeeded);
        outcome.Detail.Should().Be("HTTP 200");
        outcome.OccurredUtc.Should().Be(Now.AddSeconds(3));

        // The context of the action travels with the outcome so it can be read on its own.
        outcome.TenantId.Should().Be(TenantId);
        outcome.ActorObjectId.Should().Be(Actor);
        outcome.ActorUpn.Should().Be("ann@contoso.example");
        outcome.Action.Should().Be(AuditAction.AutoRenewChanged);
        outcome.EntityType.Should().Be("Subscription");
        outcome.EntityId.Should().Be("sub-1");
        outcome.CorrelationId.Should().Be("corr");
        outcome.SourceIp.Should().Be("203.0.113.9");
        outcome.FinancialImpactAmount.Should().Be(99.99m);
        outcome.FinancialImpactCurrency.Should().Be("USD");
    }

    [Fact]
    public void Recording_an_outcome_leaves_the_attempt_unchanged()
    {
        var attempt = SavedAttempt();
        var updatedBefore = attempt.UpdatedUtc;

        _ = AuditLog.OutcomeOf(attempt, AuditOutcome.Failed, null, Now.AddMinutes(1));

        attempt.Outcome.Should().Be(AuditOutcome.Pending);
        attempt.UpdatedUtc.Should().Be(updatedBefore);
    }

    [Fact]
    public void An_unsaved_attempt_cannot_be_resolved()
    {
        // The outcome row points at the attempt's database id, and the attempt has to exist
        // before Microsoft is called anyway.
        var attempt = AuditLog.Attempt(TenantId, null, null, AuditAction.SubscriptionCancelled, "Subscription", null, "corr", Now);

        var act = () => AuditLog.OutcomeOf(attempt, AuditOutcome.Succeeded, null, Now);

        act.Should().Throw<DomainException>().WithMessage("*Save the attempt*");
    }

    [Fact]
    public void Only_an_attempt_can_be_resolved()
    {
        var single = Saved(
            AuditLog.ForSystem(TenantId, AuditAction.TenantConnected, "Tenant", null, AuditOutcome.Succeeded, "corr", Now),
            7);

        var act = () => AuditLog.OutcomeOf(single, AuditOutcome.Failed, null, Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void An_outcome_cannot_itself_be_resolved()
    {
        var outcome = Saved(AuditLog.OutcomeOf(SavedAttempt(), AuditOutcome.Succeeded, null, Now), 42);

        var act = () => AuditLog.OutcomeOf(outcome, AuditOutcome.Failed, null, Now);

        act.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(AuditOutcome.Unknown)]
    [InlineData(AuditOutcome.Pending)]
    public void An_outcome_row_must_carry_a_final_outcome(AuditOutcome outcome)
    {
        var act = () => AuditLog.OutcomeOf(SavedAttempt(), outcome, null, Now);

        act.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(AuditOutcome.Succeeded)]
    [InlineData(AuditOutcome.Failed)]
    [InlineData(AuditOutcome.Refused)]
    public void Every_final_outcome_is_accepted(AuditOutcome outcome)
    {
        AuditLog.OutcomeOf(SavedAttempt(), outcome, null, Now).Outcome.Should().Be(outcome);
    }

    [Fact]
    public void A_long_detail_is_truncated_rather_than_rejected()
    {
        // Losing the end of an error message is better than losing the outcome row.
        var outcome = AuditLog.OutcomeOf(SavedAttempt(), AuditOutcome.Failed, new string('x', 5000), Now);

        outcome.Detail.Should().HaveLength(AuditLog.MaxDetailLength);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "ann@contoso.example")]
    public void An_attempt_needs_both_parts_of_an_actor_or_neither(bool hasObjectId, string? upn)
    {
        var act = () => AuditLog.Attempt(
            TenantId, hasObjectId ? Actor : null, upn, AuditAction.AutoRenewChanged, "Subscription", null, "corr", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_single_row_action_needs_a_known_outcome()
    {
        var act = () => AuditLog.ForUser(
            TenantId, Actor, "ann@contoso.example", AuditAction.ConsentRequested, "Tenant", null, AuditOutcome.Unknown, "corr", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_existing_single_row_factories_still_work()
    {
        var user = AuditLog.ForUser(
            TenantId, Actor, "ann@contoso.example", AuditAction.ConsentRequested, "Tenant", null, AuditOutcome.Succeeded, "corr", Now);
        var system = AuditLog.ForSystem(TenantId, AuditAction.TenantConnected, "Tenant", null, AuditOutcome.Succeeded, "corr", Now);

        user.ActorUpn.Should().Be("ann@contoso.example");
        system.ActorObjectId.Should().BeNull();
        system.Outcome.Should().Be(AuditOutcome.Succeeded);
    }

    [Fact]
    public void The_entity_exposes_no_way_to_change_a_saved_row()
    {
        // Pins the removal of the old mutating method. Only the construction-time With*
        // builders may assign a property after creation.
        var mutators = typeof(AuditLog)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.ReturnType != typeof(AuditLog))
            .Select(m => m.Name)
            .ToList();

        mutators.Should().BeEmpty();

        typeof(AuditLog).GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true })
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(AuditOutcome.Pending, "Pending")]
    [InlineData(AuditOutcome.Succeeded, "Succeeded")]
    [InlineData(AuditOutcome.Failed, "Failed")]
    [InlineData(AuditOutcome.Refused, "Refused")]
    public void Outcome_names_are_stable_because_they_are_stored(AuditOutcome outcome, string stored)
    {
        outcome.ToString().Should().Be(stored);
    }
}
