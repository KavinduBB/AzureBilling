using Mlcp.Domain.Common;

namespace Mlcp.Domain.Capabilities;

/// <summary>Resolved state of one capability for one tenant at a point in time.</summary>
/// <param name="IsAvailable">Whether the data source answered successfully at the last probe.</param>
/// <param name="Reason">Set only when <paramref name="IsAvailable"/> is false.</param>
/// <param name="Guide">Remediation guide to surface in the onboarding checklist.</param>
/// <param name="Detail">Probe-time context. Never a secret; it is rendered and logged.</param>
/// <param name="CheckedUtc">When this capability was last probed.</param>
public sealed record CapabilityStatus(
    bool IsAvailable,
    CapabilityUnavailableReason? Reason,
    RemediationGuide Guide,
    string? Detail,
    DateTimeOffset CheckedUtc);

/// <summary>
/// Cached resolution of every capability for a tenant. Drives which providers are wired,
/// which dashboards render data, and what the onboarding checklist shows. Recomputed on
/// schedule, on each unlock, and whenever auth fails (docs/03-architecture.md §2).
/// </summary>
public class TenantCapabilityProfile : TenantEntity
{
    private readonly Dictionary<Capability, CapabilityStatus> _statuses = [];

    /// <summary>One profile per tenant, so the tenant id is also the primary key.</summary>
    public DateTimeOffset LastProfiledUtc { get; private set; }

    /// <summary>When the scheduler should re-run discovery even if nothing changed.</summary>
    public DateTimeOffset NextProfileUtc { get; private set; }

    public IReadOnlyDictionary<Capability, CapabilityStatus> Statuses => _statuses;

    private TenantCapabilityProfile()
    {
    }

    private TenantCapabilityProfile(Guid tenantId, DateTimeOffset nowUtc)
        : base(tenantId, nowUtc)
    {
        LastProfiledUtc = nowUtc;
        NextProfileUtc = nowUtc;
    }

    /// <summary>
    /// Creates a profile in which nothing is known yet. Every capability reports
    /// <see cref="CapabilityUnavailableReason.NotDiscovered"/> rather than false, so a
    /// half-provisioned tenant never looks like a tenant that genuinely lacks a capability.
    /// </summary>
    public static TenantCapabilityProfile Undiscovered(Guid tenantId, DateTimeOffset nowUtc)
    {
        var profile = new TenantCapabilityProfile(tenantId, nowUtc);

        foreach (var capability in Enum.GetValues<Capability>())
        {
            if (capability == Capability.Unknown)
            {
                continue;
            }

            profile._statuses[capability] = new CapabilityStatus(
                IsAvailable: false,
                Reason: CapabilityUnavailableReason.NotDiscovered,
                Guide: RemediationGuide.ConnectOrganisation,
                Detail: null,
                CheckedUtc: nowUtc);
        }

        return profile;
    }

    public void MarkAvailable(Capability capability, DateTimeOffset nowUtc)
    {
        if (capability == Capability.Unknown)
        {
            throw new ArgumentException("Capability.Unknown cannot hold a status.", nameof(capability));
        }

        _statuses[capability] = new CapabilityStatus(true, Reason: null, RemediationGuide.None, Detail: null, nowUtc);
        Touch(nowUtc);
    }

    public void MarkUnavailable(CapabilityUnavailable unavailable, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(unavailable);

        _statuses[unavailable.Capability] = new CapabilityStatus(
            IsAvailable: false,
            unavailable.Reason,
            unavailable.Guide,
            unavailable.Detail,
            nowUtc);

        Touch(nowUtc);
    }

    /// <summary>Records that a full discovery pass finished and schedules the next one.</summary>
    public void CompleteProfiling(DateTimeOffset nowUtc, TimeSpan interval)
    {
        LastProfiledUtc = nowUtc;
        NextProfileUtc = nowUtc + interval;
        Touch(nowUtc);
    }

    /// <summary>Forces the next scheduler pass to re-probe, used after a customer grants an unlock.</summary>
    public void InvalidateNow(DateTimeOffset nowUtc)
    {
        NextProfileUtc = nowUtc;
        Touch(nowUtc);
    }

    public bool IsAvailable(Capability capability)
        => _statuses.TryGetValue(capability, out var status) && status.IsAvailable;

    /// <summary>
    /// Returns the typed unavailability for a capability, or null when it is available.
    /// Callers branch on the null rather than inspecting booleans.
    /// </summary>
    public CapabilityUnavailable? GetUnavailable(Capability capability)
    {
        if (!_statuses.TryGetValue(capability, out var status))
        {
            return CapabilityUnavailable.NotDiscovered(capability);
        }

        return status.IsAvailable
            ? null
            : new CapabilityUnavailable(
                capability,
                status.Reason ?? CapabilityUnavailableReason.NotDiscovered,
                status.Guide,
                status.Detail);
    }

    /// <summary>Replaces the whole status map. Used by discovery replay.</summary>
    public void ReplaceStatuses(IReadOnlyDictionary<Capability, CapabilityStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        _statuses.Clear();

        foreach (var (capability, status) in statuses)
        {
            _statuses[capability] = status;
        }
    }

    /// <summary>
    /// Persistence projection of <see cref="Statuses"/>, mapped to a single JSON column.
    /// </summary>
    /// <remarks>
    /// Private and named for its column rather than exposed as an API: the status map is read
    /// through <see cref="Statuses"/> and written through the Mark* methods, and no caller
    /// outside EF should be handling its serialised form.
    /// </remarks>
    private string StatusesJson
    {
        get => DomainJson.Serialize(_statuses);
        set
        {
            var restored = DomainJson.Deserialize<Dictionary<Capability, CapabilityStatus>>(value);
            _statuses.Clear();

            if (restored is null)
            {
                return;
            }

            foreach (var (capability, status) in restored)
            {
                _statuses[capability] = status;
            }
        }
    }
}
