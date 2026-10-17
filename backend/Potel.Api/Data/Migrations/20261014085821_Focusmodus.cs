using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Focusmodus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FocusMinutes",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FocusBlocks",
                table: "TimeEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FocusBlockStart",
                table: "RunningTimers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FocusBlocks",
                table: "RunningTimers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FocusBreakMinutes",
                table: "RunningTimers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "FocusBreakStartedAt",
                table: "RunningTimers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FocusMinutes",
                table: "RunningTimers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FocusRound",
                table: "RunningTimers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FocusMinutes",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FocusBlocks",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "FocusBlockStart",
                table: "RunningTimers");

            migrationBuilder.DropColumn(
                name: "FocusBlocks",
                table: "RunningTimers");

            migrationBuilder.DropColumn(
                name: "FocusBreakMinutes",
                table: "RunningTimers");

            migrationBuilder.DropColumn(
                name: "FocusBreakStartedAt",
                table: "RunningTimers");

            migrationBuilder.DropColumn(
                name: "FocusMinutes",
                table: "RunningTimers");

            migrationBuilder.DropColumn(
                name: "FocusRound",
                table: "RunningTimers");
        }
    }
}
