using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Common;
using Mlcp.Domain.Tenancy;

namespace Mlcp.UnitTests.Onboarding;

/// <summary>
/// P0-11: deleting a tenant removes all of its rows and writes an audit certificate.
/// </summary>
/// <remarks>
/// The behaviour that matters most here is the failure path. A sweep that abandons the batch on
/// the first error would leave later tenants undeleted while we had already told them their data
/// was gone, so per-tenant isolation is asserted explicitly.
/// </remarks>
public class TenantDeletionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeStore : ITenantDeletionStore
    {
        public List<Tenant> Due { get; } = [];

        public List<Guid> Deleted { get; } = [];

        public List<DeletionCertificate> Certificates { get; } = [];

        public HashSet<Guid> FailFor { get; } = [];

        public Task<IReadOnlyList<Tenant>> FindTenantsDueForDeletionAsync(DateTimeOffset asOf, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Tenant>>(Due);

        public Task<IReadOnlyDictionary<string, int>> DeleteAllTenantDataAsync(Guid tenantId, CancellationToken ct)
        {
            if (FailFor.Contains(tenantId))
            {
                throw new InvalidOperationException("Simulated database failure.");
            }

            Deleted.Add(tenantId);

            return Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["AppUser"] = 3,
                ["SyncRun"] = 12,
                ["Tenant"] = 1,
            });
        }

        public Task AddCertificateAsync(DeletionCertificate certificate, CancellationToken ct)
        {
            Certificates.Add(certificate);
            return Task.CompletedTask;
        }
    }

    private static Tenant ExpiredTenant(Guid id, string name, FakeTimeProvider clock)
    {
        var tenant = Tenant.Register(id, name, null, "westeurope", clock.GetUtcNow().AddDays(-60));
        tenant.ConfirmConsent(Guid.NewGuid(), clock.GetUtcNow().AddDays(-60));
        tenant.Activate(clock.GetUtcNow().AddDays(-60));
        tenant.BeginGracePeriod(clock.GetUtcNow().AddDays(-31), TimeSpan.FromDays(30));
        return tenant;
    }

    private static (TenantDeletionService Service, FakeStore Store, FakeTimeProvider Clock) Build()
    {
        var store = new FakeStore();
        var clock = new FakeTimeProvider(Now);

        return (new TenantDeletionService(store, clock, NullLogger<TenantDeletionService>.Instance), store, clock);
    }

    [Fact]
    public async Task An_empty_sweep_does_nothing()
    {
        var (service, store, _) = Build();

        var certificates = await service.RunAsync(CancellationToken.None);

        certificates.Should().BeEmpty();
        store.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task An_expired_tenant_is_deleted_and_certified()
    {
        var (service, store, clock) = Build();
        var tenantId = Guid.NewGuid();
        store.Due.Add(ExpiredTenant(tenantId, "Contoso", clock));

        var certificates = await service.RunAsync(CancellationToken.None);

        store.Deleted.Should().ContainSingle().Which.Should().Be(tenantId);
        certificates.Should().ContainSingle();
        certificates[0].TenantId.Should().Be(tenantId);
        certificates[0].DeletedUtc.Should().Be(Now);
    }

    [Fact]
    public async Task The_certificate_records_what_was_removed()
    {
        // A certificate saying only "deleted" answers nothing. The counts are what make it
        // possible to say specifically what went.
        var (service, store, clock) = Build();
        store.Due.Add(ExpiredTenant(Guid.NewGuid(), "Contoso", clock));

        var certificates = await service.RunAsync(CancellationToken.None);

        certificates[0].RowsDeleted.Should().Be(16);
        certificates[0].TenantDisplayName.Should().Be("Contoso");

        var counts = DomainJson.Deserialize<Dictionary<string, int>>(certificates[0].RowCountsByTable);
        counts.Should().ContainKey("AppUser").WhoseValue.Should().Be(3);
    }

    [Fact]
    public async Task One_tenant_failing_does_not_stop_the_others()
    {
        var (service, store, clock) = Build();
        var failing = Guid.NewGuid();
        var succeeding = Guid.NewGuid();

        store.Due.Add(ExpiredTenant(failing, "Broken", clock));
        store.Due.Add(ExpiredTenant(succeeding, "Fine", clock));
        store.FailFor.Add(failing);

        var certificates = await service.RunAsync(CancellationToken.None);

        store.Deleted.Should().ContainSingle().Which.Should().Be(succeeding);
        certificates.Should().ContainSingle().Which.TenantId.Should().Be(succeeding);
    }

    [Fact]
    public async Task No_certificate_is_issued_when_the_deletion_failed()
    {
        // A certificate is a claim that the data is gone. Issuing one for a failed deletion
        // would make the record actively false rather than merely incomplete.
        var (service, store, clock) = Build();
        var failing = Guid.NewGuid();

        store.Due.Add(ExpiredTenant(failing, "Broken", clock));
        store.FailFor.Add(failing);

        var certificates = await service.RunAsync(CancellationToken.None);

        certificates.Should().BeEmpty();
        store.Certificates.Should().BeEmpty();
    }

    [Fact]
    public void A_tenant_still_inside_its_grace_period_is_not_due()
    {
        var clock = new FakeTimeProvider(Now);
        var tenant = Tenant.Register(Guid.NewGuid(), "Contoso", null, "westeurope", clock.GetUtcNow());
        tenant.ConfirmConsent(Guid.NewGuid(), clock.GetUtcNow());
        tenant.Activate(clock.GetUtcNow());
        tenant.BeginGracePeriod(clock.GetUtcNow(), TimeSpan.FromDays(30));

        tenant.DeleteScheduledUtc.Should().Be(Now.AddDays(30));
        tenant.DeleteScheduledUtc.Should().BeAfter(clock.GetUtcNow());
    }
}
