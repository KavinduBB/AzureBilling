using Mlcp.Persistence.Rls;

namespace Mlcp.Persistence.Security;

/// <summary>
/// Generates the database roles and object permissions that hold the web and worker
/// identities to least privilege (ADR-019, ADR-026).
/// </summary>
/// <remarks>
/// <para>
/// What each role may do:
/// </para>
/// <list type="table">
/// <listheader><term>Object</term><description><c>mlcp_web</c> / <c>mlcp_worker</c></description></listheader>
/// <item><term>Tenant-scoped tables except AuditLog, and global tables</term>
/// <description>SELECT, INSERT, UPDATE, DELETE / the same.</description></item>
/// <item><term>AuditLog</term>
/// <description>SELECT, INSERT, with UPDATE and DELETE denied / SELECT, INSERT, DELETE (tenant
/// deletion only), with UPDATE denied.</description></item>
/// <item><term>DeletionCertificate</term>
/// <description>nothing / SELECT, INSERT. Certificates are never changed once written.</description></item>
/// <item><term>__EFMigrationsHistory</term>
/// <description>SELECT / SELECT, so a host can check the schema version at startup.</description></item>
/// </list>
/// <para>
/// <c>mlcp_system</c> gets no object permissions. The row-level security predicate only checks
/// membership of it.
/// </para>
/// <para>
/// No role receives DDL, <c>CONTROL</c>, <c>ALTER</c> or schema-wide grants. Migrations run
/// as a separate administrator identity (ADR-026). Every grant names one object, so a new table
/// gets no permissions until a migration grants them, and the application then fails loudly
/// rather than being quietly over-privileged.
/// </para>
/// <para>
/// UPDATE and DELETE on the audit log are <em>denied</em>, not merely left ungranted. A DENY
/// holds even if the user is later given a broader role by mistake. A user in both
/// <c>mlcp_web</c> and <c>mlcp_worker</c> is therefore unable to delete audit rows. That is
/// intended: the two tiers use separate users.
/// </para>
/// <para>
/// The generated SQL is idempotent, so a partially applied migration can be re-run. Like
/// <see cref="TenantRlsScript"/>, it takes its table lists as parameters, and a migration
/// passes the dated lists in <see cref="TenantRlsScript.Snapshots"/> and
/// <see cref="GlobalTableSnapshots"/>.
/// </para>
/// </remarks>
public static class DatabaseSecurityScript
{
    public const string AuditLogTable = "AuditLog";

    public const string DeletionCertificateTable = "DeletionCertificate";

    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// Frozen lists of tables that carry no <c>TenantId</c> but are used by both tiers. The
    /// deletion certificate is deliberately absent; it is granted separately.
    /// </summary>
    public static class GlobalTableSnapshots
    {
        /// <summary>The global tables as of the Phase 0 hardening migration (2026-09-17).</summary>
        public static IReadOnlyList<string> Hardening20260917 { get; } = ["Organization"];

        /// <summary>The most recent snapshot.</summary>
        public static IReadOnlyList<string> Latest => Hardening20260917;
    }

    /// <summary>
    /// Statements that create the roles and apply the grants. Run each through its own
    /// <c>migrationBuilder.Sql</c> call, before the role-gated predicate is installed.
    /// </summary>
    /// <param name="tenantScopedTables">Tenant-scoped tables as of the migration, including AuditLog.</param>
    /// <param name="globalTables">Global tables both tiers use, excluding DeletionCertificate.</param>
    public static IReadOnlyList<string> UpSql(
        IReadOnlyCollection<string> tenantScopedTables,
        IReadOnlyCollection<string> globalTables)
    {
        ArgumentNullException.ThrowIfNull(tenantScopedTables);
        ArgumentNullException.ThrowIfNull(globalTables);

        if (!tenantScopedTables.Contains(AuditLogTable, StringComparer.Ordinal))
        {
            throw new ArgumentException("The audit log must be among the tenant-scoped tables.", nameof(tenantScopedTables));
        }

        if (globalTables.Contains(DeletionCertificateTable, StringComparer.Ordinal)
            || globalTables.Contains(AuditLogTable, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "AuditLog and DeletionCertificate have their own grants and must not be listed as global tables.",
                nameof(globalTables));
        }

        var statements = new List<string>();

        foreach (var role in DatabaseRoles.All)
        {
            statements.Add(
                $"IF DATABASE_PRINCIPAL_ID(N'{role}') IS NULL EXEC(N'CREATE ROLE [{role}] AUTHORIZATION [dbo]');");
        }

        var ordinaryTables = tenantScopedTables
            .Concat(globalTables)
            .Where(t => !string.Equals(t, AuditLogTable, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal);

        foreach (var table in ordinaryTables)
        {
            TenantRlsScript.EnsurePlainIdentifier(table);
            statements.Add($"GRANT SELECT, INSERT, UPDATE, DELETE ON {Object(table)} TO [{DatabaseRoles.Web}], [{DatabaseRoles.Worker}];");
        }

        statements.Add($"GRANT SELECT, INSERT ON {Object(AuditLogTable)} TO [{DatabaseRoles.Web}];");
        statements.Add($"DENY UPDATE, DELETE ON {Object(AuditLogTable)} TO [{DatabaseRoles.Web}];");
        statements.Add($"GRANT SELECT, INSERT, DELETE ON {Object(AuditLogTable)} TO [{DatabaseRoles.Worker}];");
        statements.Add($"DENY UPDATE ON {Object(AuditLogTable)} TO [{DatabaseRoles.Worker}];");

        statements.Add($"GRANT SELECT, INSERT ON {Object(DeletionCertificateTable)} TO [{DatabaseRoles.Worker}];");
        statements.Add($"DENY UPDATE, DELETE ON {Object(DeletionCertificateTable)} TO [{DatabaseRoles.Worker}];");

        statements.Add($"GRANT SELECT ON {Object(MigrationsHistoryTable)} TO [{DatabaseRoles.Web}], [{DatabaseRoles.Worker}];");

        return statements;
    }

    /// <summary>
    /// Statements that remove the roles. Dropping a role discards its grants and denies, so
    /// only the memberships need removing first.
    /// </summary>
    /// <remarks>
    /// Removing memberships is what a Down migration has to do, but it is disruptive: the
    /// deployed identities lose all access until the roles are recreated and the users are
    /// added again (<c>--create-users</c>, ADR-026).
    /// </remarks>
    public static IReadOnlyList<string> DownSql()
    {
        var statements = new List<string>();

        foreach (var role in DatabaseRoles.All.Reverse())
        {
            statements.Add($"""
                IF DATABASE_PRINCIPAL_ID(N'{role}') IS NOT NULL
                BEGIN
                    DECLARE @members nvarchar(max) = N'';
                    SELECT @members += N'ALTER ROLE [{role}] DROP MEMBER ' + QUOTENAME(m.name) + N';'
                    FROM sys.database_role_members AS rm
                    JOIN sys.database_principals AS m ON m.principal_id = rm.member_principal_id
                    WHERE rm.role_principal_id = DATABASE_PRINCIPAL_ID(N'{role}');
                    EXEC sys.sp_executesql @members;
                    EXEC(N'DROP ROLE [{role}]');
                END;
                """);
        }

        return statements;
    }

    private static string Object(string table) => $"[{TenantRlsScript.SchemaName}].[{table}]";
}
