using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;

namespace Mlcp.Shared.Http;

/// <summary>One outbound Microsoft request, with everything needed to send and classify it.</summary>
/// <param name="TenantId">The customer tenant.</param>
/// <param name="Provider">The Microsoft surface (breaker and limiter key; decides the ClientType header).</param>
/// <param name="Audience">The token audience; <see cref="TokenAudience.GraphReports"/> uses the Usage Insights app.</param>
/// <param name="RequestUri">Relative to the client's base address.</param>
public sealed record MicrosoftRequest(Guid TenantId, MicrosoftProvider Provider, TokenAudience Audience, string RequestUri)
{
    public HttpMethod Method { get; init; } = HttpMethod.Get;

    /// <summary>Builds the body. A factory, because a retried attempt needs a fresh content instance.</summary>
    public Func<HttpContent>? Content { get; init; }

    /// <summary>A core Graph floor call (<c>subscribedSkus</c>, <c>directory/subscriptions</c>, <c>organization</c>).</summary>
    public bool IsFloorCall { get; init; }

    /// <summary>
    /// Whether the request may be retried. Defaults to true for GET/HEAD/PUT/DELETE and false for
    /// POST/PATCH; a POST that is a read (Cost Management query) sets it explicitly.
    /// </summary>
    public bool? IsIdempotent { get; init; }

    /// <summary>When an admin last returned from the consent screen (ADR-016 rule 3 propagation window).</summary>
    public DateTimeOffset? ConsentCallbackUtc { get; init; }

    /// <summary>Extra request headers (never Authorization).</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public MicrosoftApp App => TokenScopes.AppFor(Audience);

    public bool EffectiveIdempotent => IsIdempotent
        ?? (Method == HttpMethod.Get || Method == HttpMethod.Head || Method == HttpMethod.Put || Method == HttpMethod.Delete);

    public static MicrosoftRequest Get(Guid tenantId, MicrosoftProvider provider, TokenAudience audience, string requestUri)
        => new(tenantId, provider, audience, requestUri);

    public static MicrosoftRequest PostJson<TBody>(
        Guid tenantId,
        MicrosoftProvider provider,
        TokenAudience audience,
        string requestUri,
        TBody body,
        bool isIdempotent)
        => new(tenantId, provider, audience, requestUri)
        {
            Method = HttpMethod.Post,
            Content = () => JsonContent.Create(body, options: MicrosoftApiClient.JsonOptions),
            IsIdempotent = isIdempotent,
        };

    internal MicrosoftCallDescriptor Describe()
        => new(TenantId, Provider, App, IsFloorCall, EffectiveIdempotent, ConsentCallbackUtc);
}

/// <summary>The non-throwing answer to a probe (ADR-016 rule 1).</summary>
/// <param name="Kind"><see cref="MicrosoftFailureKind.None"/> on success.</param>
/// <param name="StatusCode">HTTP status, or null when no response was received.</param>
/// <param name="ErrorCode">AADSTS code for token failures, or the API's <c>error.code</c>.</param>
/// <param name="RetryAfter">Retry hint for transient failures.</param>
public record MicrosoftProbeResponse(MicrosoftFailureKind Kind, int? StatusCode, string? ErrorCode, TimeSpan? RetryAfter)
{
    public bool Succeeded => Kind == MicrosoftFailureKind.None;
}

/// <summary>A probe answer with the deserialised body on success.</summary>
public sealed record MicrosoftProbeResponse<T>(
    MicrosoftFailureKind Kind,
    int? StatusCode,
    string? ErrorCode,
    TimeSpan? RetryAfter,
    T? Value)
    : MicrosoftProbeResponse(Kind, StatusCode, ErrorCode, RetryAfter);

/// <summary>
/// The single way the platform issues an authenticated request to Microsoft.
/// </summary>
/// <remarks>
/// <para>
/// Combines what every outbound call must have: a tenant-scoped app-only token for the right
/// registration, the resilience pipeline for that tenant and provider, ADR-016 classification,
/// and the <c>ClientType</c> header on Cost Management calls only.
/// </para>
/// <para>
/// Two styles. <see cref="ProbeAsync{T}"/> never throws for a Microsoft failure: probes record a
/// refusal as an answer. <see cref="SendAsync"/> throws the typed exceptions in
/// <see cref="MicrosoftCallException"/>'s hierarchy for refusals, exhausted transient failures,
/// throttling and platform credential failures, and returns the response otherwise (including
/// 404 and other 4xx, which only the caller can interpret). Neither ever changes a tenant.
/// </para>
/// <para>
/// On any 401/403 the cached token for (tenant, app, audience) is evicted (ADR-016 rule 4). A 401
/// is retried once with a freshly acquired token, so a 401 that survives is a real refusal.
/// </para>
/// <para>
/// Redirects are not followed (the handlers are configured that way): Graph usage reports answer
/// 302 with a pre-authenticated URL, and following it would forward our bearer token.
/// </para>
/// </remarks>
public class MicrosoftApiClient
{
    /// <summary>Identifies MLCP to Cost Management (docs/02 §2.3). Sent on Cost Management calls only.</summary>
    public const string ClientTypeHeader = "ClientType";

    public const string ClientTypeValue = "Mlcp";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ITenantTokenProvider _tokenProvider;
    private readonly MicrosoftApiExecutor _executor;

    public MicrosoftApiClient(
        HttpClient httpClient,
        ITenantTokenProvider tokenProvider,
        MicrosoftApiExecutor executor)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>Sends a probe and reports the classified status without reading the body.</summary>
    public async Task<MicrosoftProbeResponse> ProbeAsync(MicrosoftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var outcome = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        var errorCode = outcome.ErrorCode ?? await ReadErrorCodeAsync(outcome, cancellationToken).ConfigureAwait(false);

        return new MicrosoftProbeResponse(outcome.Kind, outcome.StatusCode, errorCode, outcome.RetryAfter);
    }

    /// <summary>
    /// Sends a probe and deserialises the body on success. A 2xx whose body cannot be read is
    /// reported as <see cref="MicrosoftFailureKind.Transient"/> so the previous verdict is kept.
    /// </summary>
    public async Task<MicrosoftProbeResponse<T>> ProbeAsync<T>(MicrosoftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var outcome = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        if (outcome.Kind != MicrosoftFailureKind.None || outcome.Response is null)
        {
            var errorCode = outcome.ErrorCode ?? await ReadErrorCodeAsync(outcome, cancellationToken).ConfigureAwait(false);
            return new MicrosoftProbeResponse<T>(outcome.Kind, outcome.StatusCode, errorCode, outcome.RetryAfter, default);
        }

        try
        {
            var value = await outcome.Response.Content
                .ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return new MicrosoftProbeResponse<T>(MicrosoftFailureKind.None, outcome.StatusCode, null, null, value);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            return new MicrosoftProbeResponse<T>(MicrosoftFailureKind.Transient, outcome.StatusCode, "InvalidResponseBody", null, default);
        }
    }

    /// <summary>
    /// Sends a request and returns the response (caller disposes) for success, 404 and other 4xx.
    /// </summary>
    /// <exception cref="NeedsReconsentException">GrantRevoked or FloorPermissionRemoved. The tenant has not been flagged.</exception>
    /// <exception cref="MicrosoftCallRefusedException">CapabilityDenied.</exception>
    /// <exception cref="MicrosoftThrottledException">Throttled beyond the budget, or still throttled after all retries.</exception>
    /// <exception cref="MicrosoftTransientException">Other transient failure after all retries, or an open circuit.</exception>
    /// <exception cref="MicrosoftPlatformCredentialException">MLCP's own credential failed.</exception>
    public async Task<HttpResponseMessage> SendAsync(MicrosoftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var outcome = await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        if (outcome.Kind is MicrosoftFailureKind.None or MicrosoftFailureKind.NotFound or MicrosoftFailureKind.OtherClientError)
        {
            return outcome.DetachResponse()
                ?? throw new MicrosoftTransientException(
                    request.TenantId, request.Provider, request.App, null, outcome.ErrorCode, null,
                    "Microsoft returned no response.", outcome.Exception);
        }

        throw ToException(request, outcome);
    }

    /// <summary>GETs and returns the response (see <see cref="SendAsync"/>).</summary>
    public Task<HttpResponseMessage> GetAsync(
        Guid tenantId,
        MicrosoftProvider provider,
        TokenAudience audience,
        string requestUri,
        CancellationToken cancellationToken)
        => SendAsync(MicrosoftRequest.Get(tenantId, provider, audience, requestUri), cancellationToken);

    /// <summary>POSTs a JSON body (see <see cref="SendAsync"/>). Retried only when <paramref name="isIdempotent"/>.</summary>
    public Task<HttpResponseMessage> PostJsonAsync<TBody>(
        Guid tenantId,
        MicrosoftProvider provider,
        TokenAudience audience,
        string requestUri,
        TBody body,
        bool isIdempotent,
        CancellationToken cancellationToken)
        => SendAsync(MicrosoftRequest.PostJson(tenantId, provider, audience, requestUri, body, isIdempotent), cancellationToken);

    /// <summary>GETs and deserialises a successful body; null for 404.</summary>
    /// <exception cref="HttpRequestException">Another 4xx.</exception>
    public async Task<T?> GetJsonAsync<T>(MicrosoftRequest request, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return default;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MicrosoftCallOutcome> ExecuteAsync(MicrosoftRequest request, CancellationToken cancellationToken)
    {
        var outcome = await SendOnceAsync(request, cancellationToken).ConfigureAwait(false);

        if (outcome.StatusCode is not (401 or 403))
        {
            return outcome;
        }

        _tokenProvider.Evict(request.TenantId, request.App, request.Audience);

        if (outcome.StatusCode != 401)
        {
            return outcome;
        }

        // A 401 may be a token that went stale between issue and use. One retry with a freshly
        // acquired token makes a surviving 401 a real refusal (ADR-016 GrantRevoked signal).
        outcome.Dispose();
        var retried = await SendOnceAsync(request, cancellationToken).ConfigureAwait(false);

        if (retried.StatusCode is 401 or 403)
        {
            _tokenProvider.Evict(request.TenantId, request.App, request.Audience);
        }

        return retried;
    }

    private Task<MicrosoftCallOutcome> SendOnceAsync(MicrosoftRequest request, CancellationToken cancellationToken)
        => _executor.ExecuteAsync(
            request.Describe(),
            async ct =>
            {
                // Fetched per attempt so a retry after a long backoff never replays an expired token.
                var token = await _tokenProvider.GetAccessTokenAsync(request.TenantId, request.Audience, ct).ConfigureAwait(false);

                // A new message per attempt: HttpRequestMessage cannot be sent twice.
                using var message = new HttpRequestMessage(request.Method, request.RequestUri);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                if (request.Provider == MicrosoftProvider.CostManagement)
                {
                    message.Headers.TryAddWithoutValidation(ClientTypeHeader, ClientTypeValue);
                }

                if (request.Headers is not null)
                {
                    foreach (var (name, value) in request.Headers)
                    {
                        if (!string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
                        {
                            message.Headers.TryAddWithoutValidation(name, value);
                        }
                    }
                }

                if (request.Content is not null)
                {
                    message.Content = request.Content();
                }

                return await _httpClient
                    .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    private Exception ToException(MicrosoftRequest request, MicrosoftCallOutcome outcome)
    {
        var status = outcome.StatusCode;
        var code = outcome.ErrorCode;
        var where = $"{request.Provider} for tenant {request.TenantId}";

        switch (outcome.Kind)
        {
            case MicrosoftFailureKind.GrantRevoked:
            case MicrosoftFailureKind.FloorPermissionRemoved:
                return new NeedsReconsentException(
                    outcome.Kind, request.TenantId, request.Provider, status, code,
                    $"{where}: {outcome.Kind} ({code ?? status?.ToString(System.Globalization.CultureInfo.InvariantCulture)}).",
                    outcome.Exception);

            case MicrosoftFailureKind.CapabilityDenied:
                return new MicrosoftCallRefusedException(
                    outcome.Kind, request.TenantId, request.Provider, request.App, status, code,
                    $"{where}: the {request.App} app was refused ({code ?? status?.ToString(System.Globalization.CultureInfo.InvariantCulture)}).",
                    outcome.Exception);

            case MicrosoftFailureKind.PlatformCredential:
                return new MicrosoftPlatformCredentialException(
                    request.TenantId, request.Provider, request.App, code,
                    outcome.RetryAfter ?? TimeSpan.FromMinutes(5),
                    $"{where}: MLCP's {request.App} credential is unusable ({code}).",
                    outcome.Exception);

            default:
                var budget = _executor.Options.MaxHintedDelay;

                if (outcome.RetryAfter is { } hint && (status == 429 || hint > budget))
                {
                    return new MicrosoftThrottledException(
                        request.TenantId, request.Provider, request.App, status, hint,
                        $"{where}: throttled; retry after {hint}.");
                }

                if (status == 429)
                {
                    return new MicrosoftThrottledException(
                        request.TenantId, request.Provider, request.App, status, _executor.Options.DefaultThrottleDelay,
                        $"{where}: still throttled after retries.");
                }

                return new MicrosoftTransientException(
                    request.TenantId, request.Provider, request.App, status, code, outcome.RetryAfter,
                    $"{where}: transient failure ({status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? outcome.Exception?.GetType().Name}).",
                    outcome.Exception);
        }
    }

    /// <summary>Best-effort read of an ARM/Graph <c>{"error":{"code":...}}</c> body on a failure.</summary>
    private static async Task<string?> ReadErrorCodeAsync(MicrosoftCallOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome.Response?.Content is not { } content || outcome.Kind == MicrosoftFailureKind.None)
        {
            return null;
        }

        try
        {
            using var document = await JsonDocument
                .ParseAsync(await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            return null;
        }
    }
}
