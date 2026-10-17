using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ZakelijkAanmeldenEnVoorwaarden : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kvk",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TermsAcceptedAt",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TermsAcceptedByEmail",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TermsAcceptedByUserId",
                table: "Workspaces",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TermsVersion",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            // Bestaande werkruimtes nemen het KvK-nummer over uit hun bedrijfsgegevens. Ze hebben nog geen voorwaarden geaccepteerd.
            migrationBuilder.Sql("UPDATE Workspaces SET Kvk = (SELECT s.Kvk FROM Settings s WHERE s.WorkspaceId = Workspaces.Id AND s.Kvk IS NOT NULL AND s.Kvk <> '') WHERE Kvk IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kvk",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "TermsAcceptedAt",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "TermsAcceptedByEmail",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "TermsAcceptedByUserId",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "TermsVersion",
                table: "Workspaces");
        }
    }
}
