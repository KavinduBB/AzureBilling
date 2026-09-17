using System.Globalization;
using System.Text;
using Mlcp.Persistence.Security;

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
/// <para>
/// The predicate is an OR of <em>admission clauses</em>. Each clause is a named constant, and
/// the function is built from a list of them. Organisation roll-up (ADR-022) will add one more
/// clause to the FILTER function and give BLOCK its own single-tenant function through
/// <see cref="CreateSecurityPolicy(IReadOnlyCollection{string}, string, string)"/>. The existing
/// clauses will not change.
/// </para>
/// <para>
/// Migrations must pass a dated list from <see cref="Snapshots"/>, never the live
/// <see cref="TenantScopedTables"/>. A migration replays against the schema as it stood when
/// it was written. If it read a list that later gained a table, it would try to protect a
/// table that does not exist yet.
/// </para>
/// </remarks>
public static class TenantRlsScript
{
    public const string SchemaName = "dbo";

    public const string PredicateFunctionName = "fn_TenantPredicate";

    public const string SecurityPolicyName = "TenantSecurityPolicy";

    /// <summary>
    /// Admits a row belonging to the tenant stamped on the session. When nothing was stamped
    /// the comparison yields NULL, so an unstamped connection sees no rows at all, which is the
    /// safe direction to fail in.
    /// </summary>
    public const string TenantClause =
        "@TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)";

    /// <summary>
    /// The original system clause, used by migration <c>EnableRowLevelSecurity</c>: the session
    /// flag alone. Superseded by <see cref="RoleGatedSystemClause"/>, because any code on any
    /// connection could set the flag (ADR-026). Kept unchanged so that migration replays
    /// exactly as it did.
    /// </summary>
    public const string SessionOnlySystemClause =
        "CAST(SESSION_CONTEXT(N'IsSystem') AS bit) = 1";

    /// <summary>
    /// The ADR-026 system clause. The session flag counts only on a connection whose user is in
    /// <see cref="DatabaseRoles.System"/>, or is a database owner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>db_owner</c> arm was added after testing against SQL Server 2022. A sysadmin, and
    /// the Entra admin used by the migrator, both connect as <c>dbo</c>. For <c>dbo</c>,
    /// <c>IS_MEMBER(N'mlcp_system')</c> returns 0 and <c>IS_MEMBER(N'db_owner')</c> returns 1.
    /// Without this arm, a database owner running a data fix-up with the flag set would see
    /// nothing, and so would the test suite, which connects as <c>sa</c>.
    /// </para>
    /// <para>
    /// Admitting database owners weakens nothing: a database owner can already switch the
    /// security policy off. The workload users are never database owners (ADR-026: no DDL).
    /// </para>
    /// </remarks>
    public const string RoleGatedSystemClause =
        "(CAST(SESSION_CONTEXT(N'IsSystem') AS bit) = 1 AND (IS_MEMBER(N'" + DatabaseRoles.System
        + "') = 1 OR IS_MEMBER(N'db_owner') = 1))";

    /// <summary>The clauses of the current predicate, in order.</summary>
    public static IReadOnlyList<string> CurrentAdmissionClauses { get; } = [TenantClause, RoleGatedSystemClause];

    /// <summary>The clauses of the original predicate, in order.</summary>
    public static IReadOnlyList<string> SessionOnlyAdmissionClauses { get; } = [TenantClause, SessionOnlySystemClause];

    /// <summary>
    /// The original predicate function, with the session-only system clause. Used unchanged by
    /// migration <c>EnableRowLevelSecurity</c>; new migrations use
    /// <see cref="UpgradeToRoleGatedSystemClause"/>.
    /// </summary>
    public static string CreatePredicateFunction()
        => CreatePredicateFunction(PredicateFunctionName, SessionOnlyAdmissionClauses);

    /// <summary>
    /// Builds a schema-bound predicate function that admits a row when any of
    /// <paramref name="admissionClauses"/> holds. Each clause may refer to the
    /// <c>@TenantId</c> parameter.
    /// </summary>
    /// <remarks><c>SCHEMABINDING</c> is required for a security predicate.</remarks>
    public static string CreatePredicateFunction(string functionName, IReadOnlyList<string> admissionClauses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        ArgumentNullException.ThrowIfNull(admissionClauses);

        if (admissionClauses.Count == 0 || admissionClauses.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty admission clause is required.", nameof(admissionClauses));
        }

        var sql = new StringBuilder();
        sql.AppendLine(CultureInfo.InvariantCulture, $"CREATE FUNCTION [{SchemaName}].[{functionName}](@TenantId uniqueidentifier)");
        sql.AppendLine("RETURNS TABLE");
        sql.AppendLine("WITH SCHEMABINDING");
        sql.AppendLine("AS");
        sql.AppendLine("    RETURN SELECT 1 AS fn_TenantPredicateResult");
        sql.AppendLine(CultureInfo.InvariantCulture, $"    WHERE {admissionClauses[0]}");

        foreach (var clause in admissionClauses.Skip(1))
        {
            sql.AppendLine(CultureInfo.InvariantCulture, $"       OR {clause}");
        }

        return sql.ToString().TrimEnd() + ";";
    }

    public static string DropPredicateFunction()
        => DropPredicateFunction(PredicateFunctionName);

    public static string DropPredicateFunction(string functionName)
        => $"DROP FUNCTION IF EXISTS [{SchemaName}].[{functionName}];";

    /// <summary>
    /// Builds the security policy covering <paramref name="tenantScopedTables"/>, with one
    /// function serving as both the FILTER and the BLOCK predicate.
    /// </summary>
    /// <param name="tenantScopedTables">
    /// Every table carrying a <c>TenantId</c> column, as of the migration calling this. Pass a
    /// list from <see cref="Snapshots"/>.
    /// </param>
    public static string CreateSecurityPolicy(IReadOnlyCollection<string> tenantScopedTables)
        => CreateSecurityPolicy(tenantScopedTables, PredicateFunctionName, PredicateFunctionName);

    /// <summary>
    /// Builds the security policy, with separate FILTER and BLOCK functions. Roll-up (ADR-022)
    /// widens reads only, so the BLOCK function must stay strictly single-tenant.
    /// </summary>
    public static string CreateSecurityPolicy(
        IReadOnlyCollection<string> tenantScopedTables,
        string filterFunctionName,
        string blockFunctionName)
    {
        ArgumentNullException.ThrowIfNull(tenantScopedTables);
        ArgumentException.ThrowIfNullOrWhiteSpace(filterFunctionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(blockFunctionName);

        if (tenantScopedTables.Count == 0)
        {
            throw new ArgumentException("At least one tenant-scoped table is required.", nameof(tenantScopedTables));
        }

        var sql = new StringBuilder();
        sql.AppendLine(CultureInfo.InvariantCulture, $"CREATE SECURITY POLICY [{SchemaName}].[{SecurityPolicyName}]");

        var clauses = new List<string>(tenantScopedTables.Count * 2);

        foreach (var table in tenantScopedTables.Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal))
        {
            EnsurePlainIdentifier(table);
            clauses.Add($"    ADD FILTER PREDICATE [{SchemaName}].[{filterFunctionName}]([TenantId]) ON [{SchemaName}].[{table}]");
            clauses.Add($"    ADD BLOCK PREDICATE [{SchemaName}].[{blockFunctionName}]([TenantId]) ON [{SchemaName}].[{table}]");
        }

        sql.AppendLine(string.Join($",{Environment.NewLine}", clauses));
        sql.Append("WITH (STATE = ON, SCHEMABINDING = ON);");

        return sql.ToString();
    }

    public static string DropSecurityPolicy()
        => $"DROP SECURITY POLICY IF EXISTS [{SchemaName}].[{SecurityPolicyName}];";

    /// <summary>
    /// Statements that move an existing database to the ADR-026 role-gated predicate. Run each
    /// through its own <c>migrationBuilder.Sql</c> call: <c>CREATE FUNCTION</c> must start its
    /// own batch.
    /// </summary>
    /// <remarks>
    /// The policy is schema-bound to the function, so the function cannot be altered while the
    /// policy exists. The policy is dropped, the function replaced and the policy recreated.
    /// The migration runs all of this in one transaction, so no reader ever sees the tables
    /// unprotected. Create the roles (<see cref="DatabaseSecurityScript"/>) first, so that
    /// <see cref="DatabaseRoles.System"/> exists when the predicate first runs. A missing role
    /// would still fail closed, because <c>IS_MEMBER</c> returns NULL for it.
    /// </remarks>
    public static IReadOnlyList<string> UpgradeToRoleGatedSystemClause(IReadOnlyCollection<string> tenantScopedTables)
        => ReplacePredicate(tenantScopedTables, CurrentAdmissionClauses);

    /// <summary>The reverse of <see cref="UpgradeToRoleGatedSystemClause"/>, for a migration's Down.</summary>
    public static IReadOnlyList<string> DowngradeToSessionOnlySystemClause(IReadOnlyCollection<string> tenantScopedTables)
        => ReplacePredicate(tenantScopedTables, SessionOnlyAdmissionClauses);

    private static string[] ReplacePredicate(IReadOnlyCollection<string> tenantScopedTables, IReadOnlyList<string> clauses)
    {
        ArgumentNullException.ThrowIfNull(tenantScopedTables);

        return
        [
            DropSecurityPolicy(),
            DropPredicateFunction(),
            CreatePredicateFunction(PredicateFunctionName, clauses),
            CreateSecurityPolicy(tenantScopedTables),
        ];
    }

    /// <summary>
    /// The tables the model says are tenant-scoped, today. The unit tests compare this with the
    /// EF model, and require <see cref="Snapshots.Latest"/> to match it. Migrations must not
    /// read it; see <see cref="Snapshots"/>.
    /// </summary>
    public static IReadOnlyList<string> TenantScopedTables { get; } =
    [
        "AppUser",
        "AuditLog",
        "OnboardingStep",
        "PendingConsentRequest",
        "SyncGateOverride",
        "SyncRun",
        "Tenant",
        "TenantCapabilityProfile",
    ];

    /// <summary>
    /// Frozen lists of tenant-scoped tables, one per migration that rebuilds the policy.
    /// </summary>
    /// <remarks>
    /// When a tenant-scoped table is added, add a new snapshot here, point
    /// <see cref="Latest"/> at it, and rebuild the policy in the same migration that creates
    /// the table. Existing snapshots are never edited: a migration that has already run has to
    /// replay identically.
    /// </remarks>
    public static class Snapshots
    {
        /// <summary>The tables protected by migration <c>EnableRowLevelSecurity</c> (2026-09-02).</summary>
        public static IReadOnlyList<string> Initial20260902 { get; } =
        [
            "AppUser",
            "AuditLog",
            "OnboardingStep",
            "PendingConsentRequest",
            "SyncRun",
            "Tenant",
            "TenantCapabilityProfile",
        ];

        /// <summary>The tables protected by the Phase 0 hardening migration (2026-09-17).</summary>
        public static IReadOnlyList<string> Hardening20260917 { get; } =
        [
            "AppUser",
            "AuditLog",
            "OnboardingStep",
            "PendingConsentRequest",
            "SyncGateOverride",
            "SyncRun",
            "Tenant",
            "TenantCapabilityProfile",
        ];

        /// <summary>The most recent snapshot. Must equal <see cref="TenantScopedTables"/>.</summary>
        public static IReadOnlyList<string> Latest => Hardening20260917;
    }

    internal static void EnsurePlainIdentifier(string name)
    {
        // Table names are compiled constants, but they are spliced into SQL. A bracket or
        // quote would be a bug, so refuse anything that is not a plain identifier.
        if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException($"'{name}' is not a plain SQL identifier.", nameof(name));
        }
    }
}
