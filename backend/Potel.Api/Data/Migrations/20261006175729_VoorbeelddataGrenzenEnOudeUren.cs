using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class VoorbeelddataGrenzenEnOudeUren : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DemoDataAt",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WebhookDay",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WebhooksSent",
                table: "Workspaces",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "Workflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "TimeEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "Leads",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "InvoiceLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "CustomRecords",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "CustomModules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "Customers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "Appointments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PlatformCounters",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Day = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformCounters", x => x.Key);
                });

            migrationBuilder.InsertData(
                table: "PlatformCounters",
                columns: new[] { "Key", "Count", "Day" },
                values: new object[] { "proef-emails", 0, null });

            // Uren van oudere facturen aan hun eigen regel koppelen, ook als er meer urenregels op staan (de vorige migratie deed
            // dat alleen bij één urenregel). Dan geeft het weghalen van één regel uit een oud concept precies die uren weer vrij.
            // De oude gedetailleerde vorm had per boeking een regel "dd-MM-yyyy omschrijving"; bij dezelfde tekst gaat de eerste
            // boeking naar de eerste regel, de tweede naar de tweede, enzovoort.
            migrationBuilder.Sql("""
                WITH e AS (
                    SELECT t.Id AS EntryId, t.InvoiceId AS InvoiceId,
                           strftime('%d-%m-%Y', t.Date) || ' ' || CASE WHEN TRIM(t.Description) = '' THEN p.Name ELSE t.Description END AS Text
                    FROM TimeEntries t JOIN Projects p ON p.Id = t.ProjectId
                    WHERE t.InvoiceId IS NOT NULL AND t.InvoiceLineId IS NULL),
                en AS (SELECT EntryId, InvoiceId, Text, ROW_NUMBER() OVER (PARTITION BY InvoiceId, Text ORDER BY EntryId) AS Nr FROM e),
                ln AS (
                    SELECT l.Id AS LineId, l.InvoiceId AS InvoiceId, l.Description AS Text,
                           ROW_NUMBER() OVER (PARTITION BY l.InvoiceId, l.Description ORDER BY l.Id) AS Nr
                    FROM InvoiceLines l
                    WHERE l.Unit = 'uur' AND NOT EXISTS (SELECT 1 FROM TimeEntries x WHERE x.InvoiceLineId = l.Id)),
                pairs AS (SELECT en.EntryId, ln.LineId FROM en JOIN ln ON ln.InvoiceId = en.InvoiceId AND ln.Text = en.Text AND ln.Nr = en.Nr)
                UPDATE TimeEntries SET InvoiceLineId = (SELECT pairs.LineId FROM pairs WHERE pairs.EntryId = TimeEntries.Id)
                WHERE Id IN (SELECT EntryId FROM pairs);
                """);

            // De oude totaalregel per project ("Project: werkzaamheden 01-10 t/m 31-10-2026"), ook op een factuur met meer projecten.
            migrationBuilder.Sql("""
                UPDATE TimeEntries SET InvoiceLineId = (
                    SELECT l.Id FROM InvoiceLines l, Projects p
                    WHERE p.Id = TimeEntries.ProjectId AND l.InvoiceId = TimeEntries.InvoiceId AND l.Unit = 'uur'
                      AND substr(l.Description, 1, length(p.Name) + 16) = p.Name || ': werkzaamheden ')
                WHERE InvoiceId IS NOT NULL AND InvoiceLineId IS NULL
                  AND (SELECT COUNT(*) FROM InvoiceLines l, Projects p
                       WHERE p.Id = TimeEntries.ProjectId AND l.InvoiceId = TimeEntries.InvoiceId AND l.Unit = 'uur'
                         AND substr(l.Description, 1, length(p.Name) + 16) = p.Name || ': werkzaamheden ') = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformCounters");

            migrationBuilder.DropColumn(
                name: "DemoDataAt",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "WebhookDay",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "WebhooksSent",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "CustomRecords");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "CustomModules");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "Appointments");
        }
    }
}
