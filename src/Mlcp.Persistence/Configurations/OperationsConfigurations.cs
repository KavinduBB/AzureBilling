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

        // ADR-024: the mode and period a run used, its staged count (the gate's only baseline)
        // and the resume chain.
        builder.Property(r => r.LoadMode).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.PeriodKey).HasMaxLength(32);

        // Serves both "latest run of this job" for freshness labels and the sync health page.
        builder.HasIndex(r => new { r.TenantId, r.JobType, r.StartedUtc })
            .IsDescending(false, false, true);

        // The gate's baseline lookup and the resume-candidate lookup both filter on tenant, job
        // and status and take the newest.
        builder.HasIndex(r => new { r.TenantId, r.JobType, r.Status, r.CompletedUtc })
            .IsDescending(false, false, false, true);

        // The stuck-run sweeper scans running runs by age across every tenant.
        builder.HasIndex(r => new { r.Status, r.StartedUtc });

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

        // Append-only: no RowVersion, because nothing contends to update an audit row. The
        // single permitted mutation, resolving an attempted outcome, is last-writer-wins by
        // design and is itself performed once by the code path that wrote the row.
        builder.Ignore(a => a.RowVersion);

        builder.HasIndex(a => new { a.TenantId, a.OccurredUtc }).IsDescending(false, true);
        builder.HasIndex(a => a.CorrelationId);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
