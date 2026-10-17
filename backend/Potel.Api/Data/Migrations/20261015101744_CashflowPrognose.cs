using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CashflowPrognose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CashflowSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    BankBalance = table.Column<decimal>(type: "TEXT", nullable: true),
                    BankBalanceDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FixedCostsPerMonth = table.Column<decimal>(type: "TEXT", nullable: false),
                    QuoteSpreadMonths = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashflowSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CashflowSettings_WorkspaceId",
                table: "CashflowSettings",
                column: "WorkspaceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CashflowSettings");
        }
    }
}
