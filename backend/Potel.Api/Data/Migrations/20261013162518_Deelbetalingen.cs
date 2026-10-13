using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Deelbetalingen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InvoicePayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    InvoiceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    BankTransactionId = table.Column<int>(type: "INTEGER", nullable: true),
                    MolliePaymentId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoicePayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoicePayments_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InvoicePayments_BankTransactionId",
                table: "InvoicePayments",
                column: "BankTransactionId",
                unique: true,
                filter: "\"BankTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InvoicePayments_InvoiceId",
                table: "InvoicePayments",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoicePayments_MolliePaymentId",
                table: "InvoicePayments",
                column: "MolliePaymentId",
                unique: true,
                filter: "\"MolliePaymentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InvoicePayments_WorkspaceId",
                table: "InvoicePayments",
                column: "WorkspaceId");

            // Bestaande betaalde facturen krijgen één betaling voor hun volledige bedrag op hun betaaldatum, zodat wat er openstaat
            // en betaald is blijft kloppen. Het totaal rekenen we uit zoals Money.Totals: elk regelbedrag op centen, de btw per tarief
            // over het totaal van dat tarief, half van nul af. SQLite rekent hier met kommagetallen; het kleine duwtje (1e-9) zorgt dat
            // een bedrag dat precies op een halve cent valt naar boven afrondt, zoals decimal dat doet.
            migrationBuilder.Sql("""
                INSERT INTO InvoicePayments (WorkspaceId, InvoiceId, Amount, Date, Method, Note, CreatedAt)
                SELECT i.WorkspaceId, i.Id, t.Total, COALESCE(i.PaidAt, i.IssueDate), 'handmatig',
                       'Overgenomen van de betaalde factuur', strftime('%Y-%m-%d %H:%M:%f', 'now')
                FROM Invoices i
                JOIN (
                    SELECT g.InvoiceId,
                           ROUND(SUM(g.Base + ROUND(g.Base * g.Rate / 100.0 + CASE WHEN g.Base * g.Rate < 0 THEN -0.000000001 ELSE 0.000000001 END, 2)), 2) AS Total
                    FROM (
                        SELECT l.InvoiceId, CAST(l.VatRate AS REAL) AS Rate,
                               ROUND(SUM(ROUND(CAST(l.Quantity AS REAL) * CAST(l.UnitPrice AS REAL)
                                   + CASE WHEN CAST(l.Quantity AS REAL) * CAST(l.UnitPrice AS REAL) < 0 THEN -0.000000001 ELSE 0.000000001 END, 2)), 2) AS Base
                        FROM InvoiceLines l
                        GROUP BY l.InvoiceId, CAST(l.VatRate AS REAL)
                    ) g
                    GROUP BY g.InvoiceId
                ) t ON t.InvoiceId = i.Id
                WHERE i.Status = 'betaald' AND i.CreditForInvoiceId IS NULL AND t.Total > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoicePayments");
        }
    }
}
