using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mlcp.Domain.Common;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Persistence.Configurations;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("Organization");
        builder.HasKey(o => o.OrganizationId);

        builder.Property(o => o.Name).HasMaxLength(256).IsRequired();
        builder.Property(o => o.Region).HasMaxLength(32).IsRequired();

        builder.HasIndex(o => o.HomeTenantId);
    }
}

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenant");

        // The Entra tid is the natural key and the primary key, so the two cannot drift apart.
        builder.HasKey(t => t.TenantId);
        builder.Property(t => t.TenantId).ValueGeneratedNever();

        builder.Property(t => t.DisplayName).HasMaxLength(256).IsRequired();
        builder.Property(t => t.DefaultDomain).HasMaxLength(256);
        builder.Property(t => t.Region).HasMaxLength(32).IsRequired();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(t => t.AgreementTypePrimary).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(t => t.RowVersion).IsRowVersion();

        builder.Property(t => t.Features)
            .HasConversion(
                features => DomainJson.Serialize(features),
                json => DomainJson.Deserialize<TenantFeatures>(json) ?? TenantFeatures.Default)
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(t => t.OrganizationId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(t => t.OnboardingSteps)
            .WithOne()
            .HasForeignKey(s => s.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(t => t.OnboardingSteps).UsePropertyAccessMode(PropertyAccessMode.Field);

        // The deletion job sweeps tenants whose grace period has elapsed.
        builder.HasIndex(t => t.DeleteScheduledUtc).HasFilter("[DeleteScheduledUtc] IS NOT NULL");
        builder.HasIndex(t => t.Status);
    }
}

internal sealed class OnboardingStepConfiguration : IEntityTypeConfiguration<OnboardingStep>
{
    public void Configure(EntityTypeBuilder<OnboardingStep> builder)
    {
        builder.ToTable("OnboardingStep");
        builder.HasKey(s => s.OnboardingStepId);

        builder.Property(s => s.Step).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(s => s.Error).HasMaxLength(2048);
        builder.Property(s => s.RowVersion).IsRowVersion();

        // One row per step per tenant: the state machine is a set of facts, not a log.
        builder.HasIndex(s => new { s.TenantId, s.Step }).IsUnique();
    }
}

internal sealed class PendingConsentRequestConfiguration : IEntityTypeConfiguration<PendingConsentRequest>
{
    public void Configure(EntityTypeBuilder<PendingConsentRequest> builder)
    {
        builder.ToTable("PendingConsentRequest");
        builder.HasKey(r => r.PendingConsentRequestId);

        builder.Property(r => r.RequestedByUpn).HasMaxLength(320).IsRequired();
        builder.Property(r => r.SentToEmail).HasMaxLength(320).IsRequired();
        builder.Property(r => r.Token).HasMaxLength(64).IsRequired();
        builder.Property(r => r.RowVersion).IsRowVersion();

        // The consent landing page looks a request up by token alone, before any tenant is in
        // scope, so the token must be globally unique rather than unique within a tenant.
        builder.HasIndex(r => r.Token).IsUnique();
        builder.HasIndex(r => new { r.TenantId, r.ExpiresUtc });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(r => r.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("AppUser");
        builder.HasKey(u => u.AppUserId);

        builder.Property(u => u.Upn).HasMaxLength(320).IsRequired();
        builder.Property(u => u.DisplayName).HasMaxLength(256).IsRequired();
        builder.Property(u => u.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(u => u.RowVersion).IsRowVersion();

        builder.Property<string>("ScopeSubscriptionIdsJson")
            .HasColumnName("ScopeSubscriptionIds")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Ignore(u => u.ScopeSubscriptionIds);

        builder.HasIndex(u => new { u.TenantId, u.EntraObjectId }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(u => u.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DeletionCertificateConfiguration : IEntityTypeConfiguration<DeletionCertificate>
{
    public void Configure(EntityTypeBuilder<DeletionCertificate> builder)
    {
        builder.ToTable("DeletionCertificate");
        builder.HasKey(c => c.DeletionCertificateId);

        builder.Property(c => c.TenantDisplayName).HasMaxLength(256).IsRequired();
        builder.Property(c => c.RowCountsByTable).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(c => c.CorrelationId).HasMaxLength(64).IsRequired();

        // No foreign key to Tenant: the row it described no longer exists, which is the point.
        builder.HasIndex(c => c.TenantId);
        builder.HasIndex(c => c.DeletedUtc);
    }
}
