using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WerkstroomGrenzen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EmailDay",
                table: "Workspaces",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmailsSent",
                table: "Workspaces",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Calls",
                table: "WorkflowRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Steps",
                table: "WorkflowRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Waits",
                table: "WorkflowRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailDay",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "EmailsSent",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "Calls",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "Steps",
                table: "WorkflowRuns");

            migrationBuilder.DropColumn(
                name: "Waits",
                table: "WorkflowRuns");
        }
    }
}
