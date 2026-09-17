using FluentAssertions;
using Mlcp.Domain.Common;
using Mlcp.Domain.Tenancy;

namespace Mlcp.UnitTests.Tenancy;

/// <summary>A deletion certificate is a permanent claim, so it refuses to be issued incomplete.</summary>
public class DeletionCertificateTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Disconnected = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string Digest = new('0', 64);

    private static Dictionary<string, int> Counts() => new(StringComparer.Ordinal)
    {
        ["Tenant"] = 1,
        ["AuditLog"] = 4,
        ["AppUser"] = 2,
    };

    [Fact]
    public void A_certificate_records_counts_digest_and_dates()
    {
        var certificate = DeletionCertificate.Issue(TenantId, "Contoso", Disconnected, Counts(), Digest, "corr", Now);

        certificate.TenantId.Should().Be(TenantId);
        certificate.DisconnectedUtc.Should().Be(Disconnected);
        certificate.DeletedUtc.Should().Be(Now);
        certificate.RowsDeleted.Should().Be(7);
        certificate.AuditLogSha256.Should().Be(Digest);
        DomainJson.Deserialize<Dictionary<string, int>>(certificate.RowCountsByTable).Should().BeEquivalentTo(Counts());
    }

    [Fact]
    public void Counts_are_stored_in_a_stable_order()
    {
        // Two certificates for the same counts should read identically, however the dictionary
        // was built.
        var certificate = DeletionCertificate.Issue(TenantId, "Contoso", Disconnected, Counts(), Digest, "corr", Now);

        certificate.RowCountsByTable.Should().Be("""{"AppUser":2,"AuditLog":4,"Tenant":1}""");
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000ABC")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000g")]
    public void A_malformed_digest_is_refused(string digest)
    {
        var act = () => DeletionCertificate.Issue(TenantId, "Contoso", Disconnected, Counts(), digest, "corr", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Counts_without_the_audit_log_are_refused()
    {
        var counts = Counts();
        counts.Remove("AuditLog");

        var act = () => DeletionCertificate.Issue(TenantId, "Contoso", Disconnected, counts, Digest, "corr", Now);

        act.Should().Throw<ArgumentException>().WithMessage("*audit log*");
    }

    [Fact]
    public void Negative_counts_are_refused()
    {
        var counts = Counts();
        counts["AppUser"] = -1;

        var act = () => DeletionCertificate.Issue(TenantId, "Contoso", Disconnected, counts, Digest, "corr", Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_empty_tenant_id_is_refused()
    {
        var act = () => DeletionCertificate.Issue(Guid.Empty, "Contoso", Disconnected, Counts(), Digest, "corr", Now);

        act.Should().Throw<ArgumentException>();
    }
}
