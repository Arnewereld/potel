using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Bankafschriften : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Iban",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BankCsvMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    DateColumn = table.Column<string>(type: "TEXT", nullable: false),
                    AmountColumn = table.Column<string>(type: "TEXT", nullable: false),
                    DescriptionColumn = table.Column<string>(type: "TEXT", nullable: true),
                    NameColumn = table.Column<string>(type: "TEXT", nullable: true),
                    IbanColumn = table.Column<string>(type: "TEXT", nullable: true),
                    SignColumn = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankCsvMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BankImports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    Format = table.Column<string>(type: "TEXT", nullable: false),
                    Added = table.Column<int>(type: "INTEGER", nullable: false),
                    Skipped = table.Column<int>(type: "INTEGER", nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ImportedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankImports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BankTransactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ImportId = table.Column<int>(type: "INTEGER", nullable: false),
                    Account = table.Column<string>(type: "TEXT", nullable: true),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    CounterpartyName = table.Column<string>(type: "TEXT", nullable: true),
                    CounterpartyIban = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    BankReference = table.Column<string>(type: "TEXT", nullable: true),
                    DedupeKey = table.Column<string>(type: "TEXT", nullable: false),
                    Mark = table.Column<string>(type: "TEXT", nullable: true),
                    ExpenseId = table.Column<int>(type: "INTEGER", nullable: true),
                    PaymentCreated = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankTransactions_BankImports_ImportId",
                        column: x => x.ImportId,
                        principalTable: "BankImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BankTransactions_Expenses_ExpenseId",
                        column: x => x.ExpenseId,
                        principalTable: "Expenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankCsvMappings_WorkspaceId",
                table: "BankCsvMappings",
                column: "WorkspaceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankImports_WorkspaceId",
                table: "BankImports",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_ExpenseId",
                table: "BankTransactions",
                column: "ExpenseId",
                unique: true,
                filter: "\"ExpenseId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_ImportId",
                table: "BankTransactions",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_WorkspaceId",
                table: "BankTransactions",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_WorkspaceId_Date",
                table: "BankTransactions",
                columns: new[] { "WorkspaceId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_WorkspaceId_DedupeKey",
                table: "BankTransactions",
                columns: new[] { "WorkspaceId", "DedupeKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankCsvMappings");

            migrationBuilder.DropTable(
                name: "BankTransactions");

            migrationBuilder.DropTable(
                name: "BankImports");

            migrationBuilder.DropColumn(
                name: "Iban",
                table: "Customers");
        }
    }
}
