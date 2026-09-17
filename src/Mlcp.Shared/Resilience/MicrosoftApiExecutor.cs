using Microsoft.Extensions.Logging;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace Mlcp.Shared.Resilience;

/// <summary>What the executor needs to know about a call to run and classify it.</summary>
/// <param name="TenantId">The customer tenant.</param>
/// <param name="Provider">The Microsoft surface (breaker and limiter key).</param>
/// <param name="App">The registration whose token is used.</param>
/// <param name="IsFloorCall">One of the core Graph floor calls (ADR-016).</param>
/// <param name="IsIdempotent">The request may be sent more than once.</param>
/// <param name="ConsentCallbackUtc">For the propagation window on missing-principal errors.</param>
public readonly record struct MicrosoftCallDescriptor(
    Guid TenantId,
    MicrosoftProvider Provider,
    MicrosoftApp App,
    bool IsFloorCall,
    bool IsIdempotent,
    DateTimeOffset? ConsentCallbackUtc);

/// <summary>
/// The classified result of one Microsoft call. Owns <see cref="Response"/> when present.
/// </summary>
public sealed class MicrosoftCallOutcome : IDisposable
{
    public MicrosoftCallOutcome(
        MicrosoftFailureKind kind,
        HttpResponseMessage? response,
        string? errorCode = null,
        TimeSpan? retryAfter = null,
        Exception? exception = null)
    {
        Kind = kind;
        Response = response;
        ErrorCode = errorCode;
        RetryAfter = retryAfter;
        Exception = exception;
    }

    public MicrosoftFailureKind Kind { get; }

    /// <summary>The final HTTP response, or null when no response was received (token, transport, circuit).</summary>
    public HttpResponseMessage? Response { get; private set; }

    public int? StatusCode => Response is null ? null : (int)Response.StatusCode;

    /// <summary>AADSTS or OAuth code for token failures.</summary>
    public string? ErrorCode { get; }

    /// <summary>When a retry is worthwhile, from a server hint, the breaker, or a credential pause.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The exception that ended the call, when there was no response.</summary>
    public Exception? Exception { get; }

    /// <summary>Hands the response to the caller, who then owns it.</summary>
    public HttpResponseMessage? DetachResponse()
    {
        var response = Response;
        Response = null;
        return response;
    }

    public void Dispose() => Response?.Dispose();
}

/// <summary>
/// Runs a Microsoft call through the (tenant, provider) resilience pipeline and classifies the
/// outcome (ADR-016). It never throws for a Microsoft failure and never touches a tenant.
/// </summary>
/// <remarks>
/// A singleton: it holds no per-request state. The previous design flagged tenants from here
/// through an out-of-band DbContext, which raced the owning service's own context; that
/// responsibility now belongs to the service that owns the tenant aggregate (ADR-016 rule 2).
/// </remarks>
public sealed class MicrosoftApiExecutor
{
    private readonly MicrosoftApiPipelines _pipelines;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MicrosoftApiExecutor> _logger;

    public MicrosoftApiExecutor(
        MicrosoftApiPipelines pipelines,
        TimeProvider timeProvider,
        ILogger<MicrosoftApiExecutor> logger)
    {
        _pipelines = pipelines ?? throw new ArgumentNullException(nameof(pipelines));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public MicrosoftApiResilienceOptions Options => _pipelines.Options;

    /// <summary>Runs <paramref name="send"/> under the pipeline and classifies what happened.</summary>
    /// <exception cref="OperationCanceledException">Only when <paramref name="cancellationToken"/> is cancelled.</exception>
    public async Task<MicrosoftCallOutcome> ExecuteAsync(
        MicrosoftCallDescriptor call,
        Func<CancellationToken, ValueTask<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);

        var pipeline = _pipelines.Get(call.TenantId, call.Provider);
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(MicrosoftApiPipelines.IdempotentKey, call.IsIdempotent);

        HttpResponseMessage response;

        try
        {
            response = await pipeline
                .ExecuteAsync(static (ctx, state) => state(ctx.CancellationToken), context, send)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TokenAcquisitionException ex)
        {
            var now = _timeProvider.GetUtcNow();
            var kind = MicrosoftFailureClassifier.ClassifyTokenFailure(ex.Failure, call.App, call.ConsentCallbackUtc, now);

            TimeSpan? retryAfter = kind switch
            {
                MicrosoftFailureKind.PlatformCredential => ex.PausedUntilUtc is { } until && until > now
                    ? until - now
                    : TimeSpan.FromMinutes(5),
                MicrosoftFailureKind.Transient when MicrosoftFailureClassifier.IsMissingPrincipal(ex.Failure.ErrorCode)
                    => TimeSpan.FromMinutes(2),
                _ => null,
            };

            _logger.LogWarning(
                "Token for {App} on tenant {TenantId} was refused ({ErrorCode}); classified {Kind}.",
                call.App,
                call.TenantId,
                ex.Failure.ErrorCode ?? ex.Failure.Category.ToString(),
                kind);

            return new MicrosoftCallOutcome(kind, response: null, ex.Failure.ErrorCode, retryAfter, ex);
        }
        catch (BrokenCircuitException ex)
        {
            return Transient(call, ex, ex.RetryAfter ?? _pipelines.Options.CircuitBreakDuration);
        }
        catch (RateLimiterRejectedException ex)
        {
            return Transient(call, ex, ex.RetryAfter);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or TimeoutException or TaskCanceledException)
        {
            return Transient(call, ex, retryAfter: null);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }

        var status = (int)response.StatusCode;
        var classified = MicrosoftFailureClassifier.ClassifyStatus(status, call.App, call.IsFloorCall);

        if (classified == MicrosoftFailureKind.None)
        {
            return new MicrosoftCallOutcome(classified, response);
        }

        var hint = classified == MicrosoftFailureKind.Transient ? RetryAfterReader.Read(response, _timeProvider) : null;

        _logger.LogWarning(
            "{Provider} returned {Status} for tenant {TenantId} at {RequestUri}; classified {Kind}.",
            call.Provider,
            status,
            call.TenantId,
            SensitiveDataRedactor.RedactUri(response.RequestMessage?.RequestUri?.ToString()),
            classified);

        return new MicrosoftCallOutcome(classified, response, errorCode: null, hint);
    }

    private MicrosoftCallOutcome Transient(MicrosoftCallDescriptor call, Exception exception, TimeSpan? retryAfter)
    {
        _logger.LogWarning(
            "{Provider} call for tenant {TenantId} failed transiently: {Error}.",
            call.Provider,
            call.TenantId,
            exception.GetType().Name);

        return new MicrosoftCallOutcome(MicrosoftFailureKind.Transient, response: null, errorCode: null, retryAfter, exception);
    }
}
