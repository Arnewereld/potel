using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FacturenMailenEnHerinneringen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RemindersOff",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RemindersOff",
                table: "Customers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "InvoiceLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    InvoiceId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastOpenedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Opens = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceLinks_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceMails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    InvoiceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Step = table.Column<int>(type: "INTEGER", nullable: false),
                    To = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    Automatic = table.Column<bool>(type: "INTEGER", nullable: false),
                    SentBy = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceMails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceMails_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReminderSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstDays = table.Column<int>(type: "INTEGER", nullable: false),
                    SecondDays = table.Column<int>(type: "INTEGER", nullable: false),
                    FinalDays = table.Column<int>(type: "INTEGER", nullable: false),
                    InvoiceSubject = table.Column<string>(type: "TEXT", nullable: true),
                    InvoiceBody = table.Column<string>(type: "TEXT", nullable: true),
                    FirstSubject = table.Column<string>(type: "TEXT", nullable: true),
                    FirstBody = table.Column<string>(type: "TEXT", nullable: true),
                    SecondSubject = table.Column<string>(type: "TEXT", nullable: true),
                    SecondBody = table.Column<string>(type: "TEXT", nullable: true),
                    FinalSubject = table.Column<string>(type: "TEXT", nullable: true),
                    FinalBody = table.Column<string>(type: "TEXT", nullable: true),
                    LastNotice = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLinks_InvoiceId",
                table: "InvoiceLinks",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLinks_TokenHash",
                table: "InvoiceLinks",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLinks_WorkspaceId",
                table: "InvoiceLinks",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceMails_InvoiceId",
                table: "InvoiceMails",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceMails_OneAutomaticPerStep",
                table: "InvoiceMails",
                columns: new[] { "InvoiceId", "Step" },
                unique: true,
                filter: "\"Automatic\" = 1 AND \"Status\" IN ('bezig', 'verstuurd')");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceMails_WorkspaceId",
                table: "InvoiceMails",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_ReminderSettings_WorkspaceId",
                table: "ReminderSettings",
                column: "WorkspaceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceLinks");

            migrationBuilder.DropTable(
                name: "InvoiceMails");

            migrationBuilder.DropTable(
                name: "ReminderSettings");

            migrationBuilder.DropColumn(
                name: "RemindersOff",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RemindersOff",
                table: "Customers");
        }
    }
}
