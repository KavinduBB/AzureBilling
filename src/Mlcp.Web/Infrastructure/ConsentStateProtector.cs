using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Mlcp.Application.Onboarding;

namespace Mlcp.Web.Infrastructure;

/// <summary>Which admin consent flow a state belongs to.</summary>
public enum ConsentFlow
{
    /// <summary>The core MLCP registration (tier 1).</summary>
    Core = 1,

    /// <summary>The separate "MLCP Usage Insights" registration (tier 2, ADR-015).</summary>
    UsageInsights = 2,
}

/// <summary>What a consent callback's <c>state</c> turned out to be.</summary>
public enum ConsentStateStatus
{
    Valid = 0,
    Missing = 1,

    /// <summary>Not produced by us, tampered with, or produced under a rotated-out key.</summary>
    Invalid = 2,
    Expired = 3,
    WrongTenant = 4,
    WrongUser = 5,

    /// <summary>The nonce was already used, or never issued to this deployment.</summary>
    Replayed = 6,
    WrongFlow = 7,
}

/// <summary>The contents of a valid consent state.</summary>
/// <param name="TenantId">The tenant that started the flow.</param>
/// <param name="ObjectId">The person who started it.</param>
/// <param name="Region">The region the administrator confirmed.</param>
/// <param name="CorrelationId">Ties the connect audit row to the callback's rows.</param>
/// <param name="Flow">Which registration's consent this is.</param>
public sealed record ConsentState(Guid TenantId, Guid ObjectId, string Region, string CorrelationId, ConsentFlow Flow);

/// <summary>The result of checking a returned state.</summary>
public sealed record ConsentStateValidation(ConsentStateStatus Status, ConsentState? State)
{
    public bool IsValid => Status == ConsentStateStatus.Valid && State is not null;
}

/// <summary>
/// Issues and consumes the bound, single-use <c>state</c> for admin consent (ADR-018).
/// </summary>
/// <remarks>
/// <para>
/// The state is <c>{tid, oid, nonce, expiry}</c> (plus region, correlation id and flow),
/// encrypted and authenticated with Data Protection, whose keys are shared across instances.
/// The nonce is also written to the distributed cache under <c>consent-nonce:{nonce}</c> and
/// deleted on first use, so a state works once, for the person and tenant that started it,
/// within ten minutes.
/// </para>
/// <para>
/// The identity checks run before the nonce is consumed: someone else's callback cannot burn a
/// legitimate administrator's nonce. The read-then-delete is not atomic across instances; the
/// only effect of winning that race is running the (idempotent) verification twice.
/// </para>
/// </remarks>
public sealed class ConsentStateProtector
{
    public const string NonceKeyPrefix = "consent-nonce:";

    private const string ProtectorPurpose = "Mlcp.AdminConsent.State.v2";

    /// <summary>An admin has this long to complete consent.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly IDataProtector _protector;
    private readonly IDistributedCache _cache;
    private readonly TimeProvider _timeProvider;

    public ConsentStateProtector(
        IDataProtectionProvider dataProtectionProvider,
        IDistributedCache cache,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Issues a state for <paramref name="user"/> and records its nonce.</summary>
    public async Task<string> CreateAsync(
        SignedInUser user,
        string region,
        string correlationId,
        ConsentFlow flow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expires = _timeProvider.GetUtcNow() + Lifetime;

        var payload = new Payload(user.TenantId, user.ObjectId, nonce, expires.ToUnixTimeSeconds(), region, correlationId, flow);

        await _cache.SetStringAsync(
            NonceKeyPrefix + nonce,
            NonceBinding(user.TenantId, user.ObjectId),
            // Relative, so the cache's own clock decides; the expiry inside the state is the
            // authoritative check.
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime },
            cancellationToken).ConfigureAwait(false);

        return _protector.Protect(JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>
    /// Validates a returned state for <paramref name="user"/> and consumes its nonce.
    /// </summary>
    /// <remarks>
    /// Every failure is an ordinary condition on a public endpoint, so nothing throws; the
    /// caller shows one fixed message whatever the status, and logs the status.
    /// </remarks>
    public async Task<ConsentStateValidation> ConsumeAsync(
        string? state,
        SignedInUser user,
        ConsentFlow expectedFlow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrWhiteSpace(state))
        {
            return new(ConsentStateStatus.Missing, null);
        }

        Payload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(state), JsonOptions);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return new(ConsentStateStatus.Invalid, null);
        }

        if (payload is null
            || payload.TenantId == Guid.Empty
            || payload.ObjectId == Guid.Empty
            || string.IsNullOrWhiteSpace(payload.Nonce)
            || string.IsNullOrWhiteSpace(payload.Region)
            || string.IsNullOrWhiteSpace(payload.CorrelationId))
        {
            return new(ConsentStateStatus.Invalid, null);
        }

        if (DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresUnix) <= _timeProvider.GetUtcNow())
        {
            return new(ConsentStateStatus.Expired, null);
        }

        if (payload.TenantId != user.TenantId)
        {
            return new(ConsentStateStatus.WrongTenant, null);
        }

        if (payload.ObjectId != user.ObjectId)
        {
            return new(ConsentStateStatus.WrongUser, null);
        }

        if (payload.Flow != expectedFlow)
        {
            return new(ConsentStateStatus.WrongFlow, null);
        }

        var key = NonceKeyPrefix + payload.Nonce;
        var binding = await _cache.GetStringAsync(key, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(binding, NonceBinding(user.TenantId, user.ObjectId), StringComparison.Ordinal))
        {
            return new(ConsentStateStatus.Replayed, null);
        }

        await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);

        return new(
            ConsentStateStatus.Valid,
            new ConsentState(payload.TenantId, payload.ObjectId, payload.Region, payload.CorrelationId, payload.Flow));
    }

    private static string NonceBinding(Guid tenantId, Guid objectId) => $"{tenantId:N}:{objectId:N}";

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');

    private sealed record Payload(
        [property: JsonPropertyName("t")] Guid TenantId,
        [property: JsonPropertyName("o")] Guid ObjectId,
        [property: JsonPropertyName("n")] string Nonce,
        [property: JsonPropertyName("e")] long ExpiresUnix,
        [property: JsonPropertyName("r")] string Region,
        [property: JsonPropertyName("c")] string CorrelationId,
        [property: JsonPropertyName("f")] ConsentFlow Flow);
}
