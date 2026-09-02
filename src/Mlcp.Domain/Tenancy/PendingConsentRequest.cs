using System.Security.Cryptography;
using Mlcp.Domain.Common;

namespace Mlcp.Domain.Tenancy;

/// <summary>
/// A non-admin request that their administrator connect the tenant. The token is a
/// single-use, expiring secret emailed to the admin; it authorises nothing on its own and is
/// only a lookup key for the consent landing page (docs/03-architecture.md §3).
/// </summary>
public class PendingConsentRequest : TenantEntity
{
    public Guid PendingConsentRequestId { get; private set; }

    public Guid RequestedByObjectId { get; private set; }

    public string RequestedByUpn { get; private set; } = string.Empty;

    public string SentToEmail { get; private set; } = string.Empty;

    /// <summary>URL-safe random token. Compared in constant time by the consent controller.</summary>
    public string Token { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresUtc { get; private set; }

    public DateTimeOffset? CompletedUtc { get; private set; }

    public int SendCount { get; private set; }

    public DateTimeOffset? LastSentUtc { get; private set; }

    private PendingConsentRequest()
    {
    }

    private PendingConsentRequest(Guid tenantId, DateTimeOffset nowUtc)
        : base(tenantId, nowUtc)
    {
    }

    public static PendingConsentRequest Create(
        Guid tenantId,
        Guid requestedByObjectId,
        string requestedByUpn,
        string sentToEmail,
        TimeSpan validFor,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedByUpn);
        ArgumentException.ThrowIfNullOrWhiteSpace(sentToEmail);

        return new PendingConsentRequest(tenantId, nowUtc)
        {
            PendingConsentRequestId = Guid.NewGuid(),
            RequestedByObjectId = requestedByObjectId,
            RequestedByUpn = requestedByUpn,
            SentToEmail = sentToEmail,
            Token = GenerateToken(),
            ExpiresUtc = nowUtc + validFor,
            SendCount = 1,
            LastSentUtc = nowUtc,
        };
    }

    public bool IsUsable(DateTimeOffset nowUtc) => CompletedUtc is null && nowUtc < ExpiresUtc;

    /// <summary>Re-sends the same request, extending its life. Does not mint a new token.</summary>
    public void Resend(TimeSpan validFor, DateTimeOffset nowUtc)
    {
        if (CompletedUtc is not null)
        {
            throw new DomainException("Cannot resend a consent request that has already completed.");
        }

        SendCount++;
        LastSentUtc = nowUtc;
        ExpiresUtc = nowUtc + validFor;
        Touch(nowUtc);
    }

    public void Complete(DateTimeOffset nowUtc)
    {
        CompletedUtc = nowUtc;
        Touch(nowUtc);
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
    }
}
