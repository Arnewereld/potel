using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Belastingpot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaxReserveYears",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Wage = table.Column<decimal>(type: "TEXT", nullable: false),
                    WageTaxWithheld = table.Column<decimal>(type: "TEXT", nullable: false),
                    Starter = table.Column<bool>(type: "INTEGER", nullable: false),
                    Provisional = table.Column<string>(type: "TEXT", nullable: false),
                    ProvisionalAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    ProvisionalMonths = table.Column<int>(type: "INTEGER", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    Percentage = table.Column<decimal>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxReserveYears", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VatFilings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Quarter = table.Column<int>(type: "INTEGER", nullable: false),
                    FiledAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FiledBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VatFilings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaxReserveYears_WorkspaceId",
                table: "TaxReserveYears",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxReserveYears_WorkspaceId_Year",
                table: "TaxReserveYears",
                columns: new[] { "WorkspaceId", "Year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VatFilings_WorkspaceId",
                table: "VatFilings",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_VatFilings_WorkspaceId_Year_Quarter",
                table: "VatFilings",
                columns: new[] { "WorkspaceId", "Year", "Quarter" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaxReserveYears");

            migrationBuilder.DropTable(
                name: "VatFilings");
        }
    }
}
