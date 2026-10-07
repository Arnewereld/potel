using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OnlineBetalenMetMollie : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BillingCustomerId",
                table: "Workspaces",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MollieCustomerId",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MollieSubscriptionId",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaidUntil",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SubscriptionCanceledAt",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MolliePayments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Plan = table.Column<string>(type: "TEXT", nullable: false),
                    SequenceType = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    VatRate = table.Column<decimal>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PeriodStart = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PeriodEnd = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    InvoiceId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MolliePayments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_MollieCustomerId",
                table: "Workspaces",
                column: "MollieCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_MolliePayments_WorkspaceId",
                table: "MolliePayments",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MolliePayments");

            migrationBuilder.DropIndex(
                name: "IX_Workspaces_MollieCustomerId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "BillingCustomerId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "MollieCustomerId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "MollieSubscriptionId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "PaidUntil",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "SubscriptionCanceledAt",
                table: "Workspaces");
        }
    }
}
