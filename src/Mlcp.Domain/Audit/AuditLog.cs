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

/// <summary>
/// The result recorded by one audit row (ADR-019). Stored by name, so members may be added but
/// never renamed.
/// </summary>
public enum AuditOutcome
{
    Unknown = 0,

    /// <summary>
    /// An <see cref="AuditLog.Attempt"/> row, written before an outbound call. Never updated:
    /// the result arrives as a separate row made by <see cref="AuditLog.OutcomeOf"/>.
    /// </summary>
    Pending = 1,

    Succeeded = 2,

    Failed = 3,

    /// <summary>Refused by a pre-flight check before anything left the process.</summary>
    Refused = 4,
}

/// <summary>
/// Immutable audit record (ADR-019, CLAUDE.md rule 12).
/// </summary>
/// <remarks>
/// <para>
/// An action that calls Microsoft writes two rows and never updates either. The
/// <see cref="Attempt"/> row (<see cref="AuditOutcome.Pending"/>) is saved <em>before</em> the
/// outbound call, so an operation that succeeds at Microsoft but fails on the way back is still
/// visible. The <see cref="OutcomeOf"/> row follows and points back through
/// <see cref="AttemptAuditLogId"/>. An action with no outbound call writes one row with its
/// final outcome through <see cref="ForUser"/> or <see cref="ForSystem"/>.
/// </para>
/// <para>
/// There is deliberately no method that changes a saved row. The database enforces the same
/// rule: the web role is denied UPDATE and DELETE on the table, and only the worker's tenant
/// deletion may delete (see <c>DatabaseSecurityScript</c>). The <c>With*</c> methods exist only
/// to finish building a row before it is first saved.
/// </para>
/// </remarks>
public class AuditLog : TenantEntity
{
    /// <summary>Longest <see cref="Detail"/> kept. Longer text is truncated, not rejected.</summary>
    public const int MaxDetailLength = 2000;

    public long AuditLogId { get; private set; }

    /// <summary>
    /// For an outcome row, the attempt it resolves. Null for attempts and single-row actions.
    /// </summary>
    public long? AttemptAuditLogId { get; private set; }

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

    /// <summary>
    /// Short explanation on an outcome row, such as Microsoft's error code. Redacted of secrets
    /// by the caller; the domain has no access to the redactor.
    /// </summary>
    public string? Detail { get; private set; }

    /// <summary>Financial impact shown to the user at confirmation time, for write operations.</summary>
    public decimal? FinancialImpactAmount { get; private set; }

    public string? FinancialImpactCurrency { get; private set; }

    /// <summary>True for a row still awaiting its outcome row.</summary>
    public bool IsAttempt => Outcome == AuditOutcome.Pending;

    private AuditLog()
    {
    }

    private AuditLog(Guid tenantId, DateTimeOffset nowUtc)
        : base(tenantId, nowUtc)
    {
    }

    /// <summary>Records a completed action taken by a signed-in person, as a single row.</summary>
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
        EnsureKnown(outcome);

        return Create(tenantId, actorObjectId, actorUpn, action, entityType, entityId, outcome, correlationId, nowUtc);
    }

    /// <summary>Records a completed action taken by a background job, which has no acting person.</summary>
    public static AuditLog ForSystem(
        Guid tenantId,
        AuditAction action,
        string entityType,
        string? entityId,
        AuditOutcome outcome,
        string correlationId,
        DateTimeOffset nowUtc)
    {
        EnsureKnown(outcome);

        return Create(tenantId, null, null, action, entityType, entityId, outcome, correlationId, nowUtc);
    }

    /// <summary>
    /// Records the intent to call Microsoft. Save it before making the call; afterwards add the
    /// row made by <see cref="OutcomeOf"/>.
    /// </summary>
    /// <param name="actorObjectId">The acting person, or null for a background job.</param>
    /// <param name="actorUpn">Required when <paramref name="actorObjectId"/> is given, and only then.</param>
    public static AuditLog Attempt(
        Guid tenantId,
        Guid? actorObjectId,
        string? actorUpn,
        AuditAction action,
        string entityType,
        string? entityId,
        string correlationId,
        DateTimeOffset nowUtc)
    {
        if (actorObjectId.HasValue != !string.IsNullOrWhiteSpace(actorUpn))
        {
            throw new ArgumentException("An actor needs both an object id and a UPN, or neither.", nameof(actorUpn));
        }

        return Create(tenantId, actorObjectId, actorUpn, action, entityType, entityId, AuditOutcome.Pending, correlationId, nowUtc);
    }

    /// <summary>
    /// Builds the row that resolves <paramref name="attempt"/>. The attempt itself is left
    /// unchanged.
    /// </summary>
    /// <remarks>
    /// The attempt must already be saved, because the outcome row points at its database id.
    /// That ordering is the whole point: the attempt has to exist before Microsoft is called.
    /// </remarks>
    /// <exception cref="DomainException">
    /// <paramref name="attempt"/> is not a saved <see cref="AuditOutcome.Pending"/> row, or
    /// <paramref name="outcome"/> is not a final outcome.
    /// </exception>
    public static AuditLog OutcomeOf(AuditLog attempt, AuditOutcome outcome, string? detail, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        if (!attempt.IsAttempt)
        {
            throw new DomainException("Only an attempt row can be resolved by an outcome row.");
        }

        if (attempt.AuditLogId <= 0)
        {
            throw new DomainException("Save the attempt row before recording its outcome.");
        }

        if (outcome is not (AuditOutcome.Succeeded or AuditOutcome.Failed or AuditOutcome.Refused))
        {
            throw new DomainException($"'{outcome}' is not a final audit outcome.");
        }

        var row = Create(
            attempt.TenantId,
            attempt.ActorObjectId,
            attempt.ActorUpn,
            attempt.Action,
            attempt.EntityType,
            attempt.EntityId,
            outcome,
            attempt.CorrelationId,
            nowUtc);

        row.AttemptAuditLogId = attempt.AuditLogId;
        row.SourceIp = attempt.SourceIp;
        row.FinancialImpactAmount = attempt.FinancialImpactAmount;
        row.FinancialImpactCurrency = attempt.FinancialImpactCurrency;
        row.Detail = Truncate(detail);

        return row;
    }

    /// <summary>Adds before and after snapshots. Call only while building the row.</summary>
    public AuditLog WithValues(string? oldValue, string? newValue)
    {
        OldValue = oldValue;
        NewValue = newValue;
        return this;
    }

    /// <summary>Adds the caller's IP address. Call only while building the row.</summary>
    public AuditLog WithSourceIp(string? sourceIp)
    {
        SourceIp = sourceIp;
        return this;
    }

    /// <summary>Adds a short explanation. Call only while building the row.</summary>
    public AuditLog WithDetail(string? detail)
    {
        Detail = Truncate(detail);
        return this;
    }

    /// <summary>
    /// Adds the financial impact shown to the user before they confirmed. Call only while
    /// building the row. Money is always stored with its currency, and figures in different
    /// currencies are never combined.
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

    private static AuditLog Create(
        Guid tenantId,
        Guid? actorObjectId,
        string? actorUpn,
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

    private static void EnsureKnown(AuditOutcome outcome)
    {
        if (outcome == AuditOutcome.Unknown)
        {
            throw new ArgumentException("An audit row must record a known outcome.", nameof(outcome));
        }
    }

    private static string? Truncate(string? detail)
        => detail is { Length: > MaxDetailLength } ? detail[..MaxDetailLength] : detail;
}
