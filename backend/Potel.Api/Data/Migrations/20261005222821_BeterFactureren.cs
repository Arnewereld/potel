using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BeterFactureren : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PaidAt",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reference",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReverseCharge",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "InvoiceLines",
                type: "TEXT",
                nullable: false,
                defaultValue: "stuk");

            // Bestaande gegevens bijwerken: uurregels krijgen de eenheid "uur", betaalde facturen een betaaldatum.
            migrationBuilder.Sql("UPDATE InvoiceLines SET Unit = 'uur' WHERE Description LIKE '%werkzaamheden%' OR Description GLOB '[0-9][0-9]-[0-9][0-9]-[0-9][0-9][0-9][0-9] *';");
            migrationBuilder.Sql("UPDATE Invoices SET PaidAt = DueDate WHERE Status = 'betaald';");

            migrationBuilder.AddColumn<string>(
                name: "VatNumber",
                table: "Customers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaidAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Reference",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ReverseCharge",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "VatNumber",
                table: "Customers");
        }
    }
}
