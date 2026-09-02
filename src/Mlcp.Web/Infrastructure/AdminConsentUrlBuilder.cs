using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Mlcp.Web.Infrastructure;

/// <summary>Settings for the Entra admin consent redirect.</summary>
public sealed record AdminConsentOptions
{
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Scope requested at connection. Tier 1 only: directory and licence reads. Usage reports
    /// are a separate, later consent so the first screen an admin sees asks for as little as
    /// possible (docs/03-architecture.md §4.3, ADR-008).
    /// </summary>
    public string Scope { get; init; } = "https://graph.microsoft.com/.default";

    public string Instance { get; init; } = "https://login.microsoftonline.com/";
}

/// <summary>
/// Builds the admin consent URL and protects the <c>state</c> that comes back with it.
/// </summary>
/// <remarks>
/// <para>
/// The state is encrypted and authenticated with ASP.NET Core Data Protection, whose keys live
/// in Key Vault and are shared across instances. It carries the tenant that initiated the flow
/// and an expiry, so a callback cannot be replayed later or forged for another tenant.
/// </para>
/// <para>
/// The state is a correlation check, not the source of authority: the tenant that gets marked
/// consented is taken from the returning administrator's own validated token, never from the
/// callback parameters (see <c>OnboardingController</c>).
/// </para>
/// </remarks>
public sealed class AdminConsentUrlBuilder
{
    private const string ProtectorPurpose = "Mlcp.AdminConsent.State.v1";

    /// <summary>An admin has this long to complete consent before the state is stale.</summary>
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(30);

    private readonly IDataProtector _protector;
    private readonly AdminConsentOptions _options;
    private readonly TimeProvider _timeProvider;

    public AdminConsentUrlBuilder(
        IDataProtectionProvider dataProtectionProvider,
        AdminConsentOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Builds the URL an administrator is sent to in order to grant tenant-wide consent.</summary>
    public string Build(Guid initiatingTenantId, string redirectUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        var state = ProtectState(initiatingTenantId);

        var url = new StringBuilder(_options.Instance.TrimEnd('/'))
            .Append("/organizations/v2.0/adminconsent?client_id=")
            .Append(Uri.EscapeDataString(_options.ClientId))
            .Append("&scope=")
            .Append(Uri.EscapeDataString(_options.Scope))
            .Append("&redirect_uri=")
            .Append(Uri.EscapeDataString(redirectUri))
            .Append("&state=")
            .Append(Uri.EscapeDataString(state));

        return url.ToString();
    }

    public string ProtectState(Guid initiatingTenantId)
    {
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{initiatingTenantId:D}|{_timeProvider.GetUtcNow().Add(StateLifetime).ToUnixTimeSeconds()}");

        return _protector.Protect(payload);
    }

    /// <summary>
    /// Validates a returned state and yields the tenant that started the flow.
    /// </summary>
    /// <remarks>
    /// Returns false rather than throwing for tampering and expiry alike: both are ordinary
    /// conditions on a public endpoint, and distinguishing them in a response would tell an
    /// attacker which of the two they achieved.
    /// </remarks>
    public bool TryUnprotectState(string? state, out Guid initiatingTenantId)
    {
        initiatingTenantId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        string payload;

        try
        {
            payload = _protector.Unprotect(state);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }

        var parts = payload.Split('|');

        if (parts.Length != 2
            || !Guid.TryParse(parts[0], out var tenantId)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresUnix))
        {
            return false;
        }

        if (DateTimeOffset.FromUnixTimeSeconds(expiresUnix) < _timeProvider.GetUtcNow())
        {
            return false;
        }

        initiatingTenantId = tenantId;
        return true;
    }
}
