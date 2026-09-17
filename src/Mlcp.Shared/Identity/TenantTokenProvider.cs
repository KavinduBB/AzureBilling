using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace Mlcp.Shared.Identity;

/// <summary>
/// Acquires and caches app-only tokens for both MLCP registrations (ADR-015), per customer tenant.
/// </summary>
/// <remarks>
/// <para>
/// Tokens are cached per (tenant, app, audience) with a refresh skew so a token that expires
/// mid-batch is renewed before it is used. Credentials are cached per (tenant, app) because each
/// <c>Azure.Identity</c> credential keeps its own MSAL cache; evicting a token therefore also
/// drops the credential, otherwise MSAL would hand the same token straight back.
/// </para>
/// <para>
/// Failure handling follows ADR-016. A customer-side refusal drops the tenant's cached credential
/// and is reported to the caller, who classifies it with the consent propagation window. A
/// platform-credential failure (our certificate or secret rejected, or the certificate cannot be
/// loaded) pauses <em>all</em> token acquisition for that app for
/// <see cref="MlcpIdentityOptions.PlatformCredentialPause"/>, logs at Critical, and forces the
/// certificate to be reloaded: every customer would otherwise be failed for our fault.
/// </para>
/// </remarks>
public sealed class TenantTokenProvider : ITenantTokenProvider, IDisposable
{
    private readonly ConcurrentDictionary<(Guid TenantId, MicrosoftApp App), CredentialEntry> _credentials = new();
    private readonly ConcurrentDictionary<(Guid TenantId, MicrosoftApp App, TokenAudience Audience), AccessToken> _tokens = new();
    private readonly ConcurrentDictionary<MicrosoftApp, DateTimeOffset> _pausedUntil = new();
    private readonly List<X509Certificate2> _retiredCertificates = [];
    private readonly MlcpIdentityOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantTokenProvider> _logger;
    private readonly IClientCredentialFactory _credentialFactory;
    private readonly IClientCertificateLoader? _certificateLoader;
    private readonly SemaphoreSlim _certificateLock = new(1, 1);

    private X509Certificate2? _loadedCertificate;
    private int _certificateVersion;
    private DateTimeOffset _lastCertificateCheckUtc;
    private volatile bool _forceCertificateReload;

    public TenantTokenProvider(
        MlcpIdentityOptions options,
        TimeProvider timeProvider,
        ILogger<TenantTokenProvider> logger,
        IClientCredentialFactory? credentialFactory = null,
        IClientCertificateLoader? certificateLoader = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
        {
            throw new ArgumentException("ClientId is required to acquire app-only tokens.", nameof(options));
        }

        _credentialFactory = credentialFactory ?? new DefaultClientCredentialFactory(_options.SendCertificateChain);
        _certificateLoader = certificateLoader
            ?? (_options.KeyVaultUri is not null && !string.IsNullOrWhiteSpace(_options.CertificateName)
                ? new KeyVaultClientCertificateLoader(_options.KeyVaultUri, _options.CertificateName)
                : null);
    }

    public async Task<string> GetAccessTokenAsync(Guid tenantId, TokenAudience audience, CancellationToken cancellationToken)
    {
        var app = TokenScopes.AppFor(audience);
        var now = _timeProvider.GetUtcNow();

        if (_pausedUntil.TryGetValue(app, out var pausedUntil) && pausedUntil > now)
        {
            throw new TokenAcquisitionException(
                app,
                new TokenFailure("PlatformCredentialPaused", TokenFailureCategory.Paused),
                $"Token acquisition for the {app} app is paused until {pausedUntil:u} after a platform credential failure.")
            {
                PausedUntilUtc = pausedUntil,
            };
        }

        var key = (tenantId, app, audience);

        if (_tokens.TryGetValue(key, out var cached) && cached.ExpiresOn - _options.RefreshSkew > now)
        {
            return cached.Token;
        }

        var credential = await GetCredentialAsync(tenantId, app, cancellationToken).ConfigureAwait(false);
        var context = new TokenRequestContext([TokenScopes.For(audience)], tenantId: tenantId.ToString());

        try
        {
            var token = await credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
            _tokens[key] = token;
            return token.Token;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Every token failure is reduced to a TokenFailure and rethrown typed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var failure = TokenFailure.FromException(ex);
            HandleFailure(tenantId, app, failure);

            throw new TokenAcquisitionException(
                app,
                failure,
                $"Microsoft did not issue a {app} token for tenant {tenantId} ({failure.ErrorCode ?? failure.Category.ToString()}).",
                ex);
        }
    }

    public void Evict(Guid tenantId, MicrosoftApp app, TokenAudience audience)
    {
        _tokens.TryRemove((tenantId, app, audience), out _);
        _credentials.TryRemove((tenantId, app), out _);
    }

    private void HandleFailure(Guid tenantId, MicrosoftApp app, TokenFailure failure)
    {
        switch (failure.Category)
        {
            case TokenFailureCategory.PlatformCredential:
                Pause(app, failure.ErrorCode);
                break;

            case TokenFailureCategory.Refused:
                // A later re-consent must start from a fresh credential and an empty MSAL cache.
                _credentials.TryRemove((tenantId, app), out _);

                foreach (var tokenKey in _tokens.Keys)
                {
                    if (tokenKey.TenantId == tenantId && tokenKey.App == app)
                    {
                        _tokens.TryRemove(tokenKey, out _);
                    }
                }

                break;
        }
    }

    private void Pause(MicrosoftApp app, string? reason)
    {
        var until = _timeProvider.GetUtcNow() + _options.PlatformCredentialPause;
        _pausedUntil[app] = until;
        _forceCertificateReload = true;

        foreach (var credentialKey in _credentials.Keys)
        {
            if (credentialKey.App == app)
            {
                _credentials.TryRemove(credentialKey, out _);
            }
        }

        _logger.LogCritical(
            "Platform credential failure for the {App} app ({Reason}). All token acquisition for this app is paused until {PausedUntil:u}. Operator action required: check the certificate and the app registration.",
            app,
            reason ?? "unknown",
            until);
    }

    private async Task<TokenCredential> GetCredentialAsync(Guid tenantId, MicrosoftApp app, CancellationToken cancellationToken)
    {
        var clientId = _options.ClientIdFor(app);

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new TokenAcquisitionException(
                app,
                new TokenFailure("AppNotConfigured", TokenFailureCategory.NotConfigured),
                $"No client id is configured for the {app} app.");
        }

        X509Certificate2? certificate;

        try
        {
            certificate = await ResolveCertificateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A certificate load failure is a platform credential failure (ADR-016).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var failure = new TokenFailure("CertificateLoadFailed", TokenFailureCategory.PlatformCredential);

            // Both registrations share the certificate, so both are paused.
            Pause(MicrosoftApp.Core, failure.ErrorCode);
            Pause(MicrosoftApp.UsageInsights, failure.ErrorCode);

            throw new TokenAcquisitionException(app, failure, "The client certificate could not be loaded.", ex);
        }

        var version = Volatile.Read(ref _certificateVersion);

        if (_credentials.TryGetValue((tenantId, app), out var existing) && existing.CertificateVersion == version)
        {
            return existing.Credential;
        }

        var secret = certificate is null ? _options.ClientSecretFor(app) : null;

        if (certificate is null && string.IsNullOrWhiteSpace(secret))
        {
            throw new TokenAcquisitionException(
                app,
                new TokenFailure("CredentialNotConfigured", TokenFailureCategory.NotConfigured),
                "No client credential is configured. Provide a Key Vault certificate in Azure, or a development client secret locally.");
        }

        var credential = _credentialFactory.Create(tenantId, clientId, certificate, secret);
        _credentials[(tenantId, app)] = new CredentialEntry(credential, version);
        return credential;
    }

    /// <summary>
    /// Returns the client certificate, loading it from Key Vault on first use, when it is within
    /// <see cref="MlcpIdentityOptions.CertificateReloadWindow"/> of expiry (checked at most every
    /// <see cref="MlcpIdentityOptions.CertificateReloadCheckInterval"/>), and after a platform
    /// credential failure.
    /// </summary>
    /// <remarks>
    /// Key Vault is reached with the host's Managed Identity, so nothing in configuration can fetch
    /// the credential elsewhere. A certificate supplied by the caller is never reloaded or disposed:
    /// its owner controls its lifetime.
    /// </remarks>
    private async Task<X509Certificate2?> ResolveCertificateAsync(CancellationToken cancellationToken)
    {
        if (_options.ClientCertificate is not null)
        {
            return _options.ClientCertificate;
        }

        if (_certificateLoader is null)
        {
            return null;
        }

        if (!NeedsCertificateLoad())
        {
            return _loadedCertificate;
        }

        await _certificateLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!NeedsCertificateLoad())
            {
                return _loadedCertificate;
            }

            var now = _timeProvider.GetUtcNow();
            X509Certificate2 fresh;

            try
            {
                fresh = await _certificateLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (_loadedCertificate is not null
                && _loadedCertificate.NotAfter.ToUniversalTime() > now.UtcDateTime
                && ex is not OperationCanceledException)
            {
                // The certificate we hold still works; a failed refresh must not stop sync.
                _logger.LogWarning(ex, "Reloading the client certificate failed; keeping the current one until {NotAfter:u}.", _loadedCertificate.NotAfter);
                _lastCertificateCheckUtc = now;
                return _loadedCertificate;
            }

            _lastCertificateCheckUtc = now;
            _forceCertificateReload = false;

            if (_loadedCertificate is not null
                && string.Equals(_loadedCertificate.Thumbprint, fresh.Thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                fresh.Dispose();
                return _loadedCertificate;
            }

            if (_loadedCertificate is not null)
            {
                // In-flight requests may still hold the old one; it is disposed with the provider.
                _retiredCertificates.Add(_loadedCertificate);
            }

            _loadedCertificate = fresh;
            Interlocked.Increment(ref _certificateVersion);

            _logger.LogInformation(
                "Loaded client certificate {Thumbprint}; expires {NotAfter:u}.",
                fresh.Thumbprint,
                fresh.NotAfter);

            if (fresh.NotAfter.ToUniversalTime() - now.UtcDateTime < _options.CertificateReloadWindow)
            {
                _logger.LogCritical(
                    "The client certificate expires at {NotAfter:u}, inside the {Window} renewal window. Rotate it in Key Vault.",
                    fresh.NotAfter,
                    _options.CertificateReloadWindow);
            }

            return fresh;
        }
        finally
        {
            _certificateLock.Release();
        }
    }

    private bool NeedsCertificateLoad()
    {
        if (_loadedCertificate is null || _forceCertificateReload)
        {
            return true;
        }

        var now = _timeProvider.GetUtcNow();

        return _loadedCertificate.NotAfter.ToUniversalTime() - now.UtcDateTime < _options.CertificateReloadWindow
            && now - _lastCertificateCheckUtc >= _options.CertificateReloadCheckInterval;
    }

    public void Dispose()
    {
        _certificateLock.Dispose();

        // Only certificates this provider loaded are disposed; a caller-supplied one is theirs.
        _loadedCertificate?.Dispose();

        foreach (var retired in _retiredCertificates)
        {
            retired.Dispose();
        }
    }

    private sealed record CredentialEntry(TokenCredential Credential, int CertificateVersion);
}

/// <summary>Builds the Azure.Identity credential for one (tenant, app). A seam for tests.</summary>
public interface IClientCredentialFactory
{
    TokenCredential Create(Guid tenantId, string clientId, X509Certificate2? certificate, string? clientSecret);
}

/// <summary>Loads the client certificate. A seam for tests; Key Vault in production.</summary>
public interface IClientCertificateLoader
{
    Task<X509Certificate2> LoadAsync(CancellationToken cancellationToken);
}

internal sealed class DefaultClientCredentialFactory : IClientCredentialFactory
{
    private readonly bool _sendCertificateChain;

    public DefaultClientCredentialFactory(bool sendCertificateChain)
    {
        _sendCertificateChain = sendCertificateChain;
    }

    public TokenCredential Create(Guid tenantId, string clientId, X509Certificate2? certificate, string? clientSecret)
    {
        if (certificate is not null)
        {
            return new ClientCertificateCredential(
                tenantId.ToString(),
                clientId,
                certificate,
                new ClientCertificateCredentialOptions
                {
                    // SendCertificateChain adds the x5c header to the client assertion. Entra only
                    // uses it for registrations configured to trust a certificate by subject name
                    // and issuer, which allows rolling the certificate without re-uploading its
                    // public key. It is not proof-of-possession and does nothing for a normal
                    // registration with an uploaded public key, so it is off unless configured.
                    SendCertificateChain = _sendCertificateChain,
                });
        }

        return new ClientSecretCredential(
            tenantId.ToString(),
            clientId,
            clientSecret ?? throw new ArgumentNullException(nameof(clientSecret)));
    }
}

internal sealed class KeyVaultClientCertificateLoader : IClientCertificateLoader
{
    private readonly Azure.Security.KeyVault.Certificates.CertificateClient _client;
    private readonly string _certificateName;

    public KeyVaultClientCertificateLoader(Uri keyVaultUri, string certificateName)
    {
        _client = new Azure.Security.KeyVault.Certificates.CertificateClient(keyVaultUri, new DefaultAzureCredential());
        _certificateName = certificateName;
    }

    public async Task<X509Certificate2> LoadAsync(CancellationToken cancellationToken)
    {
        var response = await _client
            .DownloadCertificateAsync(_certificateName, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.Value;
    }
}

/// <summary>
/// Registered in Development when no app registration is configured, so the host starts and
/// every Microsoft call fails with a clear, classified reason instead of a DI error.
/// </summary>
public sealed class UnconfiguredTenantTokenProvider : ITenantTokenProvider
{
    public Task<string> GetAccessTokenAsync(Guid tenantId, TokenAudience audience, CancellationToken cancellationToken)
        => throw new TokenAcquisitionException(
            TokenScopes.AppFor(audience),
            new TokenFailure("AppNotConfigured", TokenFailureCategory.NotConfigured),
            "No app registration is configured. Set AzureAd:ClientId (and AzureAd:UsageInsightsClientId) with dotnet user-secrets.");

    public void Evict(Guid tenantId, MicrosoftApp app, TokenAudience audience)
    {
    }
}
