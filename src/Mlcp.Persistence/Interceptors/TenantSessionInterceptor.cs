using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Mlcp.Shared.Tenancy;

namespace Mlcp.Persistence.Interceptors;

/// <summary>
/// Stamps the current tenant into SQL Server's <c>SESSION_CONTEXT</c> whenever a connection is
/// opened, so the row-level security predicate has something to evaluate.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes isolation layer 3 work (docs/04-data-model.md §10). Without it the RLS
/// predicate sees a null <c>TenantId</c> and every query returns nothing, which is the correct
/// failure direction but not a useful application.
/// </para>
/// <para>
/// The values are written with <c>@read_only = 1</c>. Once set, they cannot be changed for the
/// life of that connection, so neither application code nor injected SQL can widen the
/// session's tenant after the fact. This is safe with connection pooling because ADO.NET
/// issues <c>sp_reset_connection</c> when a connection returns to the pool, which clears
/// session context; every open therefore starts blank and is stamped again here.
/// </para>
/// </remarks>
public sealed class TenantSessionInterceptor : DbConnectionInterceptor
{
    internal const string SetSessionContextSql = """
        EXEC sp_set_session_context @key = N'TenantId', @value = @tenantId, @read_only = 1;
        EXEC sp_set_session_context @key = N'IsSystem', @value = @isSystem, @read_only = 1;
        """;

    private readonly ITenantContext _tenantContext;

    public TenantSessionInterceptor(ITenantContext tenantContext)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();

        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var command = CreateCommand(connection);

        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = SetSessionContextSql;

        var tenantId = command.CreateParameter();
        tenantId.ParameterName = "@tenantId";

        // DBNull rather than Guid.Empty: an absent tenant must match no rows, and Guid.Empty is
        // a value that a buggy insert could plausibly write into a TenantId column.
        tenantId.Value = _tenantContext.TenantId.HasValue ? _tenantContext.TenantId.Value : DBNull.Value;
        command.Parameters.Add(tenantId);

        var isSystem = command.CreateParameter();
        isSystem.ParameterName = "@isSystem";
        isSystem.Value = _tenantContext.IsSystem;
        command.Parameters.Add(isSystem);

        return command;
    }
}
