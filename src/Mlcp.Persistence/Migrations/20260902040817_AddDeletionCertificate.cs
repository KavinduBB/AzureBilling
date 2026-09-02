using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mlcp.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeletionCertificate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeletionCertificate",
                columns: table => new
                {
                    DeletionCertificateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantDisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisconnectedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeletedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowsDeleted = table.Column<int>(type: "int", nullable: false),
                    RowCountsByTable = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeletionCertificate", x => x.DeletionCertificateId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeletionCertificate_DeletedUtc",
                table: "DeletionCertificate",
                column: "DeletedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DeletionCertificate_TenantId",
                table: "DeletionCertificate",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeletionCertificate");
        }
    }
}
