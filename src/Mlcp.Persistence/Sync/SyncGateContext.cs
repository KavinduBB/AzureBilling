using Mlcp.Application.Sync;

namespace Mlcp.Persistence.Sync;

/// <summary>
/// The persistence side of <see cref="ISyncGateContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// There is no <c>Invoice</c> table yet: <c>InvoiceSync</c> arrives with the Billing provider
/// (docs/05, Phase 3). Until then no invoice can have been recorded, so this answers
/// <c>false</c>, and a same-period drop of more than half is blocked as usual. That is the safe
/// direction: an operator override remains available for a genuine drop.
/// </para>
/// <para>
/// When the table lands, replace the body with an existence query on
/// (<c>TenantId</c>, billing period = <c>periodKey</c>, recorded at or after <c>sinceUtc</c>).
/// </para>
/// </remarks>
public sealed class SyncGateContext : ISyncGateContext
{
    public Task<bool> InvoiceIssuedSinceAsync(
        Guid tenantId,
        string periodKey,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}
