using Microsoft.Extensions.Logging;
using Mlcp.Application.Onboarding;
using Mlcp.Shared.Http;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;

namespace Mlcp.Integration.Graph;

/// <summary>
/// Confirms admin consent with an app-only core token and <c>GET /v1.0/organization?$select=id</c>
/// (ADR-018). Never changes the tenant: the caller decides.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>200 → <see cref="ConsentVerificationStatus.Verified"/>.</item>
/// <item><c>AADSTS700016</c>/<c>AADSTS7000229</c> within 10 minutes of the consent callback →
/// <see cref="ConsentVerificationStatus.PendingPropagation"/>; after that → NotGranted.</item>
/// <item>Any other lost-grant token error, 401 or 403 → <see cref="ConsentVerificationStatus.NotGranted"/>.</item>
/// <item>Transient or platform-credential failure, or anything unexpected → <see cref="ConsentVerificationStatus.Failed"/>.</item>
/// </list>
/// The consent time comes from the tenant's <c>ConsentCallbackUtc</c>. When it is not recorded
/// yet (the callback itself is verifying), the call is treated as happening at consent time.
/// </remarks>
public sealed class GraphConsentVerifier : IConsentVerifier
{
    internal const string VerificationUri = "v1.0/organization?$select=id";

    private readonly GraphApiClient _client;
    private readonly ITenantOnboardingStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GraphConsentVerifier> _logger;

    public GraphConsentVerifier(
        GraphApiClient client,
        ITenantOnboardingStore store,
        TimeProvider timeProvider,
        ILogger<GraphConsentVerifier> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ConsentVerificationResult> VerifyAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await _store.FindTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var consentedAt = tenant?.ConsentCallbackUtc ?? _timeProvider.GetUtcNow();

        var response = await _client.ProbeAsync(
            new MicrosoftRequest(tenantId, MicrosoftProvider.Graph, TokenAudience.Graph, VerificationUri)
            {
                IsFloorCall = true,
                ConsentCallbackUtc = consentedAt,
            },
            cancellationToken).ConfigureAwait(false);

        var result = Map(response);

        _logger.LogInformation(
            "Consent verification for tenant {TenantId}: {Status} ({Kind}, {ErrorCode}).",
            tenantId,
            result.Status,
            response.Kind,
            response.ErrorCode ?? response.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return result;
    }

    internal static ConsentVerificationResult Map(MicrosoftProbeResponse response)
    {
        var missingPrincipal = MicrosoftFailureClassifier.IsMissingPrincipal(response.ErrorCode);

        return response.Kind switch
        {
            MicrosoftFailureKind.None => new ConsentVerificationResult(ConsentVerificationStatus.Verified),

            // The classifier made a missing principal Transient only inside the propagation window.
            MicrosoftFailureKind.Transient when missingPrincipal => new ConsentVerificationResult(
                ConsentVerificationStatus.PendingPropagation,
                "The MLCP enterprise application is still being created in your directory."),

            MicrosoftFailureKind.GrantRevoked or MicrosoftFailureKind.FloorPermissionRemoved or MicrosoftFailureKind.CapabilityDenied
                => new ConsentVerificationResult(
                    ConsentVerificationStatus.NotGranted,
                    response.ErrorCode ?? $"HTTP {response.StatusCode}"),

            _ => new ConsentVerificationResult(
                ConsentVerificationStatus.Failed,
                response.ErrorCode ?? response.Kind.ToString()),
        };
    }
}
