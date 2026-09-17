using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mlcp.Application.Sync;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;
using Mlcp.IntegrationTests.Infrastructure;
using Mlcp.Persistence;
using Mlcp.Persistence.Interceptors;
using Mlcp.Persistence.Stores;
using Mlcp.Persistence.Sync;
using Mlcp.Shared.Tenancy;

namespace Mlcp.IntegrationTests.Sync;

/// <summary>Shared plumbing for the SQL-backed sync tests.</summary>
internal static class SyncSqlTestSupport
{
    public const string StagingTable = "staging_Test";
    public const string LiveTable = "live_SyncTest";

    /// <summary>
    /// A context built exactly as <c>AddMlcpPersistence</c> builds it, retrying execution strategy
    /// included. The fixture's own contexts omit the retry strategy, which is how the
    /// user-transaction bug went unnoticed.
    /// </summary>
    public static MlcpDbContext CreateProductionContext(this SqlServerFixture fixture, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<MlcpDbContext>()
            .UseSqlServer(fixture.ConnectionString, sql =>
                sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null))
            .AddInterceptors(new TenantSessionInterceptor(tenantContext))
            .Options;

        return new MlcpDbContext(options, tenantContext);
    }

    public static async Task<Guid> SeedTenantAsync(this SqlServerFixture fixture)
    {
        var tenantId = Guid.NewGuid();
        await using var system = fixture.CreateContext(FixedTenantContext.System);
        system.Tenants.Add(Tenant.Register(tenantId, $"Sync {tenantId:N}", "example.test", "westeurope", DateTimeOffset.UtcNow));
        await system.SaveChangesAsync();
        return tenantId;
    }

    public static SyncRunStore CreateRunStore(MlcpDbContext context)
        => new(context, new SqlStagingStore(context), TimeProvider.System, NullLogger<SyncRunStore>.Instance);

    public static SyncPipeline CreatePipeline(MlcpDbContext context)
        => new(
            CreateRunStore(context),
            new SyncGateContext(),
            new SyncGateOverrideStore(context),
            TimeProvider.System,
            NullLogger<SyncPipeline>.Instance);

    public static async Task CreateTestTablesAsync(this SqlServerFixture fixture)
    {
        await fixture.ExecuteAsync($"""
            IF OBJECT_ID(N'dbo.{StagingTable}') IS NULL
                CREATE TABLE dbo.{StagingTable} (SyncRunId uniqueidentifier NOT NULL, TenantId uniqueidentifier NOT NULL, Value int NOT NULL);
            IF OBJECT_ID(N'dbo.{LiveTable}') IS NULL
                CREATE TABLE dbo.{LiveTable} (TenantId uniqueidentifier NOT NULL, Value int NOT NULL);
            """);
    }

    public static Task DropTestTablesAsync(this SqlServerFixture fixture)
        => fixture.ExecuteAsync($"""
            DROP TABLE IF EXISTS dbo.{StagingTable};
            DROP TABLE IF EXISTS dbo.{LiveTable};
            """);

    public static async Task ExecuteAsync(this SqlServerFixture fixture, string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-only SQL built from constants.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<int> ScalarAsync(this SqlServerFixture fixture, string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-only SQL built from constants.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.Parameters.AddRange(parameters);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static Task InsertStagingAsync(this SqlServerFixture fixture, Guid syncRunId, Guid tenantId, int rows)
        => fixture.ExecuteAsync(
            $"INSERT INTO dbo.{StagingTable} (SyncRunId, TenantId, Value) SELECT TOP (@rows) @run, @tenant, ROW_NUMBER() OVER (ORDER BY (SELECT 1)) FROM sys.all_objects",
            new SqlParameter("@rows", rows),
            new SqlParameter("@run", syncRunId),
            new SqlParameter("@tenant", tenantId));

    public static Task<int> CountStagingAsync(this SqlServerFixture fixture, Guid syncRunId)
        => fixture.ScalarAsync($"SELECT COUNT(*) FROM dbo.{StagingTable} WHERE SyncRunId = @run", new SqlParameter("@run", syncRunId));

    public static Task<int> CountLiveAsync(this SqlServerFixture fixture, Guid tenantId)
        => fixture.ScalarAsync($"SELECT COUNT(*) FROM dbo.{LiveTable} WHERE TenantId = @tenant", new SqlParameter("@tenant", tenantId));

    public static async Task<SyncRun> LoadRunAsync(this SqlServerFixture fixture, Guid syncRunId)
    {
        await using var system = fixture.CreateContext(FixedTenantContext.System);
        return await system.SyncRuns.AsNoTracking().SingleAsync(r => r.SyncRunId == syncRunId);
    }

    public static async Task AddRunAsync(this SqlServerFixture fixture, SyncRun run)
    {
        await using var system = fixture.CreateContext(FixedTenantContext.System);
        system.SyncRuns.Add(run);
        await system.SaveChangesAsync();
    }
}

/// <summary>
/// A job handler over the test tables: stages <see cref="RowsToStage"/> rows (after any adopted
/// ones), merges them into the live table on the run's context, and optionally fails.
/// </summary>
internal sealed class SqlTestHandler(MlcpDbContext context) : ISyncJobHandler
{
    public SyncJobType JobType { get; init; } = SyncJobType.LicenseSkuSync;

    public int RowsToStage { get; init; } = 3;

    public bool ThrowAfterMerge { get; init; }

    public SyncRunContext? SeenContext { get; private set; }

    public int AdoptedRows { get; private set; }

    public async Task<StagingSummary> FetchToStagingAsync(SyncRunContext runContext, CancellationToken cancellationToken)
    {
        SeenContext = runContext;
        AdoptedRows = await CountAsync(runContext.SyncRunId, cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO dbo.staging_Test (SyncRunId, TenantId, Value) SELECT TOP (@rows) @run, @tenant, 1 FROM sys.all_objects",
            [new SqlParameter("@rows", RowsToStage), new SqlParameter("@run", runContext.SyncRunId), new SqlParameter("@tenant", runContext.TenantId)],
            cancellationToken);

        await runContext.Progress.CheckpointAsync("final-page", cancellationToken);

        return new StagingSummary(await CountAsync(runContext.SyncRunId, cancellationToken));
    }

    public async Task<int> MergeStagingToLiveAsync(SyncRunContext runContext, CancellationToken cancellationToken)
    {
        var merged = await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO dbo.live_SyncTest (TenantId, Value) SELECT TenantId, Value FROM dbo.staging_Test WHERE SyncRunId = @run",
            [new SqlParameter("@run", runContext.SyncRunId)],
            cancellationToken);

        return ThrowAfterMerge ? throw new InvalidOperationException("merge step failed after writing") : merged;
    }

    public Task ClearStagingAsync(SyncRunContext runContext, CancellationToken cancellationToken)
        => new SqlStagingStore(context).DeleteForRunAsync(runContext.SyncRunId, cancellationToken);

    private async Task<int> CountAsync(Guid syncRunId, CancellationToken cancellationToken)
    {
        var counts = await context.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS [Value] FROM dbo.staging_Test WHERE SyncRunId = @run",
                new SqlParameter("@run", syncRunId))
            .ToListAsync(cancellationToken);

        return counts.Single();
    }
}
