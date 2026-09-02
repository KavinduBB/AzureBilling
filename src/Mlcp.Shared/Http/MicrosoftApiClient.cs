using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Mlcp.Shared.Identity;
using Mlcp.Shared.Resilience;

namespace Mlcp.Shared.Http;

/// <summary>
/// The single way the platform issues an authenticated request to Microsoft.
/// </summary>
/// <remarks>
/// <para>
/// Combines the three things every outbound call must have and that are easy to omit one at a
/// time: a tenant-scoped app-only token, the resilience pipeline for that tenant and provider,
/// and the <c>ClientType</c> header Cost Management requires. Providers depend on this rather
/// than on <see cref="HttpClient"/> so none of them can be written without those.
/// </para>
/// <para>
/// It deliberately does not follow redirects. Graph usage reports answer 302 with a
/// pre-authenticated URL, and following that automatically would forward our
/// <c>Authorization</c> header to a storage endpoint that neither needs nor should see it.
/// </para>
/// </remarks>
public class MicrosoftApiClient
{
    /// <summary>
    /// Identifies MLCP to Cost Management. Required on every query
    /// (docs/02-api-reference.md §2.3); Microsoft applies per-ClientType quota with it.
    /// </summary>
    public const string ClientTypeHeader = "ClientType";

    public const string ClientTypeValue = "Mlcp";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    /// <summary>Issues a GET and returns the raw response, which the caller must dispose.</summary>
    public Task<HttpResponseMessage> GetAsync(
        Guid tenantId,
        MicrosoftProvider provider,
        TokenAudience audience,
        string requestUri,
        CancellationToken cancellationToken)
        => SendAsync(tenantId, provider, audience, HttpMethod.Get, requestUri, content: null, cancellationToken);

    /// <summary>Issues a POST with a JSON body.</summary>
    public Task<HttpResponseMessage> PostJsonAsync<TBody>(
        Guid tenantId,
        MicrosoftProvider provider,
        TokenAudience audience,
        string requestUri,
        TBody body,
        CancellationToken cancellationToken)
        => SendAsync(
            tenantId,
            provider,
            audience,
            HttpMethod.Post,
            requestUri,
            () => JsonContent.Create(body, options: JsonOptions),
            cancellationToken);

    /// <summary>
    /// GETs and deserialises, or returns <c>default</c> when the response is not a success.
    /// </summary>
    /// <remarks>
    /// Used by probes, where "we were refused" is a legitimate answer rather than a fault. A
    /// 401 or 403 still throws, because that is a lost grant and must halt the tenant's sync,
    /// not be recorded as an ordinary absence.
    /// </remarks>
    public async Task<T?> GetJsonOrDefaultAsync<T>(
        Guid tenantId,
        MicrosoftProvider provider,
        TokenAudience audience,
        string requestUri,
        CancellationToken cancellationToken)
    {
        using var response = await GetAsync(tenantId, provider, audience, requestUri, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return default;
        }

        return await response.Content
            .ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Returns whether a GET succeeded, without reading the body. For cheap probes.</summary>
    public async Task<bool> CanReadAsync(
        Guid tenantId,
        MicrosoftProvider provider,
        TokenAudience audience,
        string requestUri,
        CancellationToken cancellationToken)
    {
        using var response = await GetAsync(tenantId, provider, audience, requestUri, cancellationToken)
            .ConfigureAwait(false);

        // 302 counts as readable: usage reports answer with a redirect to a pre-authenticated
        // CSV, which means the permission is present (docs/02-api-reference.md §1.2).
        return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Redirect;
    }

    private Task<HttpResponseMessage> SendAsync(
        Guid tenantId,
        MicrosoftProvider provider,
        TokenAudience audience,
        HttpMethod method,
        string requestUri,
        Func<HttpContent>? content,
        CancellationToken cancellationToken)
    {
        return _executor.SendAsync(
            tenantId,
            provider,
            async ct =>
            {
                // The token is fetched inside the delegate so a retry after a long backoff uses
                // a fresh one rather than replaying an expired token and failing again.
                var token = await _tokenProvider.GetAccessTokenAsync(tenantId, audience, ct).ConfigureAwait(false);

                // A new request per attempt: HttpRequestMessage cannot be sent twice.
                using var request = new HttpRequestMessage(method, requestUri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation(ClientTypeHeader, ClientTypeValue);

                if (content is not null)
                {
                    request.Content = content();
                }

                return await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }
}
