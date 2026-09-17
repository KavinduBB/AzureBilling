using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Tenancy;
using Mlcp.Persistence;
using Mlcp.Shared.Tenancy;

namespace Mlcp.UnitTests.Audit;

/// <summary>ADR-019's schema decisions, pinned against the EF model.</summary>
public class AuditLogModelTests
{
    private static MlcpDbContext CreateContext()
        => new(
            new DbContextOptionsBuilder<MlcpDbContext>()
                .UseSqlServer("Server=model-only;Database=model-only;Integrated Security=true")
                .Options,
            FixedTenantContext.System);

    [Fact]
    public void Deleting_a_tenant_does_not_cascade_to_its_audit_log()
    {
        using var context = CreateContext();

        var foreignKey = context.Model.FindEntityType(typeof(AuditLog))!
            .GetForeignKeys()
            .Single(fk => fk.PrincipalEntityType.ClrType == typeof(Tenant));

        foreignKey.DeleteBehavior.Should().NotBe(DeleteBehavior.Cascade);
        foreignKey.DeleteBehavior.Should().NotBe(DeleteBehavior.SetNull);
    }

    [Fact]
    public void An_attempt_has_at_most_one_outcome_row()
    {
        using var context = CreateContext();

        var index = context.Model.FindEntityType(typeof(AuditLog))!
            .GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(AuditLog.AttemptAuditLogId)]));

        index.IsUnique.Should().BeTrue();
        index.GetFilter().Should().Be("[AttemptAuditLogId] IS NOT NULL");
    }

    [Fact]
    public void The_financial_impact_keeps_its_money_shape()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(AuditLog))!;

        entity.FindProperty(nameof(AuditLog.FinancialImpactAmount))!.GetColumnType().Should().Be("decimal(19,4)");
        entity.FindProperty(nameof(AuditLog.FinancialImpactCurrency))!.GetMaxLength().Should().Be(3);
    }

    [Fact]
    public void A_certificate_is_unique_per_tenant_disconnection()
    {
        using var context = CreateContext();

        var index = context.Model.FindEntityType(typeof(DeletionCertificate))!
            .GetIndexes()
            .Single(i => i.IsUnique);

        index.Properties.Select(p => p.Name).Should().Equal(
            nameof(DeletionCertificate.TenantId), nameof(DeletionCertificate.DisconnectedUtc));
    }

    [Fact]
    public void A_certificate_stores_its_digest_as_fixed_length_hex()
    {
        using var context = CreateContext();
        IReadOnlyProperty property = context.Model.FindEntityType(typeof(DeletionCertificate))!
            .FindProperty(nameof(DeletionCertificate.AuditLogSha256))!;

        property.GetMaxLength().Should().Be(64);
        property.IsNullable.Should().BeFalse();
    }
}
