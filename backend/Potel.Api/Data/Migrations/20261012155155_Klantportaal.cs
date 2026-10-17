using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Klantportaal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PortalLinkId",
                table: "InvoiceLinks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustomerPortals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    ShowHours = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowBudget = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowInvoices = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowAmounts = table.Column<bool>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPortals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerPortals_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PortalLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastOpenedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Opens = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortalLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortalLinks_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLinks_PortalLinkId",
                table: "InvoiceLinks",
                column: "PortalLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPortals_CustomerId",
                table: "CustomerPortals",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPortals_WorkspaceId",
                table: "CustomerPortals",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_PortalLinks_CustomerId",
                table: "PortalLinks",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_PortalLinks_TokenHash",
                table: "PortalLinks",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortalLinks_WorkspaceId",
                table: "PortalLinks",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerPortals");

            migrationBuilder.DropTable(
                name: "PortalLinks");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceLinks_PortalLinkId",
                table: "InvoiceLinks");

            migrationBuilder.DropColumn(
                name: "PortalLinkId",
                table: "InvoiceLinks");
        }
    }
}
