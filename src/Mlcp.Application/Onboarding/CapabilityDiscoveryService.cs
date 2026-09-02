using Microsoft.Extensions.Logging;
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

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs the three probes, resolves them into a capability profile, and moves the tenant to
/// Active (P0-7, docs/03-architecture.md §3).
/// </summary>
/// <remarks>
/// <para>
/// Each probe is isolated: a Graph failure must not prevent the Azure and billing verdicts
/// from being recorded, because a tenant that has granted RBAC but not consent is a real state
/// the onboarding checklist needs to show accurately.
/// </para>
/// <para>
/// The whole operation is idempotent. It is triggered on connect, weekly, on each unlock and
/// after an auth failure, so running it twice must reach the same profile rather than
/// accumulate state.
/// </para>
/// </remarks>
public sealed class CapabilityDiscoveryService
{
    /// <summary>How long a profile stands before the scheduler re-probes it anyway.</summary>
    public static TimeSpan DefaultProfileInterval { get; } = TimeSpan.FromDays(7);

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

    public async Task<TenantCapabilityProfile> DiscoverAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await _store.FindTenantAsync(tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Tenant {tenantId} is not registered.");

        var now = _timeProvider.GetUtcNow();

        var step = await _store.GetOrCreateStepAsync(tenantId, OnboardingStepName.CapabilityDiscovery, cancellationToken)
            .ConfigureAwait(false);

        step.Begin(now);

        var graph = await ProbeSafelyAsync(
            ct => _graphProbe.ProbeAsync(tenantId, ct), GraphProbeResult.NotReachable, tenantId, "Graph", cancellationToken)
            .ConfigureAwait(false);

        var arm = await ProbeSafelyAsync(
            ct => _azureProbe.ProbeAsync(tenantId, ct), ArmProbeResult.NotReachable, tenantId, "ARM", cancellationToken)
            .ConfigureAwait(false);

        var billing = await ProbeSafelyAsync(
            ct => _billingProbe.ProbeAsync(tenantId, ct), BillingProbeResult.NotReachable, tenantId, "Billing", cancellationToken)
            .ConfigureAwait(false);

        var resolution = CapabilityResolver.Resolve(graph, arm, billing);

        var profile = await _store.FindCapabilityProfileAsync(tenantId, cancellationToken).ConfigureAwait(false);

        if (profile is null)
        {
            profile = TenantCapabilityProfile.Undiscovered(tenantId, now);
            await _store.AddCapabilityProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (capability, unavailable) in resolution.Capabilities)
        {
            if (unavailable is null)
            {
                profile.MarkAvailable(capability, now);
            }
            else
            {
                profile.MarkUnavailable(unavailable, now);
            }
        }

        profile.CompleteProfiling(now, DefaultProfileInterval);

        tenant.SetAgreementType(resolution.AgreementType, now);
        tenant.SetPartnerManagement(resolution.IsPartnerManaged, resolution.ManagingPartnerTenantId, now);

        if (!string.IsNullOrWhiteSpace(graph.OrganizationDisplayName))
        {
            tenant.UpdateDirectoryDetails(graph.OrganizationDisplayName, graph.DefaultDomain, now);
        }

        // Graph licensing is the universal floor. Without it there is nothing to sync, so the
        // tenant stays short of Active rather than appearing connected and showing nothing.
        if (profile.IsAvailable(Capability.GraphLicensing))
        {
            tenant.Activate(now);
            step.Complete(now);
        }
        else
        {
            step.Fail("Graph licensing is not readable, so the universal floor cannot be synced.", now);
            tenant.MarkNeedsReconsent(now);
        }

        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Capability discovery for tenant {TenantId}: agreement {AgreementType}, partner-managed {IsPartnerManaged}, {AvailableCount} of {TotalCount} capabilities available.",
            tenantId,
            resolution.AgreementType,
            resolution.IsPartnerManaged,
            resolution.Capabilities.Count(c => c.Value is null),
            resolution.Capabilities.Count);

        return profile;
    }

    /// <summary>
    /// Runs one probe, converting a failure into that probe's "not reachable" result.
    /// </summary>
    /// <remarks>
    /// A probe exists to answer "can we read this", and an exception is one of the answers. The
    /// exception is logged, not swallowed silently, and a lost grant still propagates: a
    /// <see cref="NeedsReconsentException"/> has already flagged the tenant by the time it is
    /// seen here, and its capability is recorded as unavailable for the same reason.
    /// </remarks>
    private async Task<T> ProbeSafelyAsync<T>(
        Func<CancellationToken, Task<T>> probe,
        T notReachable,
        Guid tenantId,
        string probeName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await probe(cancellationToken).ConfigureAwait(false);
        }
        catch (NeedsReconsentException ex)
        {
            _logger.LogWarning(
                "{Probe} probe for tenant {TenantId} was refused: {Reason}",
                probeName,
                tenantId,
                ex.Message);

            return notReachable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // One unreachable surface must not hide the verdict on the others.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "{Probe} probe failed for tenant {TenantId}.", probeName, tenantId);
            return notReachable;
        }
    }
}
