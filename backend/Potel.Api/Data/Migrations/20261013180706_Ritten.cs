using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Ritten : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "KilometreRate",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "KilometreRate",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TripRoutes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    From = table.Column<string>(type: "TEXT", nullable: false),
                    To = table.Column<string>(type: "TEXT", nullable: false),
                    Kilometres = table.Column<decimal>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    Billable = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TripRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TripRoutes_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TripRoutes_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Trips",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    From = table.Column<string>(type: "TEXT", nullable: false),
                    To = table.Column<string>(type: "TEXT", nullable: false),
                    Kilometres = table.Column<decimal>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    OdometerStart = table.Column<int>(type: "INTEGER", nullable: true),
                    OdometerEnd = table.Column<int>(type: "INTEGER", nullable: true),
                    Billable = table.Column<bool>(type: "INTEGER", nullable: false),
                    InvoiceId = table.Column<int>(type: "INTEGER", nullable: true),
                    InvoiceLineId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Trips_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Trips_InvoiceLines_InvoiceLineId",
                        column: x => x.InvoiceLineId,
                        principalTable: "InvoiceLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Trips_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Trips_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TripRoutes_CustomerId",
                table: "TripRoutes",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_TripRoutes_ProjectId",
                table: "TripRoutes",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_TripRoutes_WorkspaceId",
                table: "TripRoutes",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_CustomerId",
                table: "Trips",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_InvoiceId",
                table: "Trips",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_InvoiceLineId",
                table: "Trips",
                column: "InvoiceLineId");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_ProjectId",
                table: "Trips",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_WorkspaceId",
                table: "Trips",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_WorkspaceId_Date",
                table: "Trips",
                columns: new[] { "WorkspaceId", "Date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TripRoutes");

            migrationBuilder.DropTable(
                name: "Trips");

            migrationBuilder.DropColumn(
                name: "KilometreRate",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "KilometreRate",
                table: "Customers");
        }
    }
}
