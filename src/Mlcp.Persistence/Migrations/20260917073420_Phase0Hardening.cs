using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Mlcp.Persistence.Rls;
using Mlcp.Persistence.Security;

#nullable disable

namespace Mlcp.Persistence.Migrations
{
    /// <summary>
    /// The Phase 0 hardening schema (ADR-016, ADR-018, ADR-019, ADR-023, ADR-024, ADR-026) in one
    /// migration: tenant consent and re-consent state, append-only audit, the per-run validation
    /// gate, least-privilege database roles, and the role-gated RLS system clause.
    /// </summary>
    /// <remarks>
    /// Order matters. The new tenant-scoped table is created before the security policy is
    /// rebuilt to include it; roles exist before the predicate that tests membership of one. On the
    /// way down the policy is rebuilt from the earlier snapshot first, because a schema-bound
    /// policy that still names <c>SyncGateOverride</c> would block dropping that table.
    /// </remarks>
    public partial class Phase0Hardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditLog_Tenant_TenantId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "IsSubscriptionManager",
                table: "AppUser");

            migrationBuilder.AddColumn<string>(
                name: "DiscoveryDetail",
                table: "TenantCapabilityProfile",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedDomains",
                table: "TenantCapabilityProfile",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConsentCallbackUtc",
                table: "Tenant",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisconnectedUtc",
                table: "Tenant",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NeedsReconsentReason",
                table: "Tenant",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NeedsReconsentSinceUtc",
                table: "Tenant",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextReconsentProbeUtc",
                table: "Tenant",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReconsentProbeAttempts",
                table: "Tenant",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LoadMode",
                table: "SyncRun",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Full");

            migrationBuilder.AddColumn<string>(
                name: "PeriodKey",
                table: "SyncRun",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResumedFromSyncRunId",
                table: "SyncRun",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StagedRowCount",
                table: "SyncRun",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededBySyncRunId",
                table: "SyncRun",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SendHistory",
                table: "PendingConsentRequest",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "AuditLogSha256",
                table: "DeletionCertificate",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "AttemptAuditLogId",
                table: "AuditLog",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Detail",
                table: "AuditLog",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SyncGateOverride",
                columns: table => new
                {
                    SyncGateOverrideId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobType = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ConsumedBySyncRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConsumedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncGateOverride", x => x.SyncGateOverrideId);
                    table.ForeignKey(
                        name: "FK_SyncGateOverride_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tenant_NextReconsentProbeUtc",
                table: "Tenant",
                column: "NextReconsentProbeUtc",
                filter: "[NextReconsentProbeUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRun_Status_StartedUtc",
                table: "SyncRun",
                columns: new[] { "Status", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncRun_TenantId_JobType_Status_CompletedUtc",
                table: "SyncRun",
                columns: new[] { "TenantId", "JobType", "Status", "CompletedUtc" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_PendingConsentRequest_TenantId_LastSentUtc",
                table: "PendingConsentRequest",
                columns: new[] { "TenantId", "LastSentUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_DeletionCertificate_TenantId_DisconnectedUtc",
                table: "DeletionCertificate",
                columns: new[] { "TenantId", "DisconnectedUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_AttemptAuditLogId",
                table: "AuditLog",
                column: "AttemptAuditLogId",
                unique: true,
                filter: "[AttemptAuditLogId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SyncGateOverride_TenantId_JobType_ExpiresUtc",
                table: "SyncGateOverride",
                columns: new[] { "TenantId", "JobType", "ExpiresUtc" },
                filter: "[ConsumedBySyncRunId] IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLog_AuditLog_AttemptAuditLogId",
                table: "AuditLog",
                column: "AttemptAuditLogId",
                principalTable: "AuditLog",
                principalColumn: "AuditLogId");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLog_Tenant_TenantId",
                table: "AuditLog",
                column: "TenantId",
                principalTable: "Tenant",
                principalColumn: "TenantId");

            foreach (var statement in DatabaseSecurityScript.UpSql(
                TenantRlsScript.Snapshots.Hardening20260917,
                DatabaseSecurityScript.GlobalTableSnapshots.Hardening20260917))
            {
                migrationBuilder.Sql(statement);
            }

            foreach (var statement in TenantRlsScript.UpgradeToRoleGatedSystemClause(TenantRlsScript.Snapshots.Hardening20260917))
            {
                migrationBuilder.Sql(statement);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var statement in TenantRlsScript.DowngradeToSessionOnlySystemClause(TenantRlsScript.Snapshots.Initial20260902))
            {
                migrationBuilder.Sql(statement);
            }

            foreach (var statement in DatabaseSecurityScript.DownSql())
            {
                migrationBuilder.Sql(statement);
            }

            migrationBuilder.DropForeignKey(
                name: "FK_AuditLog_AuditLog_AttemptAuditLogId",
                table: "AuditLog");

            migrationBuilder.DropForeignKey(
                name: "FK_AuditLog_Tenant_TenantId",
                table: "AuditLog");

            migrationBuilder.DropTable(
                name: "SyncGateOverride");

            migrationBuilder.DropIndex(
                name: "IX_Tenant_NextReconsentProbeUtc",
                table: "Tenant");

            migrationBuilder.DropIndex(
                name: "IX_SyncRun_Status_StartedUtc",
                table: "SyncRun");

            migrationBuilder.DropIndex(
                name: "IX_SyncRun_TenantId_JobType_Status_CompletedUtc",
                table: "SyncRun");

            migrationBuilder.DropIndex(
                name: "IX_PendingConsentRequest_TenantId_LastSentUtc",
                table: "PendingConsentRequest");

            migrationBuilder.DropIndex(
                name: "UX_DeletionCertificate_TenantId_DisconnectedUtc",
                table: "DeletionCertificate");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_AttemptAuditLogId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "DiscoveryDetail",
                table: "TenantCapabilityProfile");

            migrationBuilder.DropColumn(
                name: "VerifiedDomains",
                table: "TenantCapabilityProfile");

            migrationBuilder.DropColumn(
                name: "ConsentCallbackUtc",
                table: "Tenant");

            migrationBuilder.DropColumn(
                name: "DisconnectedUtc",
                table: "Tenant");

            migrationBuilder.DropColumn(
                name: "NeedsReconsentReason",
                table: "Tenant");

            migrationBuilder.DropColumn(
                name: "NeedsReconsentSinceUtc",
                table: "Tenant");

            migrationBuilder.DropColumn(
                name: "NextReconsentProbeUtc",
                table: "Tenant");

            migrationBuilder.DropColumn(
                name: "ReconsentProbeAttempts",
                table: "Tenant");

            migrationBuilder.DropColumn(
                name: "LoadMode",
                table: "SyncRun");

            migrationBuilder.DropColumn(
                name: "PeriodKey",
                table: "SyncRun");

            migrationBuilder.DropColumn(
                name: "ResumedFromSyncRunId",
                table: "SyncRun");

            migrationBuilder.DropColumn(
                name: "StagedRowCount",
                table: "SyncRun");

            migrationBuilder.DropColumn(
                name: "SupersededBySyncRunId",
                table: "SyncRun");

            migrationBuilder.DropColumn(
                name: "SendHistory",
                table: "PendingConsentRequest");

            migrationBuilder.DropColumn(
                name: "AuditLogSha256",
                table: "DeletionCertificate");

            migrationBuilder.DropColumn(
                name: "AttemptAuditLogId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "Detail",
                table: "AuditLog");

            migrationBuilder.AddColumn<bool>(
                name: "IsSubscriptionManager",
                table: "AppUser",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddForeignKey(
                name: "FK_AuditLog_Tenant_TenantId",
                table: "AuditLog",
                column: "TenantId",
                principalTable: "Tenant",
                principalColumn: "TenantId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
