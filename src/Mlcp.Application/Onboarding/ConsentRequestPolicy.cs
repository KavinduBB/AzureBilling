using System.Net.Mail;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Application.Onboarding;

/// <summary>Why an "ask my admin" email was not sent.</summary>
public enum ConsentRequestRefusal
{
    /// <summary>Not refused: the email was sent.</summary>
    None = 0,

    /// <summary>The address is not a single, plain email address of at most 254 characters.</summary>
    InvalidAddress = 1,

    /// <summary>The address is not in one of the tenant's verified domains.</summary>
    DomainNotAllowed = 2,

    /// <summary>This person has sent the daily maximum.</summary>
    UserDailyLimit = 3,

    /// <summary>The organisation has sent the daily maximum.</summary>
    TenantDailyLimit = 4,

    /// <summary>This person sent a request within the cooldown.</summary>
    Cooldown = 5,

    /// <summary>The organisation is already connected, so there is nothing to ask for.</summary>
    AlreadyConnected = 6,
}

/// <summary>The result of asking for an admin's consent.</summary>
/// <param name="Request">The request that was sent, when it was.</param>
/// <param name="Refusal">Why nothing was sent, or <see cref="ConsentRequestRefusal.None"/>.</param>
/// <param name="RetryAfterUtc">When a rate-limited request may be tried again, if known.</param>
public sealed record ConsentRequestOutcome(
    PendingConsentRequest? Request,
    ConsentRequestRefusal Refusal,
    DateTimeOffset? RetryAfterUtc = null)
{
    public bool IsSent => Refusal == ConsentRequestRefusal.None && Request is not null;

    public static ConsentRequestOutcome Refused(ConsentRequestRefusal refusal, DateTimeOffset? retryAfterUtc = null)
        => new(null, refusal, retryAfterUtc);
}

/// <summary>
/// Limits on the "ask my admin" email (ADR-018).
/// </summary>
/// <remarks>
/// <para>
/// The email goes from MLCP's domain to someone who did not ask for it, so without limits it is
/// a way to make MLCP send mail on an attacker's behalf. The limits are: three sends per
/// requesting user and ten per tenant in any 24 hours, a 15-minute cooldown between one
/// person's sends, and recipients only in the tenant's own verified domains.
/// </para>
/// <para>
/// Pure functions over the recent requests, so the whole policy is unit-testable without a
/// database or a clock.
/// </para>
/// </remarks>
public static class ConsentRequestPolicy
{
    /// <summary>RFC 5321 path limit for a forward path, less the angle brackets.</summary>
    public const int MaxAddressLength = 254;

    public const int MaxSendsPerUserPerWindow = 3;

    public const int MaxSendsPerTenantPerWindow = 10;

    public static TimeSpan Window { get; } = TimeSpan.FromHours(24);

    /// <summary>
    /// List separators, quoting, comments and routing syntax. Legal in some RFC 5322 forms, never
    /// needed for an administrator's work address, and each one a way to smuggle a second
    /// recipient or odd parsing into the send.
    /// </summary>
    private const string ForbiddenCharacters = ",;:\"()<>[]\\";

    public static TimeSpan ResendCooldown { get; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Accepts exactly one plain address (no display name, no list, no whitespace or control
    /// characters) and returns it trimmed.
    /// </summary>
    public static bool TryNormaliseAddress(string? input, out string address)
    {
        address = string.Empty;

        var candidate = input?.Trim();

        if (string.IsNullOrEmpty(candidate)
            || candidate.Length > MaxAddressLength
            || candidate.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || ForbiddenCharacters.Contains(c)))
        {
            return false;
        }

        // MailAddress also accepts "Name <a@b>" and quoted local parts; requiring the parsed
        // address to equal the input rejects both.
        if (!MailAddress.TryCreate(candidate, out var parsed)
            || !string.Equals(parsed.Address, candidate, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(parsed.DisplayName))
        {
            return false;
        }

        var at = candidate.LastIndexOf('@');
        var localPart = candidate[..at];
        var domain = candidate[(at + 1)..];

        if (localPart.Length is 0 or > 64 || !IsHostName(domain))
        {
            return false;
        }

        address = candidate;
        return true;
    }

    /// <summary>The domain part of a normalised address, lower-cased.</summary>
    public static string DomainOf(string address)
        => address[(address.LastIndexOf('@') + 1)..].ToLowerInvariant();

    /// <summary>
    /// True when <paramref name="address"/> is in one of the tenant's domains.
    /// </summary>
    /// <param name="address">A normalised address.</param>
    /// <param name="verifiedDomains">The tenant's verified domains, or null when unknown.</param>
    /// <param name="requesterUpn">The requester's sign-in name.</param>
    /// <remarks>
    /// Before consent the verified-domain list is usually unknown. The requester's own UPN suffix
    /// is then the only domain known to belong to the tenant — Entra only allows verified domains
    /// (including <c>*.onmicrosoft.com</c>) as UPN suffixes — so it is always allowed, and is the
    /// only one allowed while the list is unknown.
    /// </remarks>
    public static bool IsDomainAllowed(string address, IReadOnlyCollection<string>? verifiedDomains, string requesterUpn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var domain = DomainOf(address);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (verifiedDomains is not null)
        {
            allowed.UnionWith(verifiedDomains.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()));
        }

        if (TryNormaliseAddress(requesterUpn, out var upn))
        {
            allowed.Add(DomainOf(upn));
        }

        return allowed.Contains(domain);
    }

    /// <summary>
    /// Applies the rate limits to a prospective send.
    /// </summary>
    /// <param name="requesterObjectId">Who is asking.</param>
    /// <param name="recentRequests">The tenant's requests last sent within <see cref="Window"/>.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <param name="retryAfterUtc">When the send would be allowed, for a rate-limited refusal.</param>
    public static ConsentRequestRefusal EvaluateRateLimits(
        Guid requesterObjectId,
        IReadOnlyCollection<PendingConsentRequest> recentRequests,
        DateTimeOffset nowUtc,
        out DateTimeOffset? retryAfterUtc)
    {
        ArgumentNullException.ThrowIfNull(recentRequests);

        retryAfterUtc = null;
        var windowStart = nowUtc - Window;

        var mySends = recentRequests
            .Where(r => r.RequestedByObjectId == requesterObjectId)
            .SelectMany(r => r.SendHistory)
            .Where(t => t >= windowStart)
            .Order()
            .ToList();

        if (mySends.Count > 0 && nowUtc - mySends[^1] < ResendCooldown)
        {
            retryAfterUtc = mySends[^1] + ResendCooldown;
            return ConsentRequestRefusal.Cooldown;
        }

        if (mySends.Count >= MaxSendsPerUserPerWindow)
        {
            retryAfterUtc = mySends[mySends.Count - MaxSendsPerUserPerWindow] + Window;
            return ConsentRequestRefusal.UserDailyLimit;
        }

        var tenantSends = recentRequests
            .SelectMany(r => r.SendHistory)
            .Where(t => t >= windowStart)
            .Order()
            .ToList();

        if (tenantSends.Count >= MaxSendsPerTenantPerWindow)
        {
            retryAfterUtc = tenantSends[tenantSends.Count - MaxSendsPerTenantPerWindow] + Window;
            return ConsentRequestRefusal.TenantDailyLimit;
        }

        return ConsentRequestRefusal.None;
    }

    private static bool IsHostName(string domain)
    {
        if (domain.Length is 0 or > 253 || !domain.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        return domain.Split('.').All(label =>
            label.Length is > 0 and <= 63
            && label[0] != '-'
            && label[^1] != '-'
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }
}
