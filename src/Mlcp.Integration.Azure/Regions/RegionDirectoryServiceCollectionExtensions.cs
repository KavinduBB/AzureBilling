using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mlcp.Application.Onboarding;

namespace Mlcp.Integration.Azure.Regions;

public static class RegionDirectoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the global region directory (ADR-021).
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="tableUri">
    /// The table's full URI, for example <c>https://mlcpglobal.table.core.windows.net/TenantRegions</c>
    /// (<c>Mlcp:Regions:DirectoryTableUri</c>). Null selects the in-memory directory; the host
    /// decides whether that is acceptable for its environment.
    /// </param>
    /// <param name="credential">
    /// The workload identity. Defaults to <see cref="DefaultAzureCredential"/>; the identity needs
    /// <em>Storage Table Data Contributor</em> on this table only.
    /// </param>
    public static IServiceCollection AddRegionDirectory(
        this IServiceCollection services,
        Uri? tableUri,
        TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (tableUri is null)
        {
            services.AddSingleton<IRegionDirectory, InMemoryRegionDirectory>();
            return services;
        }

        var (serviceUri, tableName) = SplitTableUri(tableUri);

        services.AddSingleton(_ => new TableClient(serviceUri, tableName, credential ?? new DefaultAzureCredential()));
        services.AddSingleton<IRegionDirectory>(sp => new TableRegionDirectory(
            sp.GetRequiredService<TableClient>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<TableRegionDirectory>>()));

        return services;
    }

    /// <summary>Splits <c>https://account.table.core.windows.net/Table</c> into the service endpoint and table name.</summary>
    /// <exception cref="ArgumentException">The URI does not name exactly one table.</exception>
    public static (Uri ServiceUri, string TableName) SplitTableUri(Uri tableUri)
    {
        ArgumentNullException.ThrowIfNull(tableUri);

        var segments = tableUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (!tableUri.IsAbsoluteUri || tableUri.Scheme != Uri.UriSchemeHttps || segments.Length != 1)
        {
            throw new ArgumentException(
                "Mlcp:Regions:DirectoryTableUri must be an https URI naming one table, "
                + "for example https://account.table.core.windows.net/TenantRegions.",
                nameof(tableUri));
        }

        return (new Uri(tableUri.GetLeftPart(UriPartial.Authority)), segments[0]);
    }
}
