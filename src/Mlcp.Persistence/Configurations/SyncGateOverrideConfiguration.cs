using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mlcp.Domain.Sync;
using Mlcp.Domain.Tenancy;

namespace Mlcp.Persistence.Configurations;

internal sealed class SyncGateOverrideConfiguration : IEntityTypeConfiguration<SyncGateOverride>
{
    public void Configure(EntityTypeBuilder<SyncGateOverride> builder)
    {
        builder.ToTable("SyncGateOverride");
        builder.HasKey(o => o.SyncGateOverrideId);
        builder.Property(o => o.SyncGateOverrideId).ValueGeneratedNever();

        builder.Property(o => o.JobType).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(o => o.Reason).HasMaxLength(1000).IsRequired();
        builder.Property(o => o.ApprovedBy).HasMaxLength(320).IsRequired();
        builder.Property(o => o.RowVersion).IsRowVersion();

        // The pipeline asks "is there an unused override for this tenant and job" on every full
        // load; consumed rows are history and stay out of the index.
        builder.HasIndex(o => new { o.TenantId, o.JobType, o.ExpiresUtc })
            .HasFilter("[ConsumedBySyncRunId] IS NULL");

        // No foreign key to SyncRun: both tables already cascade from Tenant, and SQL Server
        // rejects a second cascade path. The consuming run is recorded for audit only.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(o => o.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
