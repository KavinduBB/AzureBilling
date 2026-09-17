using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mlcp.Application.Onboarding;
using Mlcp.Domain.Common;
using Mlcp.Domain.Tenancy;

namespace Mlcp.UnitTests.Onboarding;

/// <summary>
/// P0-11: the deletion sweep deletes each due tenant, which also certifies it, and survives
/// one tenant failing.
/// </summary>
/// <remarks>
/// The behaviour that matters most here is the failure path. A sweep that abandons the batch on
/// the first error would leave later tenants undeleted while we had already told them their data
/// was gone, so per-tenant isolation is asserted explicitly. Atomicity of the deletion and the
/// certificate is the store's job and is covered by the SQL-backed tenant-isolation suite.
/// </remarks>
public class TenantDeletionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private static readonly string Digest = new('a', 64);

    private sealed class FakeStore : ITenantDeletionStore
    {
        public List<Tenant> Due { get; } = [];

        public List<(Guid TenantId, DateTimeOffset AsOf, string CorrelationId)> Calls { get; } = [];

        public HashSet<Guid> FailFor { get; } = [];

        /// <summary>Tenants the store finds no longer eligible once locked.</summary>
        public HashSet<Guid> NoLongerEligible { get; } = [];

        public Task<IReadOnlyList<Tenant>> FindTenantsDueForDeletionAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<Tenant>>(Due);

        public Task<DeletionCertificate?> DeleteAllTenantDataAsync(
            Guid tenantId,
            DateTimeOffset asOfUtc,
            string correlationId,
            CancellationToken cancellationToken)
        {
            Calls.Add((tenantId, asOfUtc, correlationId));

            if (FailFor.Contains(tenantId))
            {
                throw new InvalidOperationException("Simulated database failure.");
            }

            if (NoLongerEligible.Contains(tenantId))
            {
                return Task.FromResult<DeletionCertificate?>(null);
            }

            var tenant = Due.Single(t => t.TenantId == tenantId);

            return Task.FromResult<DeletionCertificate?>(DeletionCertificate.Issue(
                tenantId,
                tenant.DisplayName,
                tenant.DisconnectedUtc!.Value,
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["AppUser"] = 3,
                    ["AuditLog"] = 5,
                    ["SyncRun"] = 12,
                    ["Tenant"] = 1,
                },
                Digest,
                correlationId,
                asOfUtc));
        }
    }

    private sealed class RecordingLogger : ILogger<TenantDeletionService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static Tenant ExpiredTenant(Guid id, string name, FakeTimeProvider clock)
    {
        var tenant = Tenant.Register(id, name, null, "westeurope", clock.GetUtcNow().AddDays(-60));
        tenant.ConfirmConsent(Guid.NewGuid(), clock.GetUtcNow().AddDays(-60));
        tenant.Activate(clock.GetUtcNow().AddDays(-60));
        tenant.BeginGracePeriod(clock.GetUtcNow().AddDays(-31), TimeSpan.FromDays(30));
        return tenant;
    }

    private static (TenantDeletionService Service, FakeStore Store, FakeTimeProvider Clock, RecordingLogger Logger) Build()
    {
        var store = new FakeStore();
        var clock = new FakeTimeProvider(Now);
        var logger = new RecordingLogger();

        return (new TenantDeletionService(store, clock, logger), store, clock, logger);
    }

    [Fact]
    public async Task An_empty_sweep_does_nothing()
    {
        var (service, store, _, _) = Build();

        var certificates = await service.RunAsync(CancellationToken.None);

        certificates.Should().BeEmpty();
        store.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_expired_tenant_is_deleted_and_certified_by_the_store()
    {
        var (service, store, clock, _) = Build();
        var tenantId = Guid.NewGuid();
        store.Due.Add(ExpiredTenant(tenantId, "Contoso", clock));

        var certificates = await service.RunAsync(CancellationToken.None);

        var call = store.Calls.Should().ContainSingle().Subject;
        call.TenantId.Should().Be(tenantId);
        call.AsOf.Should().Be(Now, "the store re-checks eligibility as of the sweep's clock");
        call.CorrelationId.Should().NotBeNullOrWhiteSpace();

        certificates.Should().ContainSingle();
        certificates[0].TenantId.Should().Be(tenantId);
        certificates[0].DeletedUtc.Should().Be(Now);
        certificates[0].CorrelationId.Should().Be(call.CorrelationId);
    }

    [Fact]
    public async Task The_certificate_records_what_was_removed()
    {
        // A certificate saying only "deleted" answers nothing. The counts are what make it
        // possible to say specifically what went.
        var (service, store, clock, _) = Build();
        store.Due.Add(ExpiredTenant(Guid.NewGuid(), "Contoso", clock));

        var certificates = await service.RunAsync(CancellationToken.None);

        certificates[0].RowsDeleted.Should().Be(21);
        certificates[0].TenantDisplayName.Should().Be("Contoso");
        certificates[0].DisconnectedUtc.Should().Be(Now.AddDays(-31), "the disconnect date, not the scheduled deletion date");

        var counts = DomainJson.Deserialize<Dictionary<string, int>>(certificates[0].RowCountsByTable);
        counts.Should().ContainKey("AppUser").WhoseValue.Should().Be(3);
        counts.Should().ContainKey("AuditLog").WhoseValue.Should().Be(5);
    }

    [Fact]
    public async Task One_tenant_failing_does_not_stop_the_others()
    {
        var (service, store, clock, _) = Build();
        var failing = Guid.NewGuid();
        var succeeding = Guid.NewGuid();

        store.Due.Add(ExpiredTenant(failing, "Broken", clock));
        store.Due.Add(ExpiredTenant(succeeding, "Fine", clock));
        store.FailFor.Add(failing);

        var certificates = await service.RunAsync(CancellationToken.None);

        store.Calls.Select(c => c.TenantId).Should().Equal(failing, succeeding);
        certificates.Should().ContainSingle().Which.TenantId.Should().Be(succeeding);
    }

    [Fact]
    public async Task No_certificate_is_reported_when_the_deletion_failed()
    {
        // A certificate is a claim that the data is gone. Reporting one for a failed deletion
        // would make the record actively false rather than merely incomplete.
        var (service, store, clock, logger) = Build();
        var failing = Guid.NewGuid();

        store.Due.Add(ExpiredTenant(failing, "Broken", clock));
        store.FailFor.Add(failing);

        var certificates = await service.RunAsync(CancellationToken.None);

        certificates.Should().BeEmpty();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains(failing.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tenant_that_is_no_longer_eligible_is_skipped_without_error()
    {
        // The store found it reconnected, or already deleted by another sweep. Nothing happened,
        // and the log must say so rather than claim a deletion.
        var (service, store, clock, logger) = Build();
        var reconnected = Guid.NewGuid();

        store.Due.Add(ExpiredTenant(reconnected, "Returned", clock));
        store.NoLongerEligible.Add(reconnected);

        var certificates = await service.RunAsync(CancellationToken.None);

        certificates.Should().BeEmpty();
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        logger.Entries.Should().Contain(e => e.Message.Contains("nothing was deleted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_candidate_that_is_not_actually_due_is_never_sent_to_the_store()
    {
        var (service, store, clock, _) = Build();
        var tenant = Tenant.Register(Guid.NewGuid(), "Contoso", null, "westeurope", clock.GetUtcNow());
        tenant.ConfirmConsent(Guid.NewGuid(), clock.GetUtcNow());
        tenant.Activate(clock.GetUtcNow());
        tenant.BeginGracePeriod(clock.GetUtcNow(), TimeSpan.FromDays(30));
        store.Due.Add(tenant);

        var certificates = await service.RunAsync(CancellationToken.None);

        certificates.Should().BeEmpty();
        store.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_stops_the_sweep()
    {
        var (_, store, clock, _) = Build();
        store.Due.Add(ExpiredTenant(Guid.NewGuid(), "Contoso", clock));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var cancellingStore = new CancellingStore(store);
        var cancellingService = new TenantDeletionService(cancellingStore, clock, NullLogger<TenantDeletionService>.Instance);

        var act = async () => await cancellingService.RunAsync(cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
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
        tenant.IsDueForDeletion(clock.GetUtcNow()).Should().BeFalse();
    }

    private sealed class CancellingStore(FakeStore inner) : ITenantDeletionStore
    {
        public Task<IReadOnlyList<Tenant>> FindTenantsDueForDeletionAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken)
            => inner.FindTenantsDueForDeletionAsync(asOfUtc, cancellationToken);

        public Task<DeletionCertificate?> DeleteAllTenantDataAsync(
            Guid tenantId,
            DateTimeOffset asOfUtc,
            string correlationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return inner.DeleteAllTenantDataAsync(tenantId, asOfUtc, correlationId, cancellationToken);
        }
    }
}
