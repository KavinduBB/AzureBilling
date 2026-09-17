using Microsoft.EntityFrameworkCore;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;
using Mlcp.Shared.Tenancy;

namespace Mlcp.IntegrationTests.Infrastructure;

/// <summary>
/// Seeds a tenant with at least one row in every tenant-scoped table.
/// </summary>
/// <remarks>
/// The per-table isolation theory and the deletion tests both rely on this covering every table
/// in <c>TenantRlsScript.TenantScopedTables</c>. The theory checks that each table has rows for
/// the tenant, so a new table that is not seeded here fails loudly rather than passing an
/// empty-table check.
/// </remarks>
public static class TenantSeed
{
    public static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Audit rows written per tenant: one single-row action and an attempt/outcome pair.</summary>
    public const int AuditRowsPerTenant = 3;

    /// <summary>
    /// Seeds <paramref name="tenantId"/> unless it already exists.
    /// </summary>
    /// <param name="disconnectedDaysAgo">
    /// When set, the tenant is disconnected that many days before <see cref="Now"/> with a
    /// 30-day grace period, so it is due for deletion when the value exceeds 30.
    /// </param>
    public static async Task SeedAsync(SqlServerFixture fixture, Guid tenantId, int? disconnectedDaysAgo = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var system = fixture.CreateContext(FixedTenantContext.System);

        if (await system.Tenants.AnyAsync(t => t.TenantId == tenantId))
        {
            return;
        }

        var tenant = Tenant.Register(tenantId, $"Tenant {tenantId:N}", "example.test", "westeurope", Now.AddDays(-60));
        tenant.ConfirmConsent(Guid.NewGuid(), Now.AddDays(-60));
        tenant.Activate(Now.AddDays(-60));

        if (disconnectedDaysAgo is { } days)
        {
            tenant.BeginGracePeriod(Now.AddDays(-days), TimeSpan.FromDays(30));
        }

        system.Tenants.Add(tenant);

        var actor = Guid.NewGuid();
        system.AppUsers.Add(AppUser.Create(tenantId, actor, "a@example.test", "A", AppRole.Owner, Now));
        system.OnboardingSteps.Add(OnboardingStep.Pending(tenantId, OnboardingStepName.CapabilityDiscovery, Now));
        system.PendingConsentRequests.Add(PendingConsentRequest.Create(
            tenantId, Guid.NewGuid(), "a@example.test", "admin@example.test", TimeSpan.FromDays(14), Now));
        system.TenantCapabilityProfiles.Add(TenantCapabilityProfile.Undiscovered(tenantId, Now));
        system.SyncRuns.Add(SyncRun.Start(tenantId, SyncJobType.LicenseSkuSync, "corr", Now));
        system.SyncGateOverrides.Add(SyncGateOverride.Approve(
            tenantId, SyncJobType.LicenseSkuSync, "seeded override", "ops@example.test", Now));
        system.AuditLogs.Add(AuditLog.ForSystem(
            tenantId, AuditAction.TenantConnected, nameof(Tenant), tenantId.ToString(), AuditOutcome.Succeeded, "corr", Now));

        var attempt = AuditLog.Attempt(
                tenantId, actor, "a@example.test", AuditAction.AutoRenewChanged, "Subscription", "sub-1", "corr-write", Now)
            .WithValues("""{"autoRenew":true}""", """{"autoRenew":false}""")
            .WithFinancialImpact(-12.5m, "eur");
        system.AuditLogs.Add(attempt);

        await system.SaveChangesAsync();

        system.AuditLogs.Add(AuditLog.OutcomeOf(attempt, AuditOutcome.Succeeded, "HTTP 200", Now.AddSeconds(2)));
        await system.SaveChangesAsync();
    }
}
