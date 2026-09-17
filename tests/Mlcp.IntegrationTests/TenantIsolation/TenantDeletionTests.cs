using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Common;
using Mlcp.Domain.Tenancy;
using Mlcp.IntegrationTests.Infrastructure;
using Mlcp.Persistence.Rls;
using Mlcp.Persistence.Stores;
using Mlcp.Shared.Tenancy;
using Xunit;

namespace Mlcp.IntegrationTests.TenantIsolation;

/// <summary>
/// P0-11 and ADR-019: deleting a tenant removes every row in every table, in one transaction,
/// together with the certificate that records it. Verified against a real database.
/// </summary>
/// <remarks>
/// This has to run against SQL Server rather than a fake. Three of the claims tested depend on
/// the real database's behaviour: what remains after a set-based delete inside a transaction,
/// how two sweeps contend for a row lock, and what a unique index refuses. A substitute store
/// cannot answer any of them.
/// </remarks>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "TenantIsolation")]
public class TenantDeletionTests
{
    /// <summary>
    /// The sweep's clock. A tenant seeded as disconnected 31 days before this, with a 30-day
    /// grace period, is one day overdue.
    /// </summary>
    private static readonly DateTimeOffset SweepTime = TenantSeed.Now;

    private readonly SqlServerFixture _fixture;

    public TenantDeletionTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Guid> SeedDueTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await TenantSeed.SeedAsync(_fixture, tenantId, disconnectedDaysAgo: 31);
        return tenantId;
    }

    private TenantDeletionStore Store(string? databaseUser = null)
        => new(new TestSystemDbContextFactory(_fixture, databaseUser));

    private async Task<Dictionary<string, long>> CountRowsAsync(Guid tenantId)
    {
        await using var system = _fixture.CreateContext(FixedTenantContext.System);

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var table in TenantRlsScript.TenantScopedTables)
        {
            // Table names come from the compiled list, never from input.
            var sql = "SELECT COUNT_BIG(*) AS Value FROM [dbo].[" + table + "] WHERE [TenantId] = @tenantId";

            counts[table] = await system.Database
                .SqlQueryRaw<long>(sql, new Microsoft.Data.SqlClient.SqlParameter("@tenantId", tenantId))
                .SingleAsync();
        }

        return counts;
    }

    private async Task<List<DeletionCertificate>> CertificatesForAsync(Guid tenantId)
    {
        await using var system = _fixture.CreateContext(FixedTenantContext.System);
        return await system.DeletionCertificates.Where(c => c.TenantId == tenantId).ToListAsync();
    }

    [RequiresDockerFact]
    public async Task Deleting_a_tenant_removes_every_row_it_owns_in_every_table()
    {
        var tenantId = await SeedDueTenantAsync();
        (await CountRowsAsync(tenantId)).Values.Should().OnlyContain(c => c > 0, "the seed covers every table");

        var certificate = await Store().DeleteAllTenantDataAsync(tenantId, SweepTime, "corr-del", CancellationToken.None);

        certificate.Should().NotBeNull();
        (await CountRowsAsync(tenantId)).Values.Should().OnlyContain(c => c == 0);
    }

    [RequiresDockerFact]
    public async Task The_certificate_is_written_with_the_deletion_and_records_what_went()
    {
        var tenantId = await SeedDueTenantAsync();

        var certificate = await Store().DeleteAllTenantDataAsync(tenantId, SweepTime, "corr-cert", CancellationToken.None);

        var stored = (await CertificatesForAsync(tenantId)).Should().ContainSingle().Subject;
        stored.DeletionCertificateId.Should().Be(certificate!.DeletionCertificateId);
        stored.CorrelationId.Should().Be("corr-cert");
        stored.DeletedUtc.Should().Be(SweepTime);

        // The disconnect date, not the scheduled deletion date, is what the certificate records.
        stored.DisconnectedUtc.Should().Be(TenantSeed.Now.AddDays(-31));

        var counts = DomainJson.Deserialize<Dictionary<string, int>>(stored.RowCountsByTable)!;
        counts.Keys.Should().BeEquivalentTo(TenantRlsScript.TenantScopedTables);
        counts["Tenant"].Should().Be(1);
        counts["AppUser"].Should().Be(1);
        counts["AuditLog"].Should().Be(TenantSeed.AuditRowsPerTenant);
        counts["SyncRun"].Should().Be(1);
        counts["OnboardingStep"].Should().Be(1);
        counts["PendingConsentRequest"].Should().Be(1);
        counts["TenantCapabilityProfile"].Should().Be(1);
        stored.RowsDeleted.Should().Be(counts.Values.Sum());
    }

    [RequiresDockerFact]
    public async Task The_certificate_carries_the_digest_of_the_audit_log_as_it_was()
    {
        var tenantId = await SeedDueTenantAsync();

        List<AuditLog> before;

        await using (var system = _fixture.CreateContext(FixedTenantContext.System))
        {
            before = await system.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenantId).ToListAsync();
        }

        var certificate = await Store().DeleteAllTenantDataAsync(tenantId, SweepTime, "corr-digest", CancellationToken.None);

        certificate!.AuditLogSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        certificate.AuditLogSha256.Should().Be(AuditLogDigest.Compute(before), "anyone with an export can reproduce it");

        (await CertificatesForAsync(tenantId)).Single().AuditLogSha256.Should().Be(certificate.AuditLogSha256);
    }

    [RequiresDockerFact]
    public async Task Deleting_one_tenant_leaves_another_untouched()
    {
        var doomed = await SeedDueTenantAsync();
        var survivor = await SeedDueTenantAsync();
        var survivorBefore = await CountRowsAsync(survivor);

        await Store().DeleteAllTenantDataAsync(doomed, SweepTime, "corr-one", CancellationToken.None);

        (await CountRowsAsync(survivor)).Should().Equal(survivorBefore);
        (await CertificatesForAsync(survivor)).Should().BeEmpty();
    }

    [RequiresDockerFact]
    public async Task A_tenant_whose_grace_period_has_not_ended_is_not_deleted()
    {
        var tenantId = Guid.NewGuid();
        await TenantSeed.SeedAsync(_fixture, tenantId, disconnectedDaysAgo: 5);
        var before = await CountRowsAsync(tenantId);

        var certificate = await Store().DeleteAllTenantDataAsync(tenantId, SweepTime, "corr-early", CancellationToken.None);

        certificate.Should().BeNull();
        (await CountRowsAsync(tenantId)).Should().Equal(before);
        (await CertificatesForAsync(tenantId)).Should().BeEmpty();
    }

    [RequiresDockerFact]
    public async Task A_tenant_that_reconnected_after_the_sweep_found_it_is_not_deleted()
    {
        // The race the eligibility re-check exists for: the candidate list is stale by the time
        // the delete runs.
        var tenantId = await SeedDueTenantAsync();
        var store = Store();

        var candidates = await store.FindTenantsDueForDeletionAsync(SweepTime, CancellationToken.None);
        candidates.Should().Contain(t => t.TenantId == tenantId);

        await using (var system = _fixture.CreateContext(FixedTenantContext.System))
        {
            var tenant = await system.Tenants.SingleAsync(t => t.TenantId == tenantId);
            tenant.CancelDeletion(SweepTime);
            await system.SaveChangesAsync();
        }

        var before = await CountRowsAsync(tenantId);

        var certificate = await store.DeleteAllTenantDataAsync(tenantId, SweepTime, "corr-race", CancellationToken.None);

        certificate.Should().BeNull();
        (await CountRowsAsync(tenantId)).Should().Equal(before);
        (await CertificatesForAsync(tenantId)).Should().BeEmpty();
    }

    [RequiresDockerFact]
    public async Task Deleting_an_already_deleted_tenant_does_nothing()
    {
        var tenantId = await SeedDueTenantAsync();
        var store = Store();

        (await store.DeleteAllTenantDataAsync(tenantId, SweepTime, "corr-first", CancellationToken.None)).Should().NotBeNull();
        (await store.DeleteAllTenantDataAsync(tenantId, SweepTime, "corr-second", CancellationToken.None)).Should().BeNull();

        (await CertificatesForAsync(tenantId)).Should().ContainSingle().Which.CorrelationId.Should().Be("corr-first");
    }

    [RequiresDockerFact]
    public async Task Concurrent_sweeps_delete_once_and_issue_one_certificate()
    {
        var tenantId = await SeedDueTenantAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() =>
            Store().DeleteAllTenantDataAsync(tenantId, SweepTime, $"corr-concurrent-{i}", CancellationToken.None))));

        results.Where(r => r is not null).Should().ContainSingle();
        (await CertificatesForAsync(tenantId)).Should().ContainSingle();
        (await CountRowsAsync(tenantId)).Values.Should().OnlyContain(c => c == 0);
    }

    [RequiresDockerFact]
    public async Task The_database_refuses_a_second_certificate_for_the_same_disconnection()
    {
        // Belt and braces behind the row lock: even a code path that skipped the lock could not
        // record the same deletion twice.
        var tenantId = await SeedDueTenantAsync();
        var certificate = await Store().DeleteAllTenantDataAsync(tenantId, SweepTime, "corr-unique", CancellationToken.None);

        await using var system = _fixture.CreateContext(FixedTenantContext.System);

        system.DeletionCertificates.Add(DeletionCertificate.Issue(
            tenantId,
            "Duplicate",
            certificate!.DisconnectedUtc,
            new Dictionary<string, int> { ["AuditLog"] = 0 },
            new string('0', 64),
            "corr-duplicate",
            SweepTime));

        var act = async () => await system.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [RequiresDockerFact]
    public async Task The_worker_database_role_has_every_right_deletion_needs()
    {
        // Runs the real store as the least-privileged worker user, so a missing grant (or a
        // lock hint that needs one) fails here rather than in the first production sweep.
        var tenantId = await SeedDueTenantAsync();

        var certificate = await Store(SqlServerFixture.WorkerUser)
            .DeleteAllTenantDataAsync(tenantId, SweepTime, "corr-worker", CancellationToken.None);

        certificate.Should().NotBeNull();
        (await CountRowsAsync(tenantId)).Values.Should().OnlyContain(c => c == 0);
        (await CertificatesForAsync(tenantId)).Should().ContainSingle();
    }

    [RequiresDockerFact]
    public async Task The_web_database_role_cannot_run_a_deletion()
    {
        var tenantId = await SeedDueTenantAsync();
        var before = await CountRowsAsync(tenantId);

        var act = async () => await Store(SqlServerFixture.WebUser)
            .DeleteAllTenantDataAsync(tenantId, SweepTime, "corr-web", CancellationToken.None);

        // The web user is not in mlcp_system, so the system context sees no tenant at all.
        (await act()).Should().BeNull();
        (await CountRowsAsync(tenantId)).Should().Equal(before);
    }
}
