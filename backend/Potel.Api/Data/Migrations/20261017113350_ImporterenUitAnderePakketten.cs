using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImporterenUitAnderePakketten : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ImportBatchId",
                table: "TimeEntries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImportBatchId",
                table: "Projects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImportBatchId",
                table: "Customers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kvk",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ImportBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: true),
                    Created = table.Column<int>(type: "INTEGER", nullable: false),
                    Updated = table.Column<int>(type: "INTEGER", nullable: false),
                    Skipped = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomersCreated = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectsCreated = table.Column<int>(type: "INTEGER", nullable: false),
                    ImportedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UndoneAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UndoneBy = table.Column<string>(type: "TEXT", nullable: true),
                    Kept = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImportedInvoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ImportBatchId = table.Column<int>(type: "INTEGER", nullable: false),
                    Number = table.Column<string>(type: "TEXT", nullable: false),
                    IssueDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Subtotal = table.Column<decimal>(type: "TEXT", nullable: false),
                    Vat = table.Column<decimal>(type: "TEXT", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedInvoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportedInvoices_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_ImportBatchId",
                table: "TimeEntries",
                column: "ImportBatchId",
                filter: "\"ImportBatchId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_ImportBatchId",
                table: "Projects",
                column: "ImportBatchId",
                filter: "\"ImportBatchId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_ImportBatchId",
                table: "Customers",
                column: "ImportBatchId",
                filter: "\"ImportBatchId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatches_WorkspaceId",
                table: "ImportBatches",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedInvoices_CustomerId",
                table: "ImportedInvoices",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedInvoices_ImportBatchId",
                table: "ImportedInvoices",
                column: "ImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedInvoices_WorkspaceId",
                table: "ImportedInvoices",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedInvoices_WorkspaceId_Number",
                table: "ImportedInvoices",
                columns: new[] { "WorkspaceId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportBatches");

            migrationBuilder.DropTable(
                name: "ImportedInvoices");

            migrationBuilder.DropIndex(
                name: "IX_TimeEntries_ImportBatchId",
                table: "TimeEntries");

            migrationBuilder.DropIndex(
                name: "IX_Projects_ImportBatchId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Customers_ImportBatchId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ImportBatchId",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "ImportBatchId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ImportBatchId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Kvk",
                table: "Customers");
        }
    }
}
