using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Investeringen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DisposalPrice",
                table: "Expenses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DisposedOn",
                table: "Expenses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Investment",
                table: "Expenses",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PassengerCar",
                table: "Expenses",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ResidualValue",
                table: "Expenses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UsefulLifeYears",
                table: "Expenses",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisposalPrice",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "DisposedOn",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "Investment",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "PassengerCar",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "ResidualValue",
                table: "Expenses");

            migrationBuilder.DropColumn(
                name: "UsefulLifeYears",
                table: "Expenses");
        }
    }
}
