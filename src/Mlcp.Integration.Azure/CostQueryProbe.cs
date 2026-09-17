using Mlcp.Shared.Http;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;

namespace Mlcp.Integration.Azure;

/// <summary>
/// The smallest Cost Management query that answers "can MLCP read costs at this scope": month to
/// date, no grouping, no granularity — one QPU.
/// </summary>
/// <remarks>
/// Cost Management query is a POST but a read, so it is marked idempotent and may be retried.
/// The <c>ClientType</c> header is added by <see cref="MicrosoftApiClient"/> for the Cost
/// Management provider (docs/02 §2.3).
/// </remarks>
internal static class CostQueryProbe
{
    /// <summary>docs/02 §2.3: Query may use 2024-08-01.</summary>
    public const string ApiVersion = "2024-08-01";

    private static readonly object Body = new
    {
        type = "ActualCost",
        timeframe = "MonthToDate",
        dataset = new
        {
            granularity = "None",
            aggregation = new
            {
                totalCost = new { name = "Cost", function = "Sum" },
            },
        },
    };

    /// <param name="tenantId">The tenant.</param>
    /// <param name="scope">A scope from docs/02 §2.3, with or without a leading slash.</param>
    /// <param name="consentCallbackUtc">Propagation window input.</param>
    public static MicrosoftRequest Request(Guid tenantId, string scope, DateTimeOffset? consentCallbackUtc)
        => MicrosoftRequest.PostJson(
            tenantId,
            MicrosoftProvider.CostManagement,
            TokenAudience.ResourceManager,
            $"{scope.TrimStart('/')}/providers/Microsoft.CostManagement/query?api-version={ApiVersion}",
            Body,
            isIdempotent: true) with
        {
            ConsentCallbackUtc = consentCallbackUtc,
        };
}
