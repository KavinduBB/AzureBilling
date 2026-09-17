using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Mlcp.Application.Sync;

namespace Mlcp.Persistence.Sync;

/// <summary>
/// <see cref="IStagingStore"/> over SQL Server, discovering staging tables from the catalog.
/// </summary>
/// <remarks>
/// <para>
/// Table names come from <c>sys.tables</c> and are quoted by the server with
/// <c>QUOTENAME</c>; no caller-supplied text ever reaches the SQL. Only tables named
/// <c>staging_*</c> <em>and</em> carrying a <c>SyncRunId</c> column are touched, so an unrelated
/// table that happens to share the prefix is left alone.
/// </para>
/// <para>
/// Every statement goes through the context, so it enlists in whatever transaction the context
/// has open. That is what makes a resume's re-tag atomic with the run records.
/// </para>
/// </remarks>
public sealed class SqlStagingStore : IStagingStore
{
    /// <summary>Rows deleted per statement by the sweeper, to keep each transaction short.</summary>
    public const int SweepBatchSize = 5000;

    private const string TablePlaceholder = "{table}";

    private const string DiscoverSql = """
        SELECT QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) AS [Value]
        FROM sys.tables AS t
        JOIN sys.schemas AS s ON s.schema_id = t.schema_id
        WHERE t.name LIKE N'staging[_]%'
          AND t.is_ms_shipped = 0
          AND EXISTS (SELECT 1 FROM sys.columns AS c WHERE c.object_id = t.object_id AND c.name = N'SyncRunId')
        ORDER BY s.name, t.name
        """;

    private const string HasRowsSql =
        "SELECT CASE WHEN EXISTS (SELECT 1 FROM {table} WHERE [SyncRunId] = @syncRunId) THEN 1 ELSE 0 END AS [Value]";

    private const string RetagSql = "UPDATE {table} SET [SyncRunId] = @toSyncRunId WHERE [SyncRunId] = @fromSyncRunId";

    private const string DeleteSql = "DELETE FROM {table} WHERE [SyncRunId] = @syncRunId";

    /// <summary>
    /// Keeps rows of a running run, or of a run touched since the cutoff (a failed or abandoned
    /// run inside its resume window). Everything else, including rows whose run row is gone,
    /// is garbage.
    /// </summary>
    private const string SweepSql = """
        DELETE TOP (@batch) s
        FROM {table} AS s
        WHERE NOT EXISTS (
            SELECT 1 FROM [dbo].[SyncRun] AS r
            WHERE r.[SyncRunId] = s.[SyncRunId]
              AND (r.[Status] = N'Running' OR r.[UpdatedUtc] >= @cutoff))
        """;

    private readonly MlcpDbContext _context;

    public SqlStagingStore(MlcpDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>The quoted names of every staging table, e.g. <c>[dbo].[staging_Sku]</c>.</summary>
    public async Task<IReadOnlyList<string>> DiscoverTablesAsync(CancellationToken cancellationToken)
    {
        var tables = await _context.Database
            .SqlQueryRaw<string>(DiscoverSql)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var table in tables)
        {
            // QUOTENAME output always has this shape; anything else means the query changed.
            if (!table.StartsWith('[') || !table.EndsWith(']'))
            {
                throw new InvalidOperationException($"Unexpected staging table name '{table}'.");
            }
        }

        return tables;
    }

    public async Task<bool> HasRowsAsync(Guid syncRunId, CancellationToken cancellationToken)
    {
        foreach (var table in await DiscoverTablesAsync(cancellationToken).ConfigureAwait(false))
        {
            var found = await _context.Database
                .SqlQueryRaw<int>(ForTable(HasRowsSql, table), GuidParameter("@syncRunId", syncRunId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (found.Count == 1 && found[0] == 1)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<int> RetagAsync(Guid fromSyncRunId, Guid toSyncRunId, CancellationToken cancellationToken)
    {
        var moved = 0;

        foreach (var table in await DiscoverTablesAsync(cancellationToken).ConfigureAwait(false))
        {
            moved += await _context.Database
                .ExecuteSqlRawAsync(
                    ForTable(RetagSql, table),
                    [GuidParameter("@toSyncRunId", toSyncRunId), GuidParameter("@fromSyncRunId", fromSyncRunId)],
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return moved;
    }

    public async Task<int> DeleteForRunAsync(Guid syncRunId, CancellationToken cancellationToken)
    {
        var deleted = 0;

        foreach (var table in await DiscoverTablesAsync(cancellationToken).ConfigureAwait(false))
        {
            deleted += await _context.Database
                .ExecuteSqlRawAsync(ForTable(DeleteSql, table), [GuidParameter("@syncRunId", syncRunId)], cancellationToken)
                .ConfigureAwait(false);
        }

        return deleted;
    }

    /// <remarks>
    /// Must run in a context that can see every tenant's runs (the system context); in a
    /// tenant-scoped session row-level security hides other tenants' runs, and their staging
    /// would look orphaned.
    /// </remarks>
    public async Task<int> SweepAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        var deleted = 0;

        foreach (var table in await DiscoverTablesAsync(cancellationToken).ConfigureAwait(false))
        {
            int batch;

            do
            {
                batch = await _context.Database
                    .ExecuteSqlRawAsync(
                        ForTable(SweepSql, table),
                        [
                            new SqlParameter("@batch", SqlDbType.Int) { Value = SweepBatchSize },
                            new SqlParameter("@cutoff", SqlDbType.DateTimeOffset) { Value = olderThanUtc },
                        ],
                        cancellationToken)
                    .ConfigureAwait(false);

                deleted += batch;
            }
            while (batch == SweepBatchSize);
        }

        return deleted;
    }

    private static string ForTable(string template, string quotedTable)
        => template.Replace(TablePlaceholder, quotedTable, StringComparison.Ordinal);

    private static SqlParameter GuidParameter(string name, Guid value)
        => new(name, SqlDbType.UniqueIdentifier) { Value = value };
}
