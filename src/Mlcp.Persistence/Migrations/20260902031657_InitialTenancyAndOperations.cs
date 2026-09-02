using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mlcp.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialTenancyAndOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Organization",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    HomeTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Region = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organization", x => x.OrganizationId);
                });

            migrationBuilder.CreateTable(
                name: "Tenant",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DefaultDomain = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Region = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ConsentGrantedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConsentGrantedByObjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AgreementTypePrimary = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsPartnerManaged = table.Column<bool>(type: "bit", nullable: false),
                    ManagingPartnerTenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Features = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeleteScheduledUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenant", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_Tenant_Organization_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organization",
                        principalColumn: "OrganizationId",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AppUser",
                columns: table => new
                {
                    AppUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntraObjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Upn = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsSubscriptionManager = table.Column<bool>(type: "bit", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ScopeSubscriptionIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUser", x => x.AppUserId);
                    table.ForeignKey(
                        name: "FK_AppUser_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    AuditLogId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActorObjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorUpn = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    OldValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OccurredUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SourceIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    FinancialImpactAmount = table.Column<decimal>(type: "decimal(19,4)", nullable: true),
                    FinancialImpactCurrency = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.AuditLogId);
                    table.ForeignKey(
                        name: "FK_AuditLog_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OnboardingStep",
                columns: table => new
                {
                    OnboardingStepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Step = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnboardingStep", x => x.OnboardingStepId);
                    table.ForeignKey(
                        name: "FK_OnboardingStep_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PendingConsentRequest",
                columns: table => new
                {
                    PendingConsentRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedByObjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedByUpn = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    SentToEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Token = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SendCount = table.Column<int>(type: "int", nullable: false),
                    LastSentUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingConsentRequest", x => x.PendingConsentRequestId);
                    table.ForeignKey(
                        name: "FK_PendingConsentRequest_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncRun",
                columns: table => new
                {
                    SyncRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobType = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RecordsProcessed = table.Column<int>(type: "int", nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ContinuationToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidationNotes = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRun", x => x.SyncRunId);
                    table.ForeignKey(
                        name: "FK_SyncRun_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantCapabilityProfile",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastProfiledUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextProfileUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Statuses = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantCapabilityProfile", x => x.TenantId);
                    table.ForeignKey(
                        name: "FK_TenantCapabilityProfile_Tenant_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenant",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppUser_TenantId_EntraObjectId",
                table: "AppUser",
                columns: new[] { "TenantId", "EntraObjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_CorrelationId",
                table: "AuditLog",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_TenantId_OccurredUtc",
                table: "AuditLog",
                columns: new[] { "TenantId", "OccurredUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_OnboardingStep_TenantId_Step",
                table: "OnboardingStep",
                columns: new[] { "TenantId", "Step" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Organization_HomeTenantId",
                table: "Organization",
                column: "HomeTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingConsentRequest_TenantId_ExpiresUtc",
                table: "PendingConsentRequest",
                columns: new[] { "TenantId", "ExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingConsentRequest_Token",
                table: "PendingConsentRequest",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncRun_TenantId_JobType_StartedUtc",
                table: "SyncRun",
                columns: new[] { "TenantId", "JobType", "StartedUtc" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Tenant_DeleteScheduledUtc",
                table: "Tenant",
                column: "DeleteScheduledUtc",
                filter: "[DeleteScheduledUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Tenant_OrganizationId",
                table: "Tenant",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenant_Status",
                table: "Tenant",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCapabilityProfile_NextProfileUtc",
                table: "TenantCapabilityProfile",
                column: "NextProfileUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppUser");

            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "OnboardingStep");

            migrationBuilder.DropTable(
                name: "PendingConsentRequest");

            migrationBuilder.DropTable(
                name: "SyncRun");

            migrationBuilder.DropTable(
                name: "TenantCapabilityProfile");

            migrationBuilder.DropTable(
                name: "Tenant");

            migrationBuilder.DropTable(
                name: "Organization");
        }
    }
}
