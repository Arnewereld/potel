using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Urenspecificatie : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "TimeEntries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HourAmounts",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HourSpecification",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "HoursSnapshot",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            // Verstuurde facturen zijn zonder urenspecificatie verstuurd en blijven zo. Concepten krijgen hem standaard mee.
            migrationBuilder.Sql("UPDATE \"Invoices\" SET \"HourSpecification\" = 1 WHERE \"Status\" = 'concept';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserId",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "HourAmounts",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "HourSpecification",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "HoursSnapshot",
                table: "Invoices");
        }
    }
}
