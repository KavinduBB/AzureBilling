using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mlcp.Domain.Audit;
using Mlcp.Domain.Capabilities;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Persistence.Configurations;

internal sealed class TenantCapabilityProfileConfiguration : IEntityTypeConfiguration<TenantCapabilityProfile>
{
    public void Configure(EntityTypeBuilder<TenantCapabilityProfile> builder)
    {
        builder.ToTable("TenantCapabilityProfile");

        // Exactly one profile per tenant, so the tenant id is the whole key.
        builder.HasKey(p => p.TenantId);
        builder.Property(p => p.TenantId).ValueGeneratedNever();
        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.Property<string>("StatusesJson")
            .HasColumnName("Statuses")
            .HasColumnType("nvarchar(max)")
            .IsRequired();

        builder.Ignore(p => p.Statuses);

        // The discovery scheduler sweeps profiles whose next run is due.
        builder.HasIndex(p => p.NextProfileUtc);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SyncRunConfiguration : IEntityTypeConfiguration<SyncRun>
{
    public void Configure(EntityTypeBuilder<SyncRun> builder)
    {
        builder.ToTable("SyncRun");
        builder.HasKey(r => r.SyncRunId);

        builder.Property(r => r.JobType).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(r => r.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(r => r.ErrorCode).HasMaxLength(64);
        builder.Property(r => r.ErrorMessage).HasMaxLength(4000);
        builder.Property(r => r.ValidationNotes).HasMaxLength(4000);
        builder.Property(r => r.ContinuationToken).HasColumnType("nvarchar(max)");
        builder.Property(r => r.RowVersion).IsRowVersion();

        // Serves both "latest run of this job" for freshness labels and the sync health page.
        builder.HasIndex(r => new { r.TenantId, r.JobType, r.StartedUtc })
            .IsDescending(false, false, true);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(r => r.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLog");
        builder.HasKey(a => a.AuditLogId);
        builder.Property(a => a.AuditLogId).ValueGeneratedOnAdd();

        builder.Property(a => a.Action).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(a => a.Outcome).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(a => a.ActorUpn).HasMaxLength(320);
        builder.Property(a => a.EntityType).HasMaxLength(128).IsRequired();
        builder.Property(a => a.EntityId).HasMaxLength(256);
        builder.Property(a => a.OldValue).HasColumnType("nvarchar(max)");
        builder.Property(a => a.NewValue).HasColumnType("nvarchar(max)");
        builder.Property(a => a.SourceIp).HasMaxLength(64);
        builder.Property(a => a.CorrelationId).HasMaxLength(64).IsRequired();

        // Money always travels with its currency and is never summed across currencies.
        builder.Property(a => a.FinancialImpactAmount).HasColumnType("decimal(19,4)");
        builder.Property(a => a.FinancialImpactCurrency).HasMaxLength(3).IsFixedLength();

        builder.Property(a => a.Detail).HasMaxLength(AuditLog.MaxDetailLength);

        // Append-only (ADR-019): rows are never updated, so there is no concurrency token.
        builder.Ignore(a => a.RowVersion);

        builder.HasIndex(a => new { a.TenantId, a.OccurredUtc }).IsDescending(false, true);
        builder.HasIndex(a => a.CorrelationId);

        // An outcome row points at its attempt. The filtered unique index allows at most one
        // outcome per attempt, so AuditQueries.WithOutcome can never duplicate an action.
        builder.HasIndex(a => a.AttemptAuditLogId)
            .IsUnique()
            .HasFilter("[AttemptAuditLogId] IS NOT NULL");

        builder.HasOne<AuditLog>()
            .WithMany()
            .HasForeignKey(a => a.AttemptAuditLogId)
            .OnDelete(DeleteBehavior.NoAction);

        // No cascade (ADR-019): deleting a tenant row must never silently take its audit
        // history with it. TenantDeletionStore deletes audit rows explicitly, after taking the
        // digest that goes into the certificate.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary>
/// The deletion-integrity part of the <see cref="DeletionCertificate"/> mapping. The basic
/// mapping is in <c>DeletionCertificateConfiguration</c>. EF applies both, and they do not
/// overlap.
/// </summary>
internal sealed class DeletionCertificateIntegrityConfiguration : IEntityTypeConfiguration<DeletionCertificate>
{
    public void Configure(EntityTypeBuilder<DeletionCertificate> builder)
    {
        builder.Property(c => c.AuditLogSha256)
            .HasMaxLength(64)
            .IsUnicode(false)
            .IsFixedLength()
            .IsRequired();

        // One certificate per period of connection. Two deletion sweeps racing on the same
        // tenant already serialise on the tenant row lock. If that ever failed, this index
        // would still reject the second certificate, rolling back its deletion with it. A
        // tenant that reconnects and leaves again has a new DisconnectedUtc, so it can still
        // get a second certificate.
        builder.HasIndex(c => new { c.TenantId, c.DisconnectedUtc })
            .IsUnique()
            .HasDatabaseName("UX_DeletionCertificate_TenantId_DisconnectedUtc");
    }
}
