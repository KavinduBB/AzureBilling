using System.Globalization;
using System.Text;

namespace Mlcp.Persistence.Rls;

/// <summary>
/// Generates the row-level security objects that make tenant isolation a property of the
/// database rather than of the application (docs/04-data-model.md §10).
/// </summary>
/// <remarks>
/// <para>
/// Layers 1, 2 and 4 all live in application code and all fail the same way: someone writes a
/// query that bypasses them. Row-level security is the layer that holds when that happens,
/// including for raw SQL, for a stray <c>IgnoreQueryFilters()</c>, and for anyone who reaches
/// the database with the application's credentials.
/// </para>
/// <para>
/// Both predicates are applied to every table. FILTER silently removes other tenants' rows
/// from reads; BLOCK raises an error on a write that would create or move a row into another
/// tenant. FILTER alone would let a cross-tenant insert succeed and then vanish, which is a far
/// worse failure to debug than an error at the point of the mistake.
/// </para>
/// </remarks>
public static class TenantRlsScript
{
    public const string SchemaName = "dbo";

    public const string PredicateFunctionName = "fn_TenantPredicate";

    public const string SecurityPolicyName = "TenantSecurityPolicy";

    /// <summary>
    /// The predicate. A row is visible when it belongs to the session's tenant, or when the
    /// session is the sync scheduler's system context.
    /// </summary>
    /// <remarks>
    /// <c>SCHEMABINDING</c> is required for a security predicate. The comparison against
    /// <c>SESSION_CONTEXT</c> yields NULL when nothing was stamped, so an unstamped connection
    /// sees no rows at all — the safe direction to fail in.
    /// </remarks>
    public static string CreatePredicateFunction() => $"""
        CREATE FUNCTION [{SchemaName}].[{PredicateFunctionName}](@TenantId uniqueidentifier)
        RETURNS TABLE
        WITH SCHEMABINDING
        AS
            RETURN SELECT 1 AS fn_TenantPredicateResult
            WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)
               OR CAST(SESSION_CONTEXT(N'IsSystem') AS bit) = 1;
        """;

    public static string DropPredicateFunction()
        => $"DROP FUNCTION IF EXISTS [{SchemaName}].[{PredicateFunctionName}];";

    /// <summary>
    /// Builds the security policy covering <paramref name="tenantScopedTables"/>.
    /// </summary>
    /// <param name="tenantScopedTables">
    /// Every table carrying a <c>TenantId</c> column. Supplied by the migration and asserted
    /// against the EF model by the tenant-isolation test suite, so a new table that is not
    /// listed here fails CI rather than shipping unprotected.
    /// </param>
    public static string CreateSecurityPolicy(IReadOnlyCollection<string> tenantScopedTables)
    {
        ArgumentNullException.ThrowIfNull(tenantScopedTables);

        if (tenantScopedTables.Count == 0)
        {
            throw new ArgumentException("At least one tenant-scoped table is required.", nameof(tenantScopedTables));
        }

        var sql = new StringBuilder();
        sql.AppendLine(CultureInfo.InvariantCulture, $"CREATE SECURITY POLICY [{SchemaName}].[{SecurityPolicyName}]");

        var clauses = new List<string>(tenantScopedTables.Count * 2);

        foreach (var table in tenantScopedTables.OrderBy(t => t, StringComparer.Ordinal))
        {
            clauses.Add($"    ADD FILTER PREDICATE [{SchemaName}].[{PredicateFunctionName}]([TenantId]) ON [{SchemaName}].[{table}]");
            clauses.Add($"    ADD BLOCK PREDICATE [{SchemaName}].[{PredicateFunctionName}]([TenantId]) ON [{SchemaName}].[{table}]");
        }

        sql.AppendLine(string.Join($",{Environment.NewLine}", clauses));
        sql.Append("WITH (STATE = ON, SCHEMABINDING = ON);");

        return sql.ToString();
    }

    public static string DropSecurityPolicy()
        => $"DROP SECURITY POLICY IF EXISTS [{SchemaName}].[{SecurityPolicyName}];";

    /// <summary>
    /// Tables covered by the policy as of the current migration. Kept as data so the isolation
    /// test can compare it against the EF model.
    /// </summary>
    public static IReadOnlyList<string> TenantScopedTables { get; } =
    [
        "AppUser",
        "AuditLog",
        "OnboardingStep",
        "PendingConsentRequest",
        "SyncRun",
        "Tenant",
        "TenantCapabilityProfile",
    ];
}
