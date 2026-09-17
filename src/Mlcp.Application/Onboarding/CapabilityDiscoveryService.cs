using Microsoft.Extensions.Logging;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Tenancy;
using Mlcp.Shared.Resilience;

namespace Mlcp.Application.Onboarding;

/// <summary>Persistence of the pieces capability discovery writes.</summary>
public interface ITenantOnboardingStore
{
    Task<Tenant?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<TenantCapabilityProfile?> FindCapabilityProfileAsync(Guid tenantId, CancellationToken cancellationToken);

    Task AddCapabilityProfileAsync(TenantCapabilityProfile profile, CancellationToken cancellationToken);

    Task<OnboardingStep> GetOrCreateStepAsync(Guid tenantId, OnboardingStepName step, CancellationToken cancellationToken);

    /// <summary>Adds an audit row on the same context, saved with the tenant change it records (ADR-019).</summary>
    Task AddAuditAsync(AuditLog entry, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>How a discovery run ended.</summary>
public enum DiscoveryStatus
{
    /// <summary>Probes ran and the profile was updated; the floor is readable.</summary>
    Completed = 0,

    /// <summary>The tenant is not sync-eligible; no Microsoft call was made.</summary>
    Skipped = 1,

    /// <summary>The floor was refused (GrantRevoked / FloorPermissionRemoved); the tenant was flagged.</summary>
    TenantNeedsReconsent = 2,

    /// <summary>The floor probe was inconclusive (transient). Previous verdicts kept; retry later.</summary>
    Inconclusive = 3,
}

/// <summary>The result of <see cref="CapabilityDiscoveryService.DiscoverAsync"/>.</summary>
/// <param name="Status">How the run ended.</param>
/// <param name="Profile">The profile after the run, or null when skipped before loading it.</param>
/// <param name="RetryAfter">For <see cref="DiscoveryStatus.Inconclusive"/>: when to try again.</param>
public sealed record DiscoveryOutcome(DiscoveryStatus Status, TenantCapabilityProfile? Profile, TimeSpan? RetryAfter = null)
{
    public bool ShouldRetry => Status == DiscoveryStatus.Inconclusive;
}

/// <summary>The result of an automatic floor re-probe (ADR-016 rule 5).</summary>
public enum FloorReprobeStatus
{
    /// <summary>The tenant is not in NeedsReconsent; nothing was called.</summary>
    Skipped = 0,

    /// <summary>The floor answered; the tenant is Active again.</summary>
    Restored = 1,

    /// <summary>The floor is still refused, or the probe was inconclusive; the next probe is scheduled.</summary>
    StillFailing = 2,
}

/// <summary>
/// Runs the probes, resolves them into a capability profile, and owns every change to the tenant
/// that follows from a Microsoft answer (P0-7, ADR-016 rule 2).
/// </summary>
/// <remarks>
/// <para>
/// Each probe sub-call is classified independently. Only a floor refusal (GrantRevoked or
/// FloorPermissionRemoved) moves the tenant to NeedsReconsent, and it does so here, on the same
/// DbContext that loaded the tenant. A refused non-floor call degrades only its capability, and a
/// capability that was available becomes <see cref="CapabilityUnavailableReason.RoleRevoked"/>. A
/// transient failure keeps the previous verdict, marked stale (ADR-016).
/// </para>
/// <para>
/// The operation is idempotent: it runs on connect, weekly, on each unlock and after a re-consent
/// probe, and running it twice reaches the same profile.
/// </para>
/// </remarks>
public sealed class CapabilityDiscoveryService
{
    /// <summary>How long a profile stands before the scheduler re-probes it anyway.</summary>
    public static TimeSpan DefaultProfileInterval { get; } = TimeSpan.FromDays(7);

    /// <summary>After an inconclusive floor probe, the scheduler picks the tenant up again after this.</summary>
    public static TimeSpan InconclusiveRetryInterval { get; } = TimeSpan.FromHours(1);

    /// <summary>Default wait before retrying an inconclusive run (ADR-016 rule 3: 2 minutes after consent).</summary>
    public static TimeSpan DefaultRetryAfter { get; } = TimeSpan.FromMinutes(2);

    private readonly ITenantOnboardingStore _store;
    private readonly IGraphCapabilityProbe _graphProbe;
    private readonly IAzureCapabilityProbe _azureProbe;
    private readonly IBillingCapabilityProbe _billingProbe;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CapabilityDiscoveryService> _logger;

    public CapabilityDiscoveryService(
        ITenantOnboardingStore store,
        IGraphCapabilityProbe graphProbe,
        IAzureCapabilityProbe azureProbe,
        IBillingCapabilityProbe billingProbe,
        TimeProvider timeProvider,
        ILogger<CapabilityDiscoveryService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _graphProbe = graphProbe ?? throw new ArgumentNullException(nameof(graphProbe));
        _azureProbe = azureProbe ?? throw new ArgumentNullException(nameof(azureProbe));
        _billingProbe = billingProbe ?? throw new ArgumentNullException(nameof(billingProbe));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DiscoveryOutcome> DiscoverAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await _store.FindTenantAsync(tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tenant {tenantId} is not registered.");

        // NotConnected, ConsentPendingVerification, NeedsReconsent, GracePeriod and Deleted: no
        // Microsoft calls. Re-consent recovery is ReprobeFloorAsync, not a full discovery.
        if (!tenant.IsSyncEligible)
        {
            _logger.LogInformation(
                "Skipping capability discovery for tenant {TenantId}: status {Status} is not sync-eligible.",
                tenantId,
                tenant.Status);

            return new DiscoveryOutcome(DiscoveryStatus.Skipped, Profile: null);
        }

        var now = _timeProvider.GetUtcNow();
        var context = new ProbeContext(tenantId, tenant.ConsentCallbackUtc ?? tenant.ConsentGrantedUtc);

        var step = await _store.GetOrCreateStepAsync(tenantId, OnboardingStepName.CapabilityDiscovery, cancellationToken)
            .ConfigureAwait(false);

        step.Begin(now);

        var graph = await ProbeSafelyAsync(
            ct => _graphProbe.ProbeAsync(context, ct), GraphProbeResult.Inconclusive, tenantId, "Graph", cancellationToken)
            .ConfigureAwait(false);

        var floor = graph.FloorOutcome;

        // With the core grant gone every ARM and billing call would fail the same way; skip them
        // and keep their previous verdicts.
        var arm = floor.RequiresReconsent
            ? ArmProbeResult.Inconclusive(ProbeCallOutcome.NotAttempted)
            : await ProbeSafelyAsync(
                ct => _azureProbe.ProbeAsync(context, ct), ArmProbeResult.Inconclusive, tenantId, "ARM", cancellationToken)
                .ConfigureAwait(false);

        var billing = floor.RequiresReconsent
            ? BillingProbeResult.Inconclusive(ProbeCallOutcome.NotAttempted)
            : await ProbeSafelyAsync(
                ct => _billingProbe.ProbeAsync(context, ct), BillingProbeResult.Inconclusive, tenantId, "Billing", cancellationToken)
                .ConfigureAwait(false);

        var resolution = CapabilityResolver.Resolve(graph, arm, billing, tenantId);

        var profile = await _store.FindCapabilityProfileAsync(tenantId, cancellationToken).ConfigureAwait(false);

        if (profile is null)
        {
            profile = TenantCapabilityProfile.Undiscovered(tenantId, now);
            await _store.AddCapabilityProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        ApplyCapabilities(profile, resolution, now);
        ApplyTenantFacts(tenant, profile, resolution, graph, arm, now);

        DiscoveryOutcome outcome;

        if (floor.RequiresReconsent)
        {
            var reason = ReconsentReason(floor);
            tenant.MarkNeedsReconsent(reason, now);
            step.Fail($"The universal floor was refused ({floor.Describe()}). An administrator must re-consent.", now);
            profile.CompleteProfiling(now, DefaultProfileInterval);
            outcome = new DiscoveryOutcome(DiscoveryStatus.TenantNeedsReconsent, profile);

            _logger.LogWarning(
                "Tenant {TenantId} flagged NeedsReconsent by capability discovery: {Reason}.",
                tenantId,
                reason);
        }
        else if (floor.Succeeded)
        {
            if (tenant.ConsentGrantedUtc is not null)
            {
                tenant.Activate(now);
            }

            step.Complete(now);
            profile.CompleteProfiling(now, DefaultProfileInterval);
            outcome = new DiscoveryOutcome(DiscoveryStatus.Completed, profile);
        }
        else
        {
            step.Fail($"Microsoft did not answer the floor probe conclusively ({floor.Describe()}). Retrying.", now);
            profile.LeaseUntil(now + InconclusiveRetryInterval, now);
            outcome = new DiscoveryOutcome(DiscoveryStatus.Inconclusive, profile, floor.RetryAfter ?? DefaultRetryAfter);
        }

        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Capability discovery for tenant {TenantId}: {Status}, agreement {AgreementType}, partner-managed {IsPartnerManaged}, cost scope {CostScopeRung} ({CostScopeReason}), {AvailableCount} of {TotalCount} capabilities available, {StaleCount} kept from a previous run.",
            tenantId,
            outcome.Status,
            resolution.AgreementType,
            resolution.IsPartnerManaged,
            resolution.CostScope?.Rung,
            resolution.CostScope?.Reason,
            resolution.Capabilities.Count(c => c.Value is null),
            resolution.Capabilities.Count,
            resolution.InconclusiveCapabilities.Count);

        return outcome;
    }

    /// <summary>
    /// The automatic re-consent probe: floor calls only (ADR-016 rule 5). Success restores the
    /// tenant to Active; anything else schedules the next probe. Only acts on NeedsReconsent.
    /// </summary>
    public async Task<FloorReprobeStatus> ReprobeFloorAsync(
        Guid tenantId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var tenant = await _store.FindTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);

        if (tenant is null || tenant.Status != TenantStatus.NeedsReconsent)
        {
            return FloorReprobeStatus.Skipped;
        }

        var context = new ProbeContext(tenantId, tenant.ConsentCallbackUtc ?? tenant.ConsentGrantedUtc);
        ProbeCallOutcome floor;

        try
        {
            floor = await _graphProbe.ProbeFloorAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // An unexpected probe failure is an inconclusive answer, not a crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Floor re-probe failed unexpectedly for tenant {TenantId}.", tenantId);
            floor = ProbeCallOutcome.Transient(ex.GetType().Name);
        }

        var now = _timeProvider.GetUtcNow();

        if (floor.Succeeded)
        {
            tenant.RestoreConsent(now);

            // ADR-016 names this ConsentRestored; until that AuditAction exists the row is a
            // ConsentGranted by the system with the restoration recorded in its values.
            await _store.AddAuditAsync(
                AuditLog.ForSystem(
                    tenantId,
                    AuditAction.ConsentGranted,
                    nameof(Tenant),
                    tenantId.ToString(),
                    AuditOutcome.Succeeded,
                    correlationId ?? Guid.NewGuid().ToString("N"),
                    now)
                    .WithValues(TenantStatus.NeedsReconsent.ToString(), "ConsentRestored"),
                cancellationToken).ConfigureAwait(false);

            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Tenant {TenantId} answered the floor probe; consent restored.", tenantId);
            return FloorReprobeStatus.Restored;
        }

        tenant.RecordReconsentProbeFailed(now);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Floor re-probe for tenant {TenantId} still failing ({Outcome}); next probe at {NextProbe:u}.",
            tenantId,
            floor.Describe(),
            tenant.NextReconsentProbeUtc);

        return FloorReprobeStatus.StillFailing;
    }

    private static void ApplyCapabilities(TenantCapabilityProfile profile, CapabilityResolution resolution, DateTimeOffset now)
    {
        foreach (var (capability, unavailable) in resolution.Capabilities)
        {
            if (resolution.InconclusiveCapabilities.Contains(capability))
            {
                profile.MarkStale(capability, unavailable?.Detail, now);
                continue;
            }

            if (unavailable is null)
            {
                profile.MarkAvailable(capability, now);
                continue;
            }

            var wasAvailable = profile.Statuses.TryGetValue(capability, out var previous) && previous.IsAvailable;

            if (wasAvailable && resolution.DeniedCapabilities.Contains(capability))
            {
                profile.MarkUnavailable(
                    unavailable with
                    {
                        Reason = CapabilityUnavailableReason.RoleRevoked,
                        Detail = "This was available and its permission or role has since been removed. " + unavailable.Detail,
                    },
                    now);

                continue;
            }

            profile.MarkUnavailable(unavailable, now);
        }
    }

    private static void ApplyTenantFacts(
        Tenant tenant,
        TenantCapabilityProfile profile,
        CapabilityResolution resolution,
        GraphProbeResult graph,
        ArmProbeResult arm,
        DateTimeOffset now)
    {
        // ADR-020: the primary type is the most specific non-Undetermined value found. Losing
        // visibility does not change what the agreement is, so a known value is not overwritten
        // by Undetermined/NotDiscovered.
        var found = resolution.AgreementType;
        var current = tenant.AgreementTypePrimary;
        var foundIsSpecific = found is not (AgreementType.Undetermined or AgreementType.NotDiscovered or AgreementType.Unknown);
        var currentIsSpecific = current is not (AgreementType.Undetermined or AgreementType.NotDiscovered or AgreementType.Unknown);

        if (foundIsSpecific || (!currentIsSpecific && found != AgreementType.NotDiscovered))
        {
            tenant.SetAgreementType(found, now);
        }

        if (graph.DirectoryOutcome.Succeeded && !arm.SubscriptionListOutcome.IsInconclusive)
        {
            tenant.SetPartnerManagement(resolution.IsPartnerManaged, resolution.ManagingPartnerTenantId, now);
        }

        if (graph.OrganizationOutcome.Succeeded)
        {
            if (!string.IsNullOrWhiteSpace(graph.OrganizationDisplayName))
            {
                tenant.UpdateDirectoryDetails(graph.OrganizationDisplayName, graph.DefaultDomain, now);
            }

            if (graph.VerifiedDomains is not null)
            {
                profile.SetVerifiedDomains(graph.VerifiedDomains, now);
            }
        }

        if (arm.SubscriptionListOutcome.Succeeded && resolution.CostScope is { } scope)
        {
            profile.SetDiscoveryDetail(
                new CapabilityDiscoveryDetail
                {
                    CostScopeRung = scope.Rung,
                    CostScopeReason = scope.Reason,
                    CostScopeCount = scope.Scopes.Count,
                    PurchasesVisible = scope.PurchasesVisible,
                    VisibleSubscriptionCount = arm.SubscriptionCount,
                    Subscriptions = arm.Subscriptions
                        .Select(s => new SubscriptionAgreement(s.SubscriptionId, s.AgreementType, s.IsAzurePlan))
                        .ToList(),
                },
                now);
        }
    }

    private static string ReconsentReason(ProbeCallOutcome floor)
        => string.Join(
            ':',
            new[]
            {
                floor.Kind.ToString(),
                "Graph",
                floor.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                floor.ErrorCode,
            }.Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>
    /// Runs one probe. Probes do not throw for Microsoft answers; anything that escapes is an
    /// unexpected fault, recorded as an inconclusive result so it never downgrades a verdict.
    /// </summary>
    private async Task<T> ProbeSafelyAsync<T>(
        Func<CancellationToken, Task<T>> probe,
        Func<ProbeCallOutcome, T> inconclusive,
        Guid tenantId,
        string probeName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await probe(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MicrosoftCallException ex)
        {
            _logger.LogWarning("{Probe} probe for tenant {TenantId} threw {Kind}: {Reason}", probeName, tenantId, ex.Kind, ex.Message);
            return inconclusive(new ProbeCallOutcome(MicrosoftFailureKind.Transient, ex.StatusCode, ex.ErrorCode));
        }
#pragma warning disable CA1031 // One unreachable surface must not hide the verdict on the others.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "{Probe} probe failed for tenant {TenantId}.", probeName, tenantId);
            return inconclusive(ProbeCallOutcome.Transient(ex.GetType().Name));
        }
    }
}
