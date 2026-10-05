using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Werkruimtes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_Number",
                table: "Invoices");

            migrationBuilder.AddColumn<int>(
                name: "WorkspaceId",
                table: "Workflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WorkspaceId",
                table: "WorkflowRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WorkspaceId",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WorkspaceId",
                table: "TimeEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "BrandColor",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LogoDataUrl",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkspaceId",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WorkspaceId",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WorkspaceId",
                table: "Leads",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WorkspaceId",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WorkspaceId",
                table: "CustomRecords",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WorkspaceId",
                table: "CustomModules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WorkspaceId",
                table: "Customers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WorkspaceId",
                table: "Appointments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WorkspaceId",
                table: "Activities",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Plan = table.Column<string>(type: "TEXT", nullable: false),
                    TrialEndsAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OnboardedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_WorkspaceId",
                table: "Workflows",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_WorkspaceId",
                table: "WorkflowRuns",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_WorkspaceId",
                table: "Users",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_WorkspaceId",
                table: "TimeEntries",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Settings_WorkspaceId",
                table: "Settings",
                column: "WorkspaceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_WorkspaceId",
                table: "Projects",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Leads_WorkspaceId",
                table: "Leads",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_WorkspaceId",
                table: "Invoices",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_WorkspaceId_Number",
                table: "Invoices",
                columns: new[] { "WorkspaceId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomRecords_WorkspaceId",
                table: "CustomRecords",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomModules_WorkspaceId",
                table: "CustomModules",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_WorkspaceId",
                table: "Customers",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_WorkspaceId",
                table: "Appointments",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_WorkspaceId",
                table: "Activities",
                column: "WorkspaceId");

            // Bestaande gegevens van voor de werkruimtes komen samen in werkruimte 1.
            migrationBuilder.Sql("""
                INSERT INTO Workspaces (Id, Name, Plan, OnboardedAt, CreatedAt)
                SELECT 1, COALESCE((SELECT CompanyName FROM Settings LIMIT 1), 'Mijn bedrijf'), 'team', datetime('now'), datetime('now')
                WHERE EXISTS (SELECT 1 FROM Users);
                """);
            migrationBuilder.Sql("UPDATE Customers SET WorkspaceId = 1 WHERE WorkspaceId = 0 AND EXISTS (SELECT 1 FROM Workspaces WHERE Id = 1);");
            migrationBuilder.Sql("UPDATE Leads SET WorkspaceId = 1 WHERE WorkspaceId = 0 AND EXISTS (SELECT 1 FROM Workspaces WHERE Id = 1);");
            migrationBuilder.Sql("UPDATE Invoices SET WorkspaceId = 1 WHERE WorkspaceId = 0 AND EXISTS (SELECT 1 FROM Workspaces WHERE Id = 1);");
            migrationBuilder.Sql("UPDATE Appointments SET WorkspaceId = 1 WHERE WorkspaceId = 0 AND EXISTS (SELECT 1 FROM Workspaces WHERE Id = 1);");
            migrationBuilder.Sql("UPDATE Projects SET WorkspaceId = 1 WHERE WorkspaceId = 0 AND EXISTS (SELECT 1 FROM Workspaces WHERE Id = 1);");
            migrationBuilder.Sql("UPDATE TimeEntries SET WorkspaceId = 1 WHERE WorkspaceId = 0 AND EXISTS (SELECT 1 FROM Workspaces WHERE Id = 1);");
            migrationBuilder.Sql("UPDATE Settings SET WorkspaceId = 1 WHERE WorkspaceId = 0 AND EXISTS (SELECT 1 FROM Workspaces WHERE Id = 1);");
            migrationBuilder.Sql("UPDATE CustomModules SET WorkspaceId = 1 WHERE WorkspaceId = 0 AND EXISTS (SELECT 1 FROM Workspaces WHERE Id = 1);");
            migrationBuilder.Sql("UPDATE CustomRecords SET WorkspaceId = 1 WHERE WorkspaceId = 0 AND EXISTS (SELECT 1 FROM Workspaces WHERE Id = 1);");
            migrationBuilder.Sql("UPDATE Workflows SET WorkspaceId = 1 WHERE WorkspaceId = 0 AND EXISTS (SELECT 1 FROM Workspaces WHERE Id = 1);");
            migrationBuilder.Sql("UPDATE Activities SET WorkspaceId = 1 WHERE WorkspaceId = 0 AND EXISTS (SELECT 1 FROM Workspaces WHERE Id = 1);");
            migrationBuilder.Sql("UPDATE Users SET WorkspaceId = 1 WHERE WorkspaceId = 0 AND EXISTS (SELECT 1 FROM Workspaces WHERE Id = 1);");
            migrationBuilder.Sql("UPDATE WorkflowRuns SET WorkspaceId = 1 WHERE WorkspaceId = 0 AND EXISTS (SELECT 1 FROM Workspaces WHERE Id = 1);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Workspaces");

            migrationBuilder.DropIndex(
                name: "IX_Workflows_WorkspaceId",
                table: "Workflows");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_WorkspaceId",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_Users_WorkspaceId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_TimeEntries_WorkspaceId",
                table: "TimeEntries");

            migrationBuilder.DropIndex(
                name: "IX_Settings_WorkspaceId",
                table: "Settings");

            migrationBuilder.DropIndex(
                name: "IX_Projects_WorkspaceId",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Leads_WorkspaceId",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_WorkspaceId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_WorkspaceId_Number",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_CustomRecords_WorkspaceId",
                table: "CustomRecords");

            migrationBuilder.DropIndex(
                name: "IX_CustomModules_WorkspaceId",
                table: "CustomModules");

            migrationBuilder.DropIndex(
                name: "IX_Customers_WorkspaceId",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_WorkspaceId",
                table: "Appointments");

            migrationBuilder.DropIndex(
                name: "IX_Activities_WorkspaceId",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "BrandColor",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "LogoDataUrl",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "CustomRecords");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "CustomModules");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Activities");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Number",
                table: "Invoices",
                column: "Number",
                unique: true);
        }
    }
}
