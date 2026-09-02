using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Mlcp.Shared.Resilience;

namespace Mlcp.Shared.Identity;

/// <summary>Configuration for the multi-tenant application's own credential.</summary>
public sealed record MlcpIdentityOptions
{
    /// <summary>The application (client) id of the multi-tenant registration.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Name of the certificate in Key Vault used as the client credential. Production only:
    /// locally a developer supplies <see cref="ClientCertificate"/> or a secret.
    /// </summary>
    public string? CertificateName { get; init; }

    /// <summary>Key Vault URI holding the certificate, for example <c>https://mlcp-kv.vault.azure.net/</c>.</summary>
    public Uri? KeyVaultUri { get; init; }

    /// <summary>
    /// A pre-loaded certificate, used locally and in tests. In Azure the certificate is fetched
    /// from Key Vault with the app's Managed Identity instead.
    /// </summary>
    public X509Certificate2? ClientCertificate { get; init; }

    /// <summary>
    /// A client secret. Development only. Production must use a certificate
    /// (CLAUDE.md rule 4, docs/02-api-reference.md §4).
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>How long before expiry a cached token is considered stale.</summary>
    public TimeSpan RefreshSkew { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Acquires and caches app-only tokens, one credential per customer tenant.
/// </summary>
/// <remarks>
/// <para>
/// Credentials are cached per tenant because constructing one is not free and because
/// <c>Azure.Identity</c> credentials maintain their own token cache internally. Tokens are
/// additionally cached here with a refresh skew so a token that expires mid-batch is renewed
/// before it is used rather than after it fails.
/// </para>
/// <para>
/// A 401 from the token endpoint is not a transient error. <c>AADSTS65001</c> and
/// <c>invalid_grant</c> mean the customer revoked consent or removed the enterprise
/// application, so they surface as <see cref="NeedsReconsentException"/> and stop the sync
/// rather than being retried (docs/02-api-reference.md §4, CLAUDE.md rule 7).
/// </para>
/// </remarks>
public sealed class TenantTokenProvider : ITenantTokenProvider, IDisposable
{
    private readonly ConcurrentDictionary<Guid, TokenCredential> _credentials = new();
    private readonly ConcurrentDictionary<(Guid TenantId, TokenAudience Audience), AccessToken> _tokens = new();
    private readonly MlcpIdentityOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantTokenProvider> _logger;
    private readonly SemaphoreSlim _certificateLock = new(1, 1);
    private X509Certificate2? _resolvedCertificate;

    public TenantTokenProvider(
        MlcpIdentityOptions options,
        TimeProvider timeProvider,
        ILogger<TenantTokenProvider> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
        {
            throw new ArgumentException("ClientId is required to acquire app-only tokens.", nameof(options));
        }
    }

    public async Task<string> GetAccessTokenAsync(
        Guid tenantId,
        TokenAudience audience,
        CancellationToken cancellationToken)
    {
        var key = (tenantId, audience);

        if (_tokens.TryGetValue(key, out var cached) && IsUsable(cached))
        {
            return cached.Token;
        }

        var credential = await GetCredentialAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var context = new TokenRequestContext([TokenScopes.For(audience)], tenantId: tenantId.ToString());

        try
        {
            var token = await credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
            _tokens[key] = token;
            return token.Token;
        }
        catch (AuthenticationFailedException ex) when (IndicatesLostGrant(ex))
        {
            _logger.LogError(
                "Token acquisition for tenant {TenantId} failed because the grant is gone. Flagging NeedsReconsent.",
                tenantId);

            // Drop the cached credential: a later re-consent must build a fresh one.
            _credentials.TryRemove(tenantId, out _);

            throw new NeedsReconsentException(
                $"Microsoft refused an app-only token for tenant {tenantId}. Consent has been revoked or the application was removed.",
                ex);
        }
    }

    private bool IsUsable(AccessToken token)
        => token.ExpiresOn - _options.RefreshSkew > _timeProvider.GetUtcNow();

    private async Task<TokenCredential> GetCredentialAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_credentials.TryGetValue(tenantId, out var existing))
        {
            return existing;
        }

        var certificate = await ResolveCertificateAsync(cancellationToken).ConfigureAwait(false);

        TokenCredential credential = certificate is not null
            ? new ClientCertificateCredential(
                tenantId.ToString(),
                _options.ClientId,
                certificate,
                new ClientCertificateCredentialOptions
                {
                    // Proof-of-possession binds the token to our key, so a stolen token is not
                    // replayable against another client.
                    SendCertificateChain = true,
                })
            : new ClientSecretCredential(
                tenantId.ToString(),
                _options.ClientId,
                _options.ClientSecret
                    ?? throw new InvalidOperationException(
                        "No client credential is configured. Provide a Key Vault certificate in Azure, or a development client secret locally."));

        return _credentials.GetOrAdd(tenantId, credential);
    }

    /// <summary>
    /// Loads the client certificate once, from Key Vault when configured.
    /// </summary>
    /// <remarks>
    /// Key Vault is reached with the host's Managed Identity, so the platform holds no secret
    /// capable of fetching its own credential — the identity is granted by Azure at runtime and
    /// nothing in configuration or source can be replayed elsewhere.
    /// </remarks>
    private async Task<X509Certificate2?> ResolveCertificateAsync(CancellationToken cancellationToken)
    {
        if (_resolvedCertificate is not null)
        {
            return _resolvedCertificate;
        }

        if (_options.ClientCertificate is not null)
        {
            return _resolvedCertificate = _options.ClientCertificate;
        }

        if (_options.KeyVaultUri is null || string.IsNullOrWhiteSpace(_options.CertificateName))
        {
            return null;
        }

        await _certificateLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_resolvedCertificate is not null)
            {
                return _resolvedCertificate;
            }

            var client = new Azure.Security.KeyVault.Certificates.CertificateClient(
                _options.KeyVaultUri,
                new DefaultAzureCredential());

            var certificate = await client
                .DownloadCertificateAsync(_options.CertificateName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Loaded client certificate {CertificateName} from Key Vault; expires {NotAfter:u}.",
                _options.CertificateName,
                certificate.Value.NotAfter);

            return _resolvedCertificate = certificate.Value;
        }
        finally
        {
            _certificateLock.Release();
        }
    }

    /// <summary>
    /// Whether a token failure means the customer's grant is gone rather than a transient fault.
    /// </summary>
    private static bool IndicatesLostGrant(AuthenticationFailedException exception)
    {
        var message = exception.Message;

        return message.Contains("AADSTS65001", StringComparison.Ordinal)
            || message.Contains("AADSTS700016", StringComparison.Ordinal)
            || message.Contains("AADSTS7000215", StringComparison.Ordinal)
            || message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid_client", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _certificateLock.Dispose();
        _resolvedCertificate?.Dispose();
    }
}
