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
}
