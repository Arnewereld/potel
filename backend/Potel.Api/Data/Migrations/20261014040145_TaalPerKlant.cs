using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaalPerKlant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Quotes",
                type: "TEXT",
                nullable: false,
                defaultValue: "nl");

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Leads",
                type: "TEXT",
                nullable: false,
                defaultValue: "nl");

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Invoices",
                type: "TEXT",
                nullable: false,
                defaultValue: "nl");

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Customers",
                type: "TEXT",
                nullable: false,
                defaultValue: "nl");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Language",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "Customers");
        }
    }
}
