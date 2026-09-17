using System.Net;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Mlcp.Application.Onboarding;

namespace Mlcp.Integration.Azure.Regions;

/// <summary>
/// The global <c>tid → region</c> map in a geo-redundant Azure Table (ADR-021).
/// </summary>
/// <remarks>
/// <para>
/// One entity per tenant: <c>PartitionKey</c> is the tenant id, <c>RowKey</c> is the constant
/// <see cref="RowKey"/>. Partitioning by tenant spreads load and makes every read a point read.
/// The entity holds the region code and the registration time only — no names and no customer
/// data, because this is the one store shared by every region.
/// </para>
/// <para>
/// Registration is an insert, never an upsert. Table storage rejects an insert whose key exists
/// with 409, so two regions racing for the same tenant resolve to exactly one winner, and the
/// loser reads the winner's region back.
/// </para>
/// </remarks>
public sealed class TableRegionDirectory : IRegionDirectory
{
    public const string RowKey = "region";

    private const string RegionProperty = "Region";
    private const string RegisteredUtcProperty = "RegisteredUtc";

    private readonly TableClient _table;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TableRegionDirectory> _logger;

    public TableRegionDirectory(TableClient table, TimeProvider timeProvider, ILogger<TableRegionDirectory> logger)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> GetRegionAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var response = await _table
            .GetEntityIfExistsAsync<TableEntity>(PartitionKeyFor(tenantId), RowKey, [RegionProperty], cancellationToken)
            .ConfigureAwait(false);

        return response.HasValue ? response.Value?.GetString(RegionProperty) : null;
    }

    public async Task<RegionRegistration> TryRegisterAsync(Guid tenantId, string region, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        var entity = new TableEntity(PartitionKeyFor(tenantId), RowKey)
        {
            [RegionProperty] = region,
            [RegisteredUtcProperty] = _timeProvider.GetUtcNow(),
        };

        try
        {
            await _table.AddEntityAsync(entity, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Registered tenant {TenantId} in region {Region}.", tenantId, region);
            return new RegionRegistration(region, RegisteredNow: true);
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.Conflict)
        {
            var existing = await GetRegionAsync(tenantId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Tenant {tenantId} conflicted on insert but has no region entry.", ex);

            return new RegionRegistration(existing, RegisteredNow: false);
        }
    }

    private static string PartitionKeyFor(Guid tenantId) => tenantId.ToString("D");
}
