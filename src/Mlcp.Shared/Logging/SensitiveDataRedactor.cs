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
/// unstructured one: an SDK exception message, a serialised request, a URL in a stack trace,
/// or a connection string. Every pattern carries a match timeout so hostile input cannot stall
/// a log write, and a timeout redacts the whole string rather than letting it through.
/// </para>
/// <para>
/// Parameter names come in two kinds:
/// </para>
/// <list type="bullet">
/// <item><b>Unambiguous</b> names, such as <c>sig</c>, <c>client_secret</c> or
/// <c>AccountKey</c>, are redacted wherever they appear: in a query string, a
/// <c>;</c>-separated connection string, a form body or free text.</item>
/// <item><b>Ambiguous</b> short names, such as <c>code</c>, <c>se</c>, <c>sp</c> or
/// <c>token</c>, are redacted only as URL query parameters. Otherwise "error code=429" would lose
/// the one fact worth logging.</item>
/// </list>
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
            "ServiceBusSupplementaryAuthorization",
            "ServiceBusDlqSupplementaryAuthorization",
        };

    /// <summary>
    /// Parameter names whose value is replaced wherever they appear, in any separator style.
    /// </summary>
    public static IReadOnlySet<string> UnambiguousParameterNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "access_token",
            "refresh_token",
            "id_token",
            "client_secret",
            "client_assertion",
            "assertion",
            "password",
            "pwd",
            "sig",
            "signature",
            "AccountKey",
            "SharedAccessKey",
            "SharedAccessSignature",
        };

    /// <summary>
    /// Parameter names whose value is replaced only when they appear as a URL query parameter.
    /// Covers the OAuth authorisation code and the Azure storage SAS fields.
    /// </summary>
    public static IReadOnlySet<string> QueryOnlyParameterNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "code",
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
            "skv",
            "srt",
            "ss",
            "api-key",
            "subscription-key",
        };

    /// <summary>All parameter names treated as secret, in either kind.</summary>
    public static IReadOnlySet<string> SensitiveParameterNames { get; } =
        new HashSet<string>(UnambiguousParameterNames.Concat(QueryOnlyParameterNames), StringComparer.OrdinalIgnoreCase);

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
            result = AuthorizationHeaderPattern().Replace(result, m => m.Groups["prefix"].Value + Placeholder);
            result = CredentialSchemePattern().Replace(result, m => m.Groups["prefix"].Value + Placeholder);
            result = ConnectionStringSecretPattern().Replace(result, m => m.Groups["prefix"].Value + Placeholder);
            result = UnambiguousParameterPattern().Replace(result, m => m.Groups["prefix"].Value + Placeholder);
            result = QueryParameterPattern().Replace(result, m => m.Groups["prefix"].Value + Placeholder);
            result = EncodedQueryParameterPattern().Replace(result, m => m.Groups["prefix"].Value + Placeholder);
            result = JsonSecretPattern().Replace(result, m => $"{m.Groups["prefix"].Value}\"{Placeholder}\"");
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

        // User info (https://user:password@host) is a credential too.
        var path = Redact(parsed.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped));

        return string.IsNullOrEmpty(parsed.Query) ? path : $"{path}?{Placeholder}";
    }

    // A JWT or JWS: base64url segments, the first starting with the encoded '{"' of a JOSE
    // header. The signature segment may be empty (an unsigned token is still a token).
    [GeneratedRegex(
        @"eyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]*",
        RegexOptions.CultureInvariant,
        MatchTimeoutMs)]
    private static partial Regex JwtPattern();

    // "Authorization: Bearer xyz", "Authorization=Bearer xyz", "\"Authorization\": \"Bearer xyz\""
    // and the scheme-only variants. Everything up to the end of the value goes.
    [GeneratedRegex(
        @"(?<prefix>(?:proxy-)?authorization[""']?\s*[:=]\s*[""']?(?:(?:bearer|basic|negotiate|sharedkey|pop)\s+)?)[^\s""',;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeoutMs)]
    private static partial Regex AuthorizationHeaderPattern();

    // A credential after its scheme anywhere in text: "Bearer abc", "SharedAccessSignature sr=..".
    // The value must look like a token (16+ characters with at least one digit or symbol), so
    // "basic information" or "Bearer authentication failed" survive.
    [GeneratedRegex(
        @"(?<prefix>\b(?:bearer|basic|sharedkey|SharedAccessSignature)\s+)(?=[A-Za-z0-9\-._~+/=%&:]*[0-9\-._~+/=%&:])[A-Za-z0-9\-._~+/=%&:]{16,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeoutMs)]
    private static partial Regex CredentialSchemePattern();

    // ';'-separated connection strings, where a value runs to the next ';' and may contain
    // spaces, '=' or '&' (a SharedAccessSignature value is itself a query string).
    [GeneratedRegex(
        @"(?<prefix>(?:^|[;\s""'])(?:AccountKey|SharedAccessKey|SharedAccessSignature|Password|Pwd|ClientSecret|Client\s+Secret)\s*=\s*)(?!\[REDACTED\])[^;\r\n""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeoutMs)]
    private static partial Regex ConnectionStringSecretPattern();

    // Unambiguous names in any key=value style: query strings, form bodies, free text.
    [GeneratedRegex(
        @"(?<prefix>(?:^|[?&#;,\s(""'])(?:access_token|refresh_token|id_token|client_secret|client_assertion|assertion|password|pwd|sig|signature|AccountKey|SharedAccessKey|SharedAccessSignature)\s*=\s*)(?!\[REDACTED\])[^&;\s""',)]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeoutMs)]
    private static partial Regex UnambiguousParameterPattern();

    // Ambiguous names, only as URL query parameters: sig=..., code=..., up to the next separator.
    [GeneratedRegex(
        @"(?<prefix>[?&#]\s*(?:code|token|sas|se|sp|sv|skoid|sktid|skt|ske|sks|skv|srt|ss|api-key|subscription-key)=)(?!\[REDACTED\])[^&\s""'#]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeoutMs)]
    private static partial Regex QueryParameterPattern();

    // The same, URL-encoded inside another URL (a redirect_uri or a logged return URL).
    [GeneratedRegex(
        @"(?<prefix>(?:%3F|%26)(?:sig|signature|code|token|access_token|refresh_token|id_token|client_secret|se|sp|sv|skoid|sktid|ske|sks)%3D)[^&\s""']*?(?=%26|[&\s""']|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeoutMs)]
    private static partial Regex EncodedQueryParameterPattern();

    // JSON style: "client_secret": "...". "code" is deliberately absent: in JSON it is almost
    // always a Microsoft error code, which is the most useful thing in the log, while OAuth
    // authorisation codes travel form-encoded and are caught above.
    [GeneratedRegex(
        @"(?<prefix>""(?:access_token|refresh_token|id_token|client_secret|client_assertion|assertion|password|pwd|sig|signature|token|sas|AccountKey|SharedAccessKey|SharedAccessSignature|connectionString)""\s*:\s*)""[^""]*""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeoutMs)]
    private static partial Regex JsonSecretPattern();
}
