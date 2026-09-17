using System.Security.Cryptography;
using Mlcp.Domain.Common;

namespace Mlcp.Domain.Tenancy;

/// <summary>
/// A non-admin request that their administrator connect the tenant. The token is a
/// single-use, expiring secret emailed to the admin; it authorises nothing on its own and is
/// only a lookup key for the consent landing page (docs/03-architecture.md §3).
/// </summary>
/// <remarks>
/// A request is bound to one recipient. Asking a different address creates a new request rather
/// than re-pointing this one, so an emailed link always leads to the request its recipient was
/// told about (ADR-018).
/// </remarks>
public class PendingConsentRequest : TenantEntity
{
    /// <summary>
    /// How many send times are kept. The longest rate-limit window is 24 hours and the per-user
    /// cap is small, so a short history answers every limit question.
    /// </summary>
    public const int SendHistoryLimit = 20;

    private readonly List<DateTimeOffset> _sendHistory = [];

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

    /// <summary>When each recent email was sent, oldest first. Feeds the rate limits (ADR-018).</summary>
    public IReadOnlyList<DateTimeOffset> SendHistory => _sendHistory.AsReadOnly();

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

        var request = new PendingConsentRequest(tenantId, nowUtc)
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

        request._sendHistory.Add(nowUtc);
        return request;
    }

    public bool IsUsable(DateTimeOffset nowUtc) => CompletedUtc is null && nowUtc < ExpiresUtc;

    /// <summary>True when this request is addressed to <paramref name="email"/> (case-insensitive).</summary>
    public bool IsAddressedTo(string email)
        => string.Equals(SentToEmail, email?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Number of emails sent for this request at or after <paramref name="sinceUtc"/>.</summary>
    public int SendsSince(DateTimeOffset sinceUtc) => _sendHistory.Count(t => t >= sinceUtc);

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

        _sendHistory.Add(nowUtc);

        if (_sendHistory.Count > SendHistoryLimit)
        {
            _sendHistory.RemoveRange(0, _sendHistory.Count - SendHistoryLimit);
        }

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

    /// <summary>Persistence projection of <see cref="SendHistory"/> as a JSON column.</summary>
    private string SendHistoryJson
    {
        get => DomainJson.Serialize(_sendHistory);
        set
        {
            _sendHistory.Clear();

            if (DomainJson.Deserialize<List<DateTimeOffset>>(value) is { } restored)
            {
                _sendHistory.AddRange(restored);
            }
        }
    }
}
