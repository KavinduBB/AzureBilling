using Mlcp.Domain.Common;

namespace Mlcp.Domain.Audit;

/// <summary>Categories of auditable action. Every financial read and every write is recorded.</summary>
public enum AuditAction
{
    Unknown = 0,
    TenantConnected = 1,
    TenantDisconnected = 2,
    ConsentRequested = 3,
    ConsentGranted = 4,
    CapabilityUnlocked = 5,
    ManualPriceEntered = 6,
    InvoiceDownloaded = 7,
    UsageAnonymisationChanged = 8,
    AppUserRoleChanged = 9,
    AutoRenewChanged = 10,
    SubscriptionQuantityChanged = 11,
    SubscriptionCancelled = 12,
    SubscriptionPurchased = 13,
    LicenceAssigned = 14,
    LicenceRemoved = 15,
    TenantDataDeleted = 16,
}

public enum AuditOutcome
{
    Unknown = 0,

    /// <summary>Written before an outbound call. Resolved once the call returns.</summary>
    Attempted = 1,
    Succeeded = 2,
    Failed = 3,

    /// <summary>Refused by a pre-flight check before anything left the process.</summary>
    Blocked = 4,
}

/// <summary>
/// Append-only audit record. For write operations the row is created with
/// <see cref="AuditOutcome.Attempted"/> <em>before</em> the outbound Microsoft call, so an
/// operation that succeeds at Microsoft but fails on the way back is still visible
/// (CLAUDE.md rule 12, ADR-010). The database principal holds INSERT and SELECT only.
/// </summary>
public class AuditLog : TenantEntity
{
    public long AuditLogId { get; private set; }

    public Guid? ActorObjectId { get; private set; }

    /// <summary>Null for actions taken by a sync job rather than a person.</summary>
    public string? ActorUpn { get; private set; }

    public AuditAction Action { get; private set; }

    public string EntityType { get; private set; } = string.Empty;

    public string? EntityId { get; private set; }

    /// <summary>JSON snapshot before the change. Redacted of secrets by the caller.</summary>
    public string? OldValue { get; private set; }

    /// <summary>JSON snapshot after the change.</summary>
    public string? NewValue { get; private set; }

    public DateTimeOffset OccurredUtc { get; private set; }

    public string? SourceIp { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public AuditOutcome Outcome { get; private set; }

    /// <summary>Financial impact shown to the user at confirmation time, for write operations.</summary>
    public decimal? FinancialImpactAmount { get; private set; }

    public string? FinancialImpactCurrency { get; private set; }

    private AuditLog()
    {
    }

    private AuditLog(Guid tenantId, DateTimeOffset nowUtc)
        : base(tenantId, nowUtc)
    {
    }

    /// <summary>Records an action taken by a signed-in person.</summary>
    public static AuditLog ForUser(
        Guid tenantId,
        Guid actorObjectId,
        string actorUpn,
        AuditAction action,
        string entityType,
        string? entityId,
        AuditOutcome outcome,
        string correlationId,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUpn);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        return new AuditLog(tenantId, nowUtc)
        {
            ActorObjectId = actorObjectId,
            ActorUpn = actorUpn,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Outcome = outcome,
            CorrelationId = correlationId,
            OccurredUtc = nowUtc,
        };
    }

    /// <summary>Records an action taken by a background job, which has no acting person.</summary>
    public static AuditLog ForSystem(
        Guid tenantId,
        AuditAction action,
        string entityType,
        string? entityId,
        AuditOutcome outcome,
        string correlationId,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        return new AuditLog(tenantId, nowUtc)
        {
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Outcome = outcome,
            CorrelationId = correlationId,
            OccurredUtc = nowUtc,
        };
    }

    public AuditLog WithValues(string? oldValue, string? newValue)
    {
        OldValue = oldValue;
        NewValue = newValue;
        return this;
    }

    public AuditLog WithSourceIp(string? sourceIp)
    {
        SourceIp = sourceIp;
        return this;
    }

    /// <summary>
    /// Attaches the financial impact that was shown to the user before they confirmed. Money is
    /// always stored with its currency; figures in different currencies are never combined.
    /// </summary>
    public AuditLog WithFinancialImpact(decimal amount, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        if (currency.Length != 3)
        {
            throw new ArgumentException("Currency must be a three-letter ISO 4217 code.", nameof(currency));
        }

        FinancialImpactAmount = amount;
        FinancialImpactCurrency = currency.ToUpperInvariant();
        return this;
    }

    /// <summary>
    /// Resolves an <see cref="AuditOutcome.Attempted"/> row once the outbound call returns.
    /// The only mutation an audit row permits.
    /// </summary>
    public void Resolve(AuditOutcome outcome, DateTimeOffset nowUtc)
    {
        if (Outcome != AuditOutcome.Attempted)
        {
            throw new DomainException("Only an attempted audit row can be resolved.");
        }

        Outcome = outcome;
        Touch(nowUtc);
    }
}
