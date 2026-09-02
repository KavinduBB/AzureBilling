using Microsoft.Extensions.Logging;
using Mlcp.Shared.Logging;

namespace Mlcp.Shared.Resilience;

/// <summary>
/// The single entry point for calling Microsoft. Wraps the send in the tenant-and-provider
/// resilience pipeline and converts a terminal 401/403 into the <c>NeedsReconsent</c> signal.
/// </summary>
/// <remarks>
/// Every integration provider calls Microsoft through this type rather than an
/// <see cref="HttpClient"/> directly, so the retry headers, the breaker, the concurrency limit
/// and the consent-revocation handling cannot be forgotten in one call site.
/// </remarks>
public sealed class MicrosoftApiExecutor
{
    private readonly MicrosoftApiPipelines _pipelines;
    private readonly ITenantReconsentSignal _reconsentSignal;
    private readonly ILogger<MicrosoftApiExecutor> _logger;

    public MicrosoftApiExecutor(
        MicrosoftApiPipelines pipelines,
        ITenantReconsentSignal reconsentSignal,
        ILogger<MicrosoftApiExecutor> logger)
    {
        _pipelines = pipelines ?? throw new ArgumentNullException(nameof(pipelines));
        _reconsentSignal = reconsentSignal ?? throw new ArgumentNullException(nameof(reconsentSignal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Sends a request under the pipeline for <paramref name="tenantId"/> and
    /// <paramref name="provider"/>.
    /// </summary>
    /// <exception cref="NeedsReconsentException">
    /// Microsoft returned 401 or 403. The tenant has already been flagged before this throws,
    /// so the caller only has to stop.
    /// </exception>
    public async Task<HttpResponseMessage> SendAsync(
        Guid tenantId,
        MicrosoftProvider provider,
        Func<CancellationToken, ValueTask<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);

        var pipeline = _pipelines.Get(tenantId, provider);
        var response = await pipeline.ExecuteAsync(send, cancellationToken);

        if (!MicrosoftApiPipelines.IsAuthorizationFailure(response.StatusCode))
        {
            return response;
        }

        var statusCode = (int)response.StatusCode;

        _logger.LogError(
            "Microsoft returned {Status} for {Provider} on tenant {TenantId} at {RequestUri}. Flagging NeedsReconsent and halting sync.",
            statusCode,
            provider,
            tenantId,
            SensitiveDataRedactor.RedactUri(response.RequestMessage?.RequestUri?.ToString()));

        response.Dispose();

        await _reconsentSignal.SignalNeedsReconsentAsync(tenantId, provider, statusCode, cancellationToken);

        throw new NeedsReconsentException(tenantId, provider, statusCode);
    }
}
