using Mlcp.Domain.Common;

namespace Mlcp.Domain.Capabilities;

/// <summary>Resolved state of one capability for one tenant at a point in time.</summary>
/// <param name="IsAvailable">Whether the data source answered successfully at the last probe.</param>
/// <param name="Reason">Set only when <paramref name="IsAvailable"/> is false.</param>
/// <param name="Guide">Remediation guide to surface in the onboarding checklist.</param>
/// <param name="Detail">Probe-time context. Never a secret; it is rendered and logged.</param>
/// <param name="CheckedUtc">When this capability's verdict was last established.</param>
/// <param name="IsStale">
/// The last probe failed transiently, so this verdict is the previous one, kept rather than
/// downgraded (ADR-016). <paramref name="StaleDetail"/> says why.
/// </param>
/// <param name="StaleDetail">The <c>ProviderError</c> detail from the failed probe.</param>
/// <param name="StaleSinceUtc">When the verdict first became stale.</param>
public sealed record CapabilityStatus(
    bool IsAvailable,
    CapabilityUnavailableReason? Reason,
    RemediationGuide Guide,
    string? Detail,
    DateTimeOffset CheckedUtc,
    bool IsStale = false,
    string? StaleDetail = null,
    DateTimeOffset? StaleSinceUtc = null)
{
    /// <summary>True for the placeholder written before any discovery ran.</summary>
    public bool IsUndiscovered => !IsAvailable && Reason == CapabilityUnavailableReason.NotDiscovered;
}

/// <summary>
/// Cached resolution of every capability for a tenant. Drives which providers are wired,
/// which dashboards render data, and what the onboarding checklist shows. Recomputed on
/// schedule, on each unlock, and whenever auth fails (docs/03-architecture.md §2).
/// </summary>
public class TenantCapabilityProfile : TenantEntity
{
    private readonly Dictionary<Capability, CapabilityStatus> _statuses = [];
    private List<string> _verifiedDomains = [];

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

    /// <summary>
    /// Keeps the previous verdict for <paramref name="capability"/> but marks it stale, because the
    /// probe failed transiently (ADR-016: no downgrade on a transient failure). A capability that
    /// was never discovered has no verdict to keep and is recorded as <c>ProviderError</c> instead.
    /// </summary>
    public void MarkStale(Capability capability, string? detail, DateTimeOffset nowUtc)
    {
        if (capability == Capability.Unknown)
        {
            throw new ArgumentException("Capability.Unknown cannot hold a status.", nameof(capability));
        }

        if (!_statuses.TryGetValue(capability, out var previous) || previous.IsUndiscovered)
        {
            MarkUnavailable(CapabilityUnavailable.ProviderError(capability, detail), nowUtc);
            return;
        }

        _statuses[capability] = previous with
        {
            IsStale = true,
            StaleDetail = detail,
            StaleSinceUtc = previous.StaleSinceUtc ?? nowUtc,
        };

        Touch(nowUtc);
    }

    /// <summary>Verified domains of the tenant, from <c>GET /organization</c> (ADR-018 consent-request recipients).</summary>
    public IReadOnlyList<string> VerifiedDomains => _verifiedDomains;

    /// <summary>Cost scope rung and per-subscription agreements from the last discovery.</summary>
    public CapabilityDiscoveryDetail DiscoveryDetail { get; private set; } = CapabilityDiscoveryDetail.Empty;

    /// <summary>Replaces the verified domain list. Normalised to lower case, de-duplicated.</summary>
    public void SetVerifiedDomains(IEnumerable<string> domains, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(domains);

        _verifiedDomains = domains
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Touch(nowUtc);
    }

    public void SetDiscoveryDetail(CapabilityDiscoveryDetail detail, DateTimeOffset nowUtc)
    {
        DiscoveryDetail = detail ?? throw new ArgumentNullException(nameof(detail));
        Touch(nowUtc);
    }

    /// <summary>
    /// Moves <see cref="NextProfileUtc"/> forward without recording a completed pass. The scheduler
    /// uses it as a lease so a tenant already queued is not picked again; discovery's
    /// <see cref="CompleteProfiling"/> replaces it.
    /// </summary>
    public void LeaseUntil(DateTimeOffset nextProfileUtc, DateTimeOffset nowUtc)
    {
        NextProfileUtc = nextProfileUtc;
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

    /// <summary>Persistence projection of <see cref="VerifiedDomains"/> (column <c>VerifiedDomains</c>, nullable JSON array).</summary>
    private string? VerifiedDomainsJson
    {
        get => _verifiedDomains.Count == 0 ? null : DomainJson.Serialize(_verifiedDomains);
        set => _verifiedDomains = DomainJson.Deserialize<List<string>>(value) ?? [];
    }

    /// <summary>Persistence projection of <see cref="DiscoveryDetail"/> (column <c>DiscoveryDetail</c>, nullable JSON).</summary>
    private string? DiscoveryDetailJson
    {
        get => ReferenceEquals(DiscoveryDetail, CapabilityDiscoveryDetail.Empty) ? null : DomainJson.Serialize(DiscoveryDetail);
        set => DiscoveryDetail = DomainJson.Deserialize<CapabilityDiscoveryDetail>(value) ?? CapabilityDiscoveryDetail.Empty;
    }
}
