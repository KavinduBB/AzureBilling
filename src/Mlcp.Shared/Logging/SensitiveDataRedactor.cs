using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Mlcp.Shared.Logging;

/// <summary>
/// Removes credentials and short-lived signed URLs from text before it reaches a log sink.
/// </summary>
/// <remarks>
/// <para>
/// This is a defence in depth, not a licence to log secrets. The platform handles bearer
/// tokens for every tenant it serves and fetches invoice documents through SAS URLs that are
/// themselves bearer credentials for the duration of their validity, so a single unredacted
/// exception message or HTTP trace is a real disclosure (CLAUDE.md rule 14).
/// </para>
/// <para>
/// Patterns match values, not just named fields, because the most likely leak is an
/// unstructured one: an SDK exception message, a serialised request, a URL in a stack trace.
/// Every pattern carries a match timeout so hostile input cannot stall a log write.
/// </para>
/// </remarks>
public static partial class SensitiveDataRedactor
{
    public const string Placeholder = "[REDACTED]";

    private const int MatchTimeoutMs = 200;

    /// <summary>
    /// Header names whose entire value is replaced. Compared case-insensitively.
    /// </summary>
    public static IReadOnlySet<string> SensitiveHeaderNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization",
            "Proxy-Authorization",
            "WWW-Authenticate",
            "Cookie",
            "Set-Cookie",
            "x-ms-authorization-auxiliary",
            "X-API-Key",
            "api-key",
            "Ocp-Apim-Subscription-Key",
        };

    /// <summary>
    /// Query-string and JSON field names whose value is replaced wherever they appear. Covers
    /// OAuth exchanges and the Azure storage SAS parameters used by invoice and cost-details
    /// downloads.
    /// </summary>
    public static IReadOnlySet<string> SensitiveParameterNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "access_token",
            "refresh_token",
            "id_token",
            "client_secret",
            "code",
            "assertion",
            "client_assertion",
            "password",
            "sig",
            "signature",
            "token",
            "sas",
            "se",
            "sp",
            "sv",
            "skoid",
            "sktid",
            "skt",
            "ske",
            "sks",
            "srt",
            "ss",
        };

    /// <summary>
    /// Redacts every known secret shape in <paramref name="input"/>. Null and whitespace pass
    /// through unchanged so callers do not need to guard.
    /// </summary>
    [return: NotNullIfNotNull(nameof(input))]
    public static string? Redact(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var result = input;

        try
        {
            result = JwtPattern().Replace(result, Placeholder);
            result = AuthorizationHeaderPattern().Replace(result, $"$1{Placeholder}");
            result = SensitiveParameterPattern().Replace(result, m => $"{m.Groups[1].Value}{Placeholder}");
            result = JsonSecretPattern().Replace(result, m => $"{m.Groups[1].Value}\"{Placeholder}\"");
        }
        catch (RegexMatchTimeoutException)
        {
            // Never let a pathological string leak by falling through to the raw value.
            return Placeholder;
        }

        return result;
    }

    /// <summary>
    /// Returns the value to log for a header, replacing it entirely when the name is sensitive.
    /// </summary>
    public static string RedactHeader(string headerName, string? headerValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);

        return SensitiveHeaderNames.Contains(headerName)
            ? Placeholder
            : Redact(headerValue) ?? string.Empty;
    }

    /// <summary>
    /// Strips the query string from a URI, keeping scheme, host and path. Used for outbound
    /// Microsoft calls, where the path is the useful diagnostic and the query may carry a
    /// signature. Unparseable input is redacted wholesale rather than guessed at.
    /// </summary>
    public static string RedactUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return Redact(uri) ?? string.Empty;
        }

        return string.IsNullOrEmpty(parsed.Query)
            ? parsed.GetLeftPart(UriPartial.Path)
            : $"{parsed.GetLeftPart(UriPartial.Path)}?{Placeholder}";
    }

    // A JWT: three base64url segments, the first starting with the encoded '{"' of a JOSE header.
    [GeneratedRegex(
        @"eyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}",
        RegexOptions.CultureInvariant,
        MatchTimeoutMs)]
    private static partial Regex JwtPattern();

    // "Authorization: Bearer xyz", "Authorization=Bearer xyz" and the scheme-only variants.
    [GeneratedRegex(
        @"(?<prefix>(?:authorization|proxy-authorization)\s*[:=]\s*(?:bearer|basic|negotiate|sharedkey)?\s*)\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeoutMs)]
    private static partial Regex AuthorizationHeaderPattern();

    // Query-string style: sig=..., access_token=..., up to the next separator.
    [GeneratedRegex(
        @"(?<prefix>[?&;]\s*(?:access_token|refresh_token|id_token|client_secret|code|assertion|client_assertion|password|sig|signature|token|sas|se|sp|sv|skoid|sktid|skt|ske|sks|srt|ss)=)[^&\s""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeoutMs)]
    private static partial Regex SensitiveParameterPattern();

    // JSON style: "client_secret": "...".
    [GeneratedRegex(
        @"(?<prefix>""(?:access_token|refresh_token|id_token|client_secret|code|assertion|client_assertion|password|sig|signature|token|sas)""\s*:\s*)""[^""]*""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeoutMs)]
    private static partial Regex JsonSecretPattern();
}
