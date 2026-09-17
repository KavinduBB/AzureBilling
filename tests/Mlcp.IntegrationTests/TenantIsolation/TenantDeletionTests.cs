using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;
using Mlcp.IntegrationTests.Infrastructure;
using Mlcp.Persistence.Stores;
using Mlcp.Shared.Tenancy;
using Xunit;

namespace Mlcp.IntegrationTests.TenantIsolation;

/// <summary>
/// P0-11: deleting a tenant removes all rows across all tables, verified against a real
/// database, and leaves an audit certificate behind.
/// </summary>
/// <remarks>
/// This has to run against SQL Server rather than a fake. The claim being tested is about what
/// remains in the database after a set-based delete inside a transaction, which a substitute
/// store cannot answer — and a promise to a departing customer that their data is gone is not
/// one to verify against a mock.
/// </remarks>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "TenantIsolation")]
public class TenantDeletionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public TenantDeletionTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>Creates a tenant with at least one row in every tenant-scoped table.</summary>
    private async Task<Guid> SeedFullTenantAsync()
    {
        var tenantId = Guid.NewGuid();

        await using var system = _fixture.CreateContext(FixedTenantContext.System);

        var tenant = Tenant.Register(tenantId, $"Tenant {tenantId:N}", "example.test", "westeurope", Now);
        tenant.ConfirmConsent(Guid.NewGuid(), Now);
        tenant.Activate(Now);
        tenant.BeginGracePeriod(Now.AddDays(-31), TimeSpan.FromDays(30));
        system.Tenants.Add(tenant);

        system.AppUsers.Add(AppUser.Create(tenantId, Guid.NewGuid(), "a@example.test", "A", AppRole.Owner, Now));
        system.OnboardingSteps.Add(OnboardingStep.Pending(tenantId, OnboardingStepName.CapabilityDiscovery, Now));
        system.PendingConsentRequests.Add(PendingConsentRequest.Create(
            tenantId, Guid.NewGuid(), "a@example.test", "admin@example.test", TimeSpan.FromDays(14), Now));
        system.TenantCapabilityProfiles.Add(TenantCapabilityProfile.Undiscovered(tenantId, Now));
        system.SyncRuns.Add(SyncRun.Start(tenantId, SyncJobType.LicenseSkuSync, "corr", Now));
        system.AuditLogs.Add(AuditLog.ForSystem(
            tenantId, AuditAction.TenantConnected, nameof(Tenant), tenantId.ToString(), AuditOutcome.Succeeded, "corr", Now));

        await system.SaveChangesAsync();

        return tenantId;
    }

    [RequiresDockerFact]
    public async Task Deleting_a_tenant_removes_every_row_it_owns()
    {
        var tenantId = await SeedFullTenantAsync();
        var store = new TenantDeletionStore(new TestSystemDbContextFactory(_fixture));

        var counts = await store.DeleteAllTenantDataAsync(tenantId, CancellationToken.None);

        counts.Values.Sum().Should().BeGreaterThan(0);

        await using var system = _fixture.CreateContext(FixedTenantContext.System);

        (await system.Tenants.CountAsync(t => t.TenantId == tenantId)).Should().Be(0);
        (await system.AppUsers.CountAsync(u => u.TenantId == tenantId)).Should().Be(0);
        (await system.OnboardingSteps.CountAsync(s => s.TenantId == tenantId)).Should().Be(0);
        (await system.PendingConsentRequests.CountAsync(r => r.TenantId == tenantId)).Should().Be(0);
        (await system.TenantCapabilityProfiles.CountAsync(p => p.TenantId == tenantId)).Should().Be(0);
        (await system.SyncRuns.CountAsync(r => r.TenantId == tenantId)).Should().Be(0);
        (await system.AuditLogs.CountAsync(a => a.TenantId == tenantId)).Should().Be(0);
    }

    [RequiresDockerFact]
    public async Task Deleting_one_tenant_leaves_another_untouched()
    {
        var doomed = await SeedFullTenantAsync();
        var survivor = await SeedFullTenantAsync();

        var store = new TenantDeletionStore(new TestSystemDbContextFactory(_fixture));
        await store.DeleteAllTenantDataAsync(doomed, CancellationToken.None);

        await using var system = _fixture.CreateContext(FixedTenantContext.System);

        (await system.Tenants.CountAsync(t => t.TenantId == survivor)).Should().Be(1);
        (await system.AppUsers.CountAsync(u => u.TenantId == survivor)).Should().Be(1);
        (await system.AuditLogs.CountAsync(a => a.TenantId == survivor)).Should().Be(1);
    }

    [RequiresDockerFact]
    public async Task The_counts_returned_match_what_was_actually_removed()
    {
        // The certificate is built from these numbers, so a count that overstates what went
        // would put a false claim into a permanent record.
        var tenantId = await SeedFullTenantAsync();
        var store = new TenantDeletionStore(new TestSystemDbContextFactory(_fixture));

        var counts = await store.DeleteAllTenantDataAsync(tenantId, CancellationToken.None);

        counts["Tenant"].Should().Be(1);
        counts["AppUser"].Should().Be(1);
        counts["AuditLog"].Should().Be(1);
        counts["SyncRun"].Should().Be(1);
        counts["OnboardingStep"].Should().Be(1);
        counts["PendingConsentRequest"].Should().Be(1);
        counts["TenantCapabilityProfile"].Should().Be(1);
    }

    [RequiresDockerFact]
    public async Task The_certificate_survives_the_tenant_it_describes()
    {
        // The whole point: after deletion we must still be able to show that we deleted it.
        var tenantId = await SeedFullTenantAsync();
        var factory = new TestSystemDbContextFactory(_fixture);
        var store = new TenantDeletionStore(factory);

        var counts = await store.DeleteAllTenantDataAsync(tenantId, CancellationToken.None);

        await store.AddCertificateAsync(
            DeletionCertificate.Issue(tenantId, "Gone", Now, counts, "corr", Now.AddDays(1)),
            CancellationToken.None);

        await using var system = _fixture.CreateContext(FixedTenantContext.System);

        var certificate = await system.DeletionCertificates.SingleAsync(c => c.TenantId == tenantId);
        certificate.RowsDeleted.Should().Be(counts.Values.Sum());
    }

    private sealed class TestSystemDbContextFactory : Mlcp.Persistence.ISystemDbContextFactory
    {
        private readonly SqlServerFixture _fixture;

        public TestSystemDbContextFactory(SqlServerFixture fixture) => _fixture = fixture;

        public Mlcp.Persistence.MlcpDbContext CreateDbContext()
            => _fixture.CreateContext(FixedTenantContext.System);
    }
}
