using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Mlcp.Domain.Audit;

namespace Mlcp.UnitTests.Audit;

/// <summary>
/// The digest recorded in a deletion certificate must be reproducible by anyone holding the
/// same rows, and must change if any of them does.
/// </summary>
public class AuditLogDigestTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 10, 0, 0, TimeSpan.FromHours(2));

    private static AuditLog Row(long id, string correlationId = "corr", string? oldValue = null)
        => AuditLogTests.Saved(
            AuditLog.ForSystem(TenantId, AuditAction.TenantConnected, "Tenant", "t-1", AuditOutcome.Succeeded, correlationId, Now)
                .WithValues(oldValue, null),
            id);

    [Fact]
    public void An_empty_log_has_the_digest_of_the_header_alone()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AuditLogDigest.FormatVersion + "\n")));

        AuditLogDigest.Compute([]).Should().Be(expected);
    }

    [Fact]
    public void The_digest_is_64_lowercase_hex_characters()
    {
        AuditLogDigest.Compute([Row(1)]).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void The_canonical_form_is_pinned()
    {
        // Pins the exact bytes. If this fails, the canonical form changed, and certificates
        // already issued can no longer be verified. Change FormatVersion rather than this value.
        var row = AuditLogTests.Saved(
            AuditLog.Attempt(TenantId, Guid.Parse("cccccccc-0000-0000-0000-000000000003"), "ann@contoso.example",
                    AuditAction.AutoRenewChanged, "Subscription", "sub-1", "corr", Now)
                .WithValues("{\"a\":1}", null)
                .WithSourceIp("203.0.113.9")
                .WithFinancialImpact(12.5m, "eur"),
            5);

        var expectedLine =
            "[5,\"aaaaaaaa-0000-0000-0000-000000000001\",null,\"cccccccc-0000-0000-0000-000000000003\","
            + "\"ann@contoso.example\",\"AutoRenewChanged\",\"Subscription\",\"sub-1\",\"{\\u0022a\\u0022:1}\",null,"
            + "\"2026-09-17T08:00:00.0000000Z\",\"203.0.113.9\",\"corr\",\"Pending\",null,\"12.5000\",\"EUR\","
            + "\"2026-09-17T08:00:00.0000000Z\"]\n";

        var expected = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(AuditLogDigest.FormatVersion + "\n" + expectedLine)));

        AuditLogDigest.Compute([row]).Should().Be(expected);
    }

    [Fact]
    public void Input_order_does_not_matter_to_compute()
    {
        AuditLogDigest.Compute([Row(2), Row(1), Row(3)])
            .Should().Be(AuditLogDigest.Compute([Row(1), Row(2), Row(3)]));
    }

    [Fact]
    public void Streaming_out_of_order_is_refused()
    {
        using var digest = new AuditLogDigest();
        digest.Append(Row(2));

        var act = () => digest.Append(Row(1));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Any_change_to_a_row_changes_the_digest()
    {
        var original = AuditLogDigest.Compute([Row(1), Row(2)]);

        AuditLogDigest.Compute([Row(1), Row(2, correlationId: "other")]).Should().NotBe(original);
        AuditLogDigest.Compute([Row(1), Row(2, oldValue: "{}")]).Should().NotBe(original);
        AuditLogDigest.Compute([Row(1), Row(3)]).Should().NotBe(original);
        AuditLogDigest.Compute([Row(1)]).Should().NotBe(original);
    }

    [Fact]
    public void A_field_boundary_cannot_be_forged_by_moving_text_between_fields()
    {
        // With naive concatenation, "ab" + "c" and "a" + "bc" would hash alike. JSON encoding
        // keeps every field separate.
        var left = AuditLogTests.Saved(
            AuditLog.ForSystem(TenantId, AuditAction.TenantConnected, "ab", "c", AuditOutcome.Succeeded, "corr", Now), 1);
        var right = AuditLogTests.Saved(
            AuditLog.ForSystem(TenantId, AuditAction.TenantConnected, "a", "bc", AuditOutcome.Succeeded, "corr", Now), 1);

        AuditLogDigest.Compute([left]).Should().NotBe(AuditLogDigest.Compute([right]));
    }

    [Fact]
    public void The_same_instant_in_another_offset_gives_the_same_digest()
    {
        // The database may hand the timestamp back in a different offset; the canonical form is UTC.
        var utc = AuditLogTests.Saved(
            AuditLog.ForSystem(TenantId, AuditAction.TenantConnected, "Tenant", null, AuditOutcome.Succeeded, "corr", Now.ToUniversalTime()), 1);
        var local = AuditLogTests.Saved(
            AuditLog.ForSystem(TenantId, AuditAction.TenantConnected, "Tenant", null, AuditOutcome.Succeeded, "corr", Now), 1);

        AuditLogDigest.Compute([utc]).Should().Be(AuditLogDigest.Compute([local]));
    }

    [Fact]
    public void Row_count_tracks_what_was_appended()
    {
        using var digest = new AuditLogDigest();
        digest.Append(Row(1));
        digest.Append(Row(5));

        digest.RowCount.Should().Be(2);
    }

    [Fact]
    public void A_finished_digest_cannot_be_reused()
    {
        using var digest = new AuditLogDigest();
        _ = digest.Finish();

        var act = () => digest.Append(Row(1));

        act.Should().Throw<InvalidOperationException>();
    }
}
