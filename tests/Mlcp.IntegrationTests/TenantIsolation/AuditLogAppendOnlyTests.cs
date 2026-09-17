using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Mlcp.Domain.Audit;
using Mlcp.IntegrationTests.Infrastructure;
using Mlcp.Shared.Tenancy;
using Xunit;

namespace Mlcp.IntegrationTests.TenantIsolation;

/// <summary>
/// ADR-019's two-row audit model, against the real schema.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "TenantIsolation")]
public class AuditLogAppendOnlyTests
{
    private readonly SqlServerFixture _fixture;

    public AuditLogAppendOnlyTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [RequiresDockerFact]
    public async Task An_attempt_and_its_outcome_read_back_as_one_action()
    {
        var tenantId = Guid.NewGuid();
        await TenantSeed.SeedAsync(_fixture, tenantId);

        await using var asTenant = _fixture.CreateContext(FixedTenantContext.For(tenantId));

        var entries = await asTenant.AuditLogs.WithOutcome().ToListAsync();

        // The seed writes one single-row action and one attempt with its outcome.
        entries.Should().HaveCount(2);

        var write = entries.Should().ContainSingle(e => e.Row.Action == AuditAction.AutoRenewChanged).Subject;
        write.Row.Outcome.Should().Be(AuditOutcome.Pending, "the attempt row is never updated");
        write.Resolution.Should().NotBeNull();
        write.Resolution!.AttemptAuditLogId.Should().Be(write.Row.AuditLogId);
        write.EffectiveOutcome.Should().Be(AuditOutcome.Succeeded);
        write.Resolution.FinancialImpactAmount.Should().Be(-12.5m);
        write.Resolution.FinancialImpactCurrency.Should().Be("EUR");

        var single = entries.Should().ContainSingle(e => e.Row.Action == AuditAction.TenantConnected).Subject;
        single.Resolution.Should().BeNull();
        single.EffectiveOutcome.Should().Be(AuditOutcome.Succeeded);
    }

    [RequiresDockerFact]
    public async Task An_attempt_whose_call_never_returned_stays_pending()
    {
        var tenantId = Guid.NewGuid();
        await TenantSeed.SeedAsync(_fixture, tenantId);

        await using var asTenant = _fixture.CreateContext(FixedTenantContext.For(tenantId));

        asTenant.AuditLogs.Add(AuditLog.Attempt(
            tenantId, null, null, AuditAction.SubscriptionCancelled, "Subscription", "sub-2", "corr-lost", TenantSeed.Now));
        await asTenant.SaveChangesAsync();

        var entry = await asTenant.AuditLogs
            .Where(a => a.CorrelationId == "corr-lost")
            .WithOutcome()
            .SingleAsync();

        entry.Resolution.Should().BeNull();
        entry.EffectiveOutcome.Should().Be(AuditOutcome.Pending);
    }

    [RequiresDockerFact]
    public async Task An_attempt_cannot_be_resolved_twice()
    {
        var tenantId = Guid.NewGuid();
        await TenantSeed.SeedAsync(_fixture, tenantId);

        await using var asTenant = _fixture.CreateContext(FixedTenantContext.For(tenantId));

        var attempt = await asTenant.AuditLogs.SingleAsync(a => a.Outcome == AuditOutcome.Pending);

        asTenant.AuditLogs.Add(AuditLog.OutcomeOf(attempt, AuditOutcome.Failed, "second opinion", TenantSeed.Now));

        var act = async () => await asTenant.SaveChangesAsync();

        (await act.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<SqlException>();
    }

    [RequiresDockerFact]
    public async Task Deleting_a_tenant_row_does_not_cascade_to_its_audit_log()
    {
        // ADR-019: an accidental tenant delete must fail rather than silently erase history.
        var tenantId = Guid.NewGuid();
        await TenantSeed.SeedAsync(_fixture, tenantId);

        await using var system = _fixture.CreateContext(FixedTenantContext.System);

        var act = async () => await system.Database.ExecuteSqlAsync($"""
            DELETE FROM dbo.AppUser WHERE TenantId = {tenantId};
            DELETE FROM dbo.OnboardingStep WHERE TenantId = {tenantId};
            DELETE FROM dbo.PendingConsentRequest WHERE TenantId = {tenantId};
            DELETE FROM dbo.SyncRun WHERE TenantId = {tenantId};
            DELETE FROM dbo.TenantCapabilityProfile WHERE TenantId = {tenantId};
            DELETE FROM dbo.Tenant WHERE TenantId = {tenantId};
            """);

        (await act.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(547, "547 is a foreign-key conflict");

        (await system.AuditLogs.CountAsync(a => a.TenantId == tenantId)).Should().Be(TenantSeed.AuditRowsPerTenant);
    }
}
