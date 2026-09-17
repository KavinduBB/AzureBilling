using FluentAssertions;
using Mlcp.Shared.Logging;

namespace Mlcp.UnitTests.Logging;

/// <summary>
/// P0-4's acceptance criterion: the log output of a request carrying a bearer token shows
/// <c>[REDACTED]</c>. These pin the shapes that actually leak in this system — Entra tokens,
/// Azure storage SAS URLs from invoice and cost-details downloads, and OAuth exchanges.
/// </summary>
public class SensitiveDataRedactorTests
{
    private const string SampleJwt =
        "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJhdWQiOiJodHRwczovL2dyYXBoLm1pY3Jvc29mdC5jb20ifQ.c2lnbmF0dXJlLXZhbHVl";

    [Fact]
    public void Authorization_header_value_is_removed()
    {
        var redacted = SensitiveDataRedactor.Redact($"Authorization: Bearer {SampleJwt}");

        redacted.Should().NotContain(SampleJwt);
        redacted.Should().Contain(SensitiveDataRedactor.Placeholder);
    }

    [Fact]
    public void A_bare_jwt_anywhere_in_the_text_is_removed()
    {
        // The realistic leak is not a tidy header but an SDK exception message that happens to
        // quote the token it was given.
        var redacted = SensitiveDataRedactor.Redact(
            $"MsalServiceException: token {SampleJwt} was rejected for tenant 00000000-0000-0000-0000-000000000001");

        redacted.Should().NotContain(SampleJwt);
        redacted.Should().Contain("00000000-0000-0000-0000-000000000001", "tenant ids are diagnostics, not secrets");
    }

    [Fact]
    public void Sas_signature_in_an_invoice_download_url_is_removed()
    {
        const string sasUrl =
            "https://mlcpinvoices.blob.core.windows.net/inv/G001234.pdf"
            + "?sv=2023-11-03&se=2026-09-02T12%3A00%3A00Z&sp=r&sig=abc123DEF456ghi789%2Fjkl%3D";

        var redacted = SensitiveDataRedactor.Redact(sasUrl);

        redacted.Should().NotContain("abc123DEF456ghi789");
        redacted.Should().Contain("G001234.pdf", "the document name is the useful diagnostic");
    }

    [Fact]
    public void Redacting_a_uri_keeps_the_path_and_drops_the_whole_query()
    {
        var redacted = SensitiveDataRedactor.RedactUri(
            "https://management.azure.com/providers/Microsoft.Billing/billingAccounts/abc/invoices?sig=secret&api-version=2024-04-01");

        redacted.Should().Be(
            "https://management.azure.com/providers/Microsoft.Billing/billingAccounts/abc/invoices?[REDACTED]");
    }

    [Fact]
    public void Client_secret_in_a_json_body_is_removed()
    {
        var redacted = SensitiveDataRedactor.Redact(
            """{"grant_type":"client_credentials","client_secret":"S3cr3t~Value","scope":".default"}""");

        redacted.Should().NotContain("S3cr3t~Value");
        redacted.Should().Contain(".default");
    }

    [Fact]
    public void Oauth_code_in_a_query_string_is_removed()
    {
        var redacted = SensitiveDataRedactor.Redact("/signin-oidc?code=0.AXkAabcdef&state=xyz");

        redacted.Should().NotContain("0.AXkAabcdef");
        redacted.Should().Contain("state=xyz");
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Cookie")]
    [InlineData("x-ms-authorization-auxiliary")]
    public void Sensitive_headers_are_replaced_by_name_regardless_of_value_shape(string headerName)
    {
        // Value-shape matching cannot be relied on alone: an opaque or malformed credential is
        // still a credential.
        SensitiveDataRedactor.RedactHeader(headerName, "anything-at-all")
            .Should().Be(SensitiveDataRedactor.Placeholder);
    }

    [Fact]
    public void Ordinary_headers_pass_through()
    {
        SensitiveDataRedactor.RedactHeader("ClientType", "Mlcp").Should().Be("Mlcp");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_is_returned_unchanged(string? input)
    {
        SensitiveDataRedactor.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Ordinary_diagnostic_text_survives_intact()
    {
        // Over-redaction destroys the logs' usefulness, which is its own kind of failure.
        const string message =
            "Retrying CostManagement for tenant 00000000-0000-0000-0000-000000000009: attempt 2, waiting 00:00:04, status TooManyRequests.";

        SensitiveDataRedactor.Redact(message).Should().Be(message);
    }

    [Theory]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=mlcp;AccountKey=c2VjcmV0S2V5VmFsdWU9PQ==;EndpointSuffix=core.windows.net", "c2VjcmV0S2V5VmFsdWU9PQ==", "AccountName=mlcp")]
    [InlineData("Endpoint=sb://mlcp.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=U2hhcmVkS2V5U2VjcmV0=", "U2hhcmVkS2V5U2VjcmV0=", "SharedAccessKeyName=RootManageSharedAccessKey")]
    [InlineData("BlobEndpoint=https://mlcp.blob.core.windows.net/;SharedAccessSignature=sv=2023-11-03&ss=b&sig=QmxvYlNpZ25hdHVyZQ%3D", "QmxvYlNpZ25hdHVyZQ", "BlobEndpoint=https://mlcp.blob.core.windows.net/")]
    [InlineData("Server=tcp:mlcp.database.windows.net;Database=Mlcp;User Id=mlcp;Password=Hunter 2 secret;Encrypt=True", "Hunter 2 secret", "Database=Mlcp")]
    [InlineData("Server=localhost;Uid=sa;Pwd=Pa55word!;TrustServerCertificate=True", "Pa55word!", "Uid=sa")]
    public void Secrets_in_connection_strings_are_removed(string connectionString, string secret, string survivor)
    {
        var redacted = SensitiveDataRedactor.Redact(connectionString);

        redacted.Should().NotContain(secret);
        redacted.Should().Contain(SensitiveDataRedactor.Placeholder);
        redacted.Should().Contain(survivor, "the non-secret parts identify which resource was involved");
    }

    [Theory]
    [InlineData("grant_type=refresh_token&refresh_token=0.ARoAv4j5cvGG&client_id=abc", "0.ARoAv4j5cvGG")]
    [InlineData("grant_type=client_credentials&client_secret=S3cr3t~Value&scope=.default", "S3cr3t~Value")]
    [InlineData("client_secret=S3cr3t~Value&grant_type=client_credentials", "S3cr3t~Value")]
    [InlineData("https://app.example/signin-oidc#id_token=opaque-id-token-value&state=1", "opaque-id-token-value")]
    [InlineData("{\"refresh_token\": \"0.ARoAv4j5cvGG\"}", "0.ARoAv4j5cvGG")]
    [InlineData("{\"id_token\":\"opaque\"}", "opaque")]
    public void Oauth_secrets_in_forms_fragments_and_json_are_removed(string text, string secret)
    {
        SensitiveDataRedactor.Redact(text).Should().NotContain(secret);
    }

    [Theory]
    [InlineData("request failed; Bearer 0123456789abcdefOPAQUE-token rejected", "0123456789abcdefOPAQUE-token")]
    [InlineData("headers: {\"Authorization\": \"Bearer opaque0123456789\"}", "opaque0123456789")]
    [InlineData("Authorization=Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA==")]
    [InlineData("SharedAccessSignature sr=https%3a%2f%2fmlcp.servicebus.windows.net&sig=U2lnbmF0dXJl&se=1700000000&skn=send", "U2lnbmF0dXJl")]
    public void Credentials_after_a_scheme_are_removed_anywhere(string text, string secret)
    {
        SensitiveDataRedactor.Redact(text).Should().NotContain(secret);
    }

    [Fact]
    public void An_unsigned_jwt_is_still_removed()
    {
        const string unsigned = "eyJhbGciOiJub25lIn0.eyJzdWIiOiIxMjM0NTY3ODkwIn0.";

        SensitiveDataRedactor.Redact($"token was {unsigned} end").Should().NotContain("eyJzdWIiOiIxMjM0NTY3ODkwIn0");
    }

    [Fact]
    public void A_sas_url_nested_in_another_url_is_removed()
    {
        const string text =
            "redirect to https://app.example/return?next=https%3A%2F%2Fx.blob.core.windows.net%2Fa.pdf%3Fsv%3D2023%26sig%3DTmVzdGVkU2ln%26se%3D2026";

        SensitiveDataRedactor.Redact(text).Should().NotContain("TmVzdGVkU2ln");
    }

    [Fact]
    public void A_sas_signature_in_free_text_is_removed()
    {
        SensitiveDataRedactor.Redact("download failed (sig=RnJlZVRleHRTaWc) after 3 attempts")
            .Should().NotContain("RnJlZVRleHRTaWc");
    }

    [Fact]
    public void User_info_in_a_uri_is_dropped()
    {
        SensitiveDataRedactor.RedactUri("https://user:p4ssw0rd@example.com/path")
            .Should().NotContain("p4ssw0rd").And.Contain("example.com/path");
    }

    [Theory]
    [InlineData("Graph returned error code=429 after 3 attempts")]
    [InlineData("{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"Insufficient privileges\"}}")]
    [InlineData("Basic information about the tenant was refreshed")]
    [InlineData("Bearer authentication failed for tenant 00000000-0000-0000-0000-000000000001")]
    [InlineData("SharedAccessKeyName=RootManageSharedAccessKey")]
    [InlineData("The token cache was empty; acquiring a new token.")]
    public void Diagnostic_text_that_only_looks_sensitive_survives(string text)
    {
        // Microsoft's error codes are the most useful thing in a failure log.
        SensitiveDataRedactor.Redact(text).Should().Be(text);
    }

    [Fact]
    public void Redacting_twice_changes_nothing_more()
    {
        const string text = "Authorization: Bearer abcdefghijklmnop0123; AccountKey=abc==; https://x/y?sig=zzz&sv=1";

        var once = SensitiveDataRedactor.Redact(text);

        SensitiveDataRedactor.Redact(once).Should().Be(once);
    }
}
