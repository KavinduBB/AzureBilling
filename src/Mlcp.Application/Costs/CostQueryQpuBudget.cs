namespace Mlcp.Application.Costs;

/// <summary>One Cost Management QPU quota: at most <see cref="Limit"/> QPU in any <see cref="Window"/>.</summary>
public readonly record struct QpuQuota(TimeSpan Window, int Limit);

/// <summary>
/// The per-tenant Cost Management Query budget in query processing units (ADR-017 rung 3).
/// </summary>
/// <remarks>
/// <para>
/// Quotas from
/// <see href="https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/manage-automation">Cost Management automation</see>:
/// 12 QPU per 10 seconds, 60 per minute, 600 per hour, per tenant. One QPU is currently charged per
/// month of data queried.
/// </para>
/// <para>
/// A sliding-window log. Rung-3 queries run sequentially per tenant, asking
/// <see cref="NextAvailableUtc"/> before each one and <see cref="TryConsume"/> to record it, so a
/// sync schedules itself inside the quota instead of discovering it through 429s.
/// </para>
/// </remarks>
public sealed class CostQueryQpuBudget
{
    public static IReadOnlyList<QpuQuota> DefaultQuotas { get; } =
    [
        new(TimeSpan.FromSeconds(10), 12),
        new(TimeSpan.FromMinutes(1), 60),
        new(TimeSpan.FromHours(1), 600),
    ];

    private readonly List<(DateTimeOffset At, int Qpu)> _spent = [];
    private readonly Lock _gate = new();
    private readonly TimeSpan _longestWindow;

    public CostQueryQpuBudget()
        : this(DefaultQuotas)
    {
    }

    public CostQueryQpuBudget(IReadOnlyList<QpuQuota> quotas)
    {
        ArgumentNullException.ThrowIfNull(quotas);

        if (quotas.Count == 0 || quotas.Any(q => q.Limit <= 0 || q.Window <= TimeSpan.Zero))
        {
            throw new ArgumentException("At least one positive quota is required.", nameof(quotas));
        }

        Quotas = quotas;
        _longestWindow = quotas.Max(q => q.Window);
    }

    public IReadOnlyList<QpuQuota> Quotas { get; }

    /// <summary>QPU for a query covering <paramref name="from"/>..<paramref name="to"/>: one per calendar month touched.</summary>
    public static int EstimateQpu(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            (from, to) = (to, from);
        }

        return ((to.Year - from.Year) * 12) + to.Month - from.Month + 1;
    }

    /// <summary>Records <paramref name="qpu"/> if every quota has room at <paramref name="nowUtc"/>.</summary>
    public bool TryConsume(int qpu, DateTimeOffset nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpu);

        lock (_gate)
        {
            Prune(nowUtc);

            if (!HasRoom(qpu, nowUtc))
            {
                return false;
            }

            _spent.Add((nowUtc, qpu));
            return true;
        }
    }

    /// <summary>The earliest time at which <paramref name="qpu"/> would fit in every quota.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="qpu"/> exceeds a quota outright.</exception>
    public DateTimeOffset NextAvailableUtc(int qpu, DateTimeOffset nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpu);

        if (Quotas.Any(q => qpu > q.Limit))
        {
            throw new ArgumentOutOfRangeException(nameof(qpu), qpu, "The query is larger than a quota; split its date range.");
        }

        lock (_gate)
        {
            Prune(nowUtc);
            var candidate = nowUtc;

            // Each spent entry expiring is the only event that frees room, so test those instants.
            foreach (var (at, _) in _spent)
            {
                if (HasRoom(qpu, candidate))
                {
                    return candidate;
                }

                foreach (var quota in Quotas)
                {
                    var expiry = at + quota.Window;

                    if (expiry > candidate && !HasRoomIn(quota, qpu, candidate))
                    {
                        candidate = expiry;
                    }
                }
            }

            return candidate;
        }
    }

    private bool HasRoom(int qpu, DateTimeOffset at)
        => Quotas.All(quota => HasRoomIn(quota, qpu, at));

    private bool HasRoomIn(QpuQuota quota, int qpu, DateTimeOffset at)
    {
        var used = 0;

        foreach (var (spentAt, spent) in _spent)
        {
            if (spentAt > at - quota.Window && spentAt <= at)
            {
                used += spent;
            }
        }

        return used + qpu <= quota.Limit;
    }

    private void Prune(DateTimeOffset nowUtc)
        => _spent.RemoveAll(entry => entry.At <= nowUtc - _longestWindow);
}
