using System.Collections.Concurrent;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Mlcp.Shared.Identity;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Retry;
using Polly.Timeout;

namespace Mlcp.Shared.Resilience;

/// <summary>Which retry-hint budget applies to calls made by this host (ADR-016 rule 7).</summary>
public enum MicrosoftCallBudget
{
    /// <summary>A person is waiting (web host). Long hints fail fast.</summary>
    Interactive = 0,

    /// <summary>A queued job (sync worker). Longer hints are slept through; longer still are re-queued.</summary>
    Worker = 1,
}

/// <summary>Tunables for the Microsoft API resilience pipeline.</summary>
public sealed record MicrosoftApiResilienceOptions
{
    public static MicrosoftApiResilienceOptions Default { get; } = new();

    public int MaxRetryAttempts { get; init; } = 5;

    /// <summary>Base delay for exponential backoff when Microsoft sends no usable retry hint.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound for our own backoff curve.</summary>
    public TimeSpan MaxBackoffDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Share of calls that must fail inside the sampling window to open the breaker.</summary>
    public double CircuitFailureRatio { get; init; } = 0.5;

    public TimeSpan CircuitSamplingDuration { get; init; } = TimeSpan.FromSeconds(60);

    public int CircuitMinimumThroughput { get; init; } = 8;

    public TimeSpan CircuitBreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Per-attempt timeout, and the only timeout: the Graph and ARM <see cref="HttpClient"/>s use
    /// <see cref="Timeout.InfiniteTimeSpan"/> (ADR-016 rule 6).
    /// </summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>Concurrent in-flight calls per provider, shared by every tenant in the process.</summary>
    public int MaxConcurrencyPerProvider { get; init; } = 16;

    /// <summary>Calls allowed to queue for a provider slot before being rejected (and retried).</summary>
    public int ConcurrencyQueueLimit { get; init; } = 256;

    /// <summary>Longest retry hint slept through on an interactive path.</summary>
    public TimeSpan MaxHintedDelayInteractive { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Longest retry hint slept through in a worker; longer hints re-queue the job.</summary>
    public TimeSpan MaxHintedDelayWorker { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Which of the two hint budgets applies in this host.</summary>
    public MicrosoftCallBudget Budget { get; init; } = MicrosoftCallBudget.Interactive;

    /// <summary>A (tenant, provider) pipeline unused for this long is dropped from the cache.</summary>
    public TimeSpan PipelineIdleEviction { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How often the cache is swept for idle pipelines.</summary>
    public TimeSpan PipelineSweepInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Re-queue delay for a throttled call when Microsoft gave no hint.</summary>
    public TimeSpan DefaultThrottleDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The hint budget for <see cref="Budget"/>.</summary>
    public TimeSpan MaxHintedDelay
        => Budget == MicrosoftCallBudget.Worker ? MaxHintedDelayWorker : MaxHintedDelayInteractive;
}

/// <summary>
/// Builds and caches the resilience pipeline every outbound Microsoft call passes through
/// (ADR-016, docs/03-architecture.md §6.3).
/// </summary>
/// <remarks>
/// <para>
/// Composition, outermost first: retry → circuit breaker (per tenant and provider) → concurrency
/// limiter (per provider, shared across tenants) → per-attempt timeout. The limiter sits inside
/// the retry so a backoff sleep does not hold a slot, and outside the timeout so time spent
/// queuing for a slot is not charged to the attempt.
/// </para>
/// <para>
/// Only transient outcomes are retried: 408, 429, 5xx, network errors, attempt timeouts and a
/// full limiter queue. 401 and 403 never are. A POST is retried only when the caller marked it
/// idempotent (<see cref="IdempotentKey"/>); Cost Management queries are. A retry hint above the
/// host's budget is not slept through: the pipeline stops and the caller raises
/// <see cref="MicrosoftThrottledException"/> so the job is re-queued (ADR-016 rule 7).
/// </para>
/// </remarks>
public sealed class MicrosoftApiPipelines : IDisposable
{
    /// <summary>Set on the <see cref="ResilienceContext"/>: may this request be sent more than once?</summary>
    public static readonly ResiliencePropertyKey<bool> IdempotentKey = new("Mlcp.Idempotent");

    private readonly ConcurrentDictionary<ProviderCircuitKey, PipelineEntry> _pipelines = new();
    private readonly ConcurrentDictionary<MicrosoftProvider, ConcurrencyLimiter> _limiters = new();
    private readonly MicrosoftApiResilienceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MicrosoftApiPipelines> _logger;
    private long _lastSweepTicks;

    public MicrosoftApiPipelines(
        MicrosoftApiResilienceOptions options,
        TimeProvider timeProvider,
        ILogger<MicrosoftApiPipelines> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lastSweepTicks = _timeProvider.GetUtcNow().UtcTicks;
    }

    public MicrosoftApiResilienceOptions Options => _options;

    /// <summary>Number of cached (tenant, provider) pipelines. Exposed for tests and metrics.</summary>
    public int CachedPipelineCount => _pipelines.Count;

    /// <summary>
    /// The pipeline for one tenant and provider. Cached, so the breaker keeps its state across
    /// calls; evicted after <see cref="MicrosoftApiResilienceOptions.PipelineIdleEviction"/> idle.
    /// </summary>
    public ResiliencePipeline<HttpResponseMessage> Get(Guid tenantId, MicrosoftProvider provider)
    {
        var now = _timeProvider.GetUtcNow();
        SweepIfDue(now);

        var entry = _pipelines.GetOrAdd(new ProviderCircuitKey(tenantId, provider), Build);
        entry.Touch(now);
        return entry.Pipeline;
    }

    /// <summary>The breaker state for one tenant and provider, or null if no pipeline is cached.</summary>
    public CircuitState? GetCircuitState(Guid tenantId, MicrosoftProvider provider)
        => _pipelines.TryGetValue(new ProviderCircuitKey(tenantId, provider), out var entry)
            ? entry.StateProvider.CircuitState
            : null;

    /// <summary>Whether a transient HTTP status is worth retrying.</summary>
    public static bool IsTransient(HttpStatusCode statusCode)
        => MicrosoftFailureClassifier.ClassifyStatus((int)statusCode, MicrosoftApp.Core, isFloorCall: false)
            == MicrosoftFailureKind.Transient;

    private void SweepIfDue(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastSweepTicks);

        if (now.UtcTicks - last < _options.PipelineSweepInterval.Ticks
            || Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) != last)
        {
            return;
        }

        var cutoff = now.UtcTicks - _options.PipelineIdleEviction.Ticks;

        foreach (var (key, entry) in _pipelines)
        {
            if (entry.LastUsedTicks < cutoff)
            {
                // An in-flight call keeps its own reference to the pipeline and finishes normally.
                _pipelines.TryRemove(new KeyValuePair<ProviderCircuitKey, PipelineEntry>(key, entry));
            }
        }
    }

    private ConcurrencyLimiter LimiterFor(MicrosoftProvider provider)
        => _limiters.GetOrAdd(provider, _ => new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = _options.MaxConcurrencyPerProvider,
            QueueLimit = _options.ConcurrencyQueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        }));

    private PipelineEntry Build(ProviderCircuitKey key)
    {
        var stateProvider = new CircuitBreakerStateProvider();
        var limiter = LimiterFor(key.Provider);

        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = _timeProvider }
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = args => ValueTask.FromResult(ShouldRetry(args.Outcome, args.Context)),
                MaxRetryAttempts = _options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = _options.BaseDelay,
                MaxDelay = _options.MaxBackoffDelay,

                // A positive server hint wins over our curve; no hint (or zero) falls back to it.
                DelayGenerator = args => ValueTask.FromResult(RetryAfterReader.Read(args.Outcome.Result, _timeProvider)),
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        "Retrying {Provider} for tenant {TenantId}: attempt {Attempt}, waiting {Delay}, outcome {Outcome}.",
                        key.Provider,
                        key.TenantId,
                        args.AttemptNumber + 1,
                        args.RetryDelay,
                        args.Outcome.Result?.StatusCode.ToString() ?? args.Outcome.Exception?.GetType().Name);

                    // The superseded response is never returned to anyone; release its connection.
                    args.Outcome.Result?.Dispose();
                    return ValueTask.CompletedTask;
                },
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = args => ValueTask.FromResult(CountsAgainstBreaker(args.Outcome)),
                FailureRatio = _options.CircuitFailureRatio,
                SamplingDuration = _options.CircuitSamplingDuration,
                MinimumThroughput = _options.CircuitMinimumThroughput,
                BreakDuration = _options.CircuitBreakDuration,
                StateProvider = stateProvider,
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
                    _logger.LogInformation("Circuit closed for {Provider} on tenant {TenantId}.", key.Provider, key.TenantId);
                    return ValueTask.CompletedTask;
                },
            })
            .AddRateLimiter(new RateLimiterStrategyOptions
            {
                RateLimiter = args => limiter.AcquireAsync(1, args.Context.CancellationToken),
                OnRejected = _ =>
                {
                    _logger.LogWarning("Concurrency queue for {Provider} is full; tenant {TenantId} will back off.", key.Provider, key.TenantId);
                    return ValueTask.CompletedTask;
                },
            })
            .AddTimeout(new TimeoutStrategyOptions { Timeout = _options.AttemptTimeout })
            .Build();

        return new PipelineEntry(pipeline, stateProvider);
    }

    private bool ShouldRetry(Outcome<HttpResponseMessage> outcome, ResilienceContext context)
    {
        if (!context.Properties.GetValue(IdempotentKey, false))
        {
            return false;
        }

        if (outcome.Exception is { } exception)
        {
            return exception is HttpRequestException or TimeoutRejectedException or RateLimiterRejectedException;
        }

        if (outcome.Result is not { } response || !IsTransient(response.StatusCode))
        {
            return false;
        }

        // A hint above the budget is not slept through; the caller re-queues instead.
        var hint = RetryAfterReader.Read(response, _timeProvider);
        return hint is null || hint <= _options.MaxHintedDelay;
    }

    private static bool CountsAgainstBreaker(Outcome<HttpResponseMessage> outcome)
        => outcome.Exception is HttpRequestException or TimeoutRejectedException
            || (outcome.Result is { } response && IsTransient(response.StatusCode));

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
        {
            limiter.Dispose();
        }

        _limiters.Clear();
        _pipelines.Clear();
    }

    private sealed class PipelineEntry
    {
        private long _lastUsedTicks;

        public PipelineEntry(ResiliencePipeline<HttpResponseMessage> pipeline, CircuitBreakerStateProvider stateProvider)
        {
            Pipeline = pipeline;
            StateProvider = stateProvider;
        }

        public ResiliencePipeline<HttpResponseMessage> Pipeline { get; }

        public CircuitBreakerStateProvider StateProvider { get; }

        public long LastUsedTicks => Interlocked.Read(ref _lastUsedTicks);

        public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastUsedTicks, now.UtcTicks);
    }
}
