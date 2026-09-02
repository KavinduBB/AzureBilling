using System.Net;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Retry;
using Polly.Timeout;

namespace Mlcp.Shared.Resilience;

/// <summary>Tunables for the Microsoft API resilience pipeline.</summary>
public sealed record MicrosoftApiResilienceOptions
{
    public static MicrosoftApiResilienceOptions Default { get; } = new();

    public int MaxRetryAttempts { get; init; } = 5;

    /// <summary>Base delay for exponential backoff when Microsoft sends no retry hint.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Share of calls that must fail inside the sampling window to open the breaker.</summary>
    public double CircuitFailureRatio { get; init; } = 0.5;

    public TimeSpan CircuitSamplingDuration { get; init; } = TimeSpan.FromSeconds(60);

    public int CircuitMinimumThroughput { get; init; } = 8;

    public TimeSpan CircuitBreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Per-attempt timeout. Must exceed the slowest legitimate Microsoft response.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>Concurrent in-flight calls allowed per provider across the worker.</summary>
    public int MaxConcurrency { get; init; } = 16;

    public int ConcurrencyQueueLimit { get; init; } = 64;
}

/// <summary>
/// Builds and caches the resilience pipeline every outbound Microsoft call passes through
/// (CLAUDE.md rule 7, docs/03-architecture.md §6.3).
/// </summary>
/// <remarks>
/// <para>
/// Composition order, outermost first: retry, circuit breaker, timeout, concurrency limiter.
/// Retry sits outside the breaker so a retried attempt is itself subject to the breaker's
/// state; the timeout is inside both so it bounds a single attempt rather than the whole
/// retry sequence.
/// </para>
/// <para>
/// 401 and 403 are deliberately <em>not</em> retried. They mean consent was revoked or a role
/// was removed, and no amount of backoff changes that — retrying only delays the moment the
/// customer is told to re-consent, and burns quota meanwhile.
/// </para>
/// </remarks>
public sealed class MicrosoftApiPipelines : IDisposable
{
    private readonly ResiliencePipelineRegistry<ProviderCircuitKey> _registry = new();
    private readonly MicrosoftApiResilienceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MicrosoftApiPipelines> _logger;

    public MicrosoftApiPipelines(
        MicrosoftApiResilienceOptions options,
        TimeProvider timeProvider,
        ILogger<MicrosoftApiPipelines> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The pipeline for one tenant and provider. Pipelines are cached, so the breaker keeps its
    /// state across calls, which is the whole point of keying them.
    /// </summary>
    public ResiliencePipeline<HttpResponseMessage> Get(Guid tenantId, MicrosoftProvider provider)
        => _registry.GetOrAddPipeline<HttpResponseMessage>(
            new ProviderCircuitKey(tenantId, provider),
            (builder, context) => Build(builder, context.PipelineKey));

    private void Build(ResiliencePipelineBuilder<HttpResponseMessage> builder, ProviderCircuitKey key)
    {
        builder
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(static response => IsTransient(response.StatusCode))
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = _options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = _options.BaseDelay,
                DelayGenerator = args =>
                {
                    // A server-supplied hint always wins over our own backoff curve.
                    var hinted = RetryAfterReader.Read(args.Outcome.Result, _timeProvider);
                    return ValueTask.FromResult(hinted);
                },
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "Retrying {Provider} for tenant {TenantId}: attempt {Attempt}, waiting {Delay}, status {Status}.",
                        key.Provider,
                        key.TenantId,
                        args.AttemptNumber + 1,
                        args.RetryDelay,
                        args.Outcome.Result?.StatusCode.ToString() ?? args.Outcome.Exception?.GetType().Name);

                    return ValueTask.CompletedTask;
                },
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(static response => IsTransient(response.StatusCode))
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>(),
                FailureRatio = _options.CircuitFailureRatio,
                SamplingDuration = _options.CircuitSamplingDuration,
                MinimumThroughput = _options.CircuitMinimumThroughput,
                BreakDuration = _options.CircuitBreakDuration,
                OnOpened = args =>
                {
                    _logger.LogError(
                        "Circuit opened for {Provider} on tenant {TenantId} for {BreakDuration}.",
                        key.Provider,
                        key.TenantId,
                        args.BreakDuration);

                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    _logger.LogInformation(
                        "Circuit closed for {Provider} on tenant {TenantId}.",
                        key.Provider,
                        key.TenantId);

                    return ValueTask.CompletedTask;
                },
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = _options.AttemptTimeout,
            })
            .AddConcurrencyLimiter(_options.MaxConcurrency, _options.ConcurrencyQueueLimit);
    }

    /// <summary>
    /// Whether a status is worth retrying. 408, 429 and the 5xx range are transient. 401 and
    /// 403 are not: they are answered by re-consent, not by waiting.
    /// </summary>
    public static bool IsTransient(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    /// <summary>Whether a status means the tenant's grant is gone.</summary>
    public static bool IsAuthorizationFailure(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    /// <summary>Disposes the cached pipelines, releasing their breaker and limiter state.</summary>
    public void Dispose() => _registry.Dispose();
}
