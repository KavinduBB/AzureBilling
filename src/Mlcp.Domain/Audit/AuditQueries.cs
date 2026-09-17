namespace Mlcp.Domain.Audit;

/// <summary>
/// An audit action together with how it ended.
/// </summary>
/// <param name="Row">
/// The attempt row, or the only row for an action that made no outbound call.
/// </param>
/// <param name="Resolution">The outcome row for an attempt, or null if none has been written yet.</param>
public sealed record AuditEntryWithOutcome(AuditLog Row, AuditLog? Resolution)
{
    /// <summary>
    /// The action's outcome. An attempt with no outcome row stays <see cref="AuditOutcome.Pending"/>.
    /// A long-pending attempt means the process stopped mid-call, and the Microsoft side must
    /// be checked.
    /// </summary>
    public AuditOutcome EffectiveOutcome => Resolution?.Outcome ?? Row.Outcome;
}

/// <summary>
/// Query helpers for the two-row audit model (ADR-019).
/// </summary>
public static class AuditQueries
{
    /// <summary>
    /// Returns one entry per action: each attempt joined to its outcome row, plus each
    /// single-row action. Outcome rows are not returned on their own.
    /// </summary>
    /// <remarks>
    /// Written as an outer join over <see cref="IQueryable{T}"/> so EF Core runs it as one SQL
    /// statement. The unique index on <see cref="AuditLog.AttemptAuditLogId"/> means the join
    /// yields at most one match, so it never duplicates an attempt.
    /// <para>
    /// Filter the audit rows <em>before</em> calling this, for example by correlation id or
    /// time range. A predicate on the returned record cannot be translated to SQL. An outcome
    /// row carries its attempt's correlation id, entity and actor, so filtering on those keeps
    /// each pair together.
    /// </para>
    /// </remarks>
    public static IQueryable<AuditEntryWithOutcome> WithOutcome(this IQueryable<AuditLog> auditLogs)
    {
        ArgumentNullException.ThrowIfNull(auditLogs);

        // A correlated SelectMany with DefaultIfEmpty, which EF translates to a LEFT JOIN. The
        // GroupJoin spelling of the same query is not translatable.
        return
            from row in auditLogs
            where row.AttemptAuditLogId == null
            from resolution in auditLogs.Where(o => o.AttemptAuditLogId == row.AuditLogId).DefaultIfEmpty()
            select new AuditEntryWithOutcome(row, resolution);
    }
}
