using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class VasteKosten : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NeedsReview",
                table: "Expenses",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RecurringExpenseId",
                table: "Expenses",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecurringPeriodStart",
                table: "Expenses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RecurringExpenses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Supplier = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    VatRate = table.Column<decimal>(type: "TEXT", nullable: true),
                    ReverseCharge = table.Column<bool>(type: "INTEGER", nullable: false),
                    SupplierRegion = table.Column<string>(type: "TEXT", nullable: true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: true),
                    Interval = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    StartDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextPeriodStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextRunDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SkippedInARow = table.Column<int>(type: "INTEGER", nullable: false),
                    RemindedPeriodStart = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringExpenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringExpenses_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RecurringExpenses_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_RecurringExpenseId_RecurringPeriodStart",
                table: "Expenses",
                columns: new[] { "RecurringExpenseId", "RecurringPeriodStart" },
                unique: true,
                filter: "\"RecurringExpenseId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_CustomerId",
                table: "RecurringExpenses",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_ProjectId",
                table: "RecurringExpenses",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_WorkspaceId",
                table: "RecurringExpenses",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_WorkspaceId_NextRunDate",
                table: "RecurringExpenses",
                columns: new[] { "WorkspaceId", "NextRunDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecurringExpenses");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_RecurringExpenseId_RecurringPeriodStart",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "NeedsReview",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "RecurringExpenseId",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "RecurringPeriodStart",
                table: "Expenses");
        }
    }
}
