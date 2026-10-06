using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class FacturenVolgensDeRegels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InvoiceLineId",
                table: "TimeEntries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VatRegime",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "normaal");

            migrationBuilder.AddColumn<string>(
                name: "Buyer_Address",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Buyer_City",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Buyer_Contact",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Buyer_Country",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Buyer_Email",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Buyer_Iban",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Buyer_Kvk",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Buyer_Name",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Buyer_Phone",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Buyer_VatNumber",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Buyer_Website",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreditForInvoiceId",
                table: "Invoices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveryFrom",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveryTo",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seller_Address",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seller_City",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seller_Contact",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seller_Country",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seller_Email",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seller_Iban",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seller_Kvk",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seller_Name",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seller_Phone",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seller_VatNumber",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Seller_Website",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SentAt",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VatRegime",
                table: "Invoices",
                type: "TEXT",
                nullable: false,
                defaultValue: "normaal");

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "Customers",
                type: "TEXT",
                nullable: false,
                defaultValue: "Nederland");

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_InvoiceLineId",
                table: "TimeEntries",
                column: "InvoiceLineId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_CreditForInvoiceId",
                table: "Invoices",
                column: "CreditForInvoiceId");

            // Bestaande gegevens geldig houden. Eerst de nieuwe kolommen vullen; oude kolommen gaan pas aan het eind weg
            // (SQLite bouwt de tabel dan opnieuw op, als laatste stap van deze migratie).

            // Een concept krijgt pas bij versturen een nummer. Het nummer mag daarom leeg zijn; omdat SQLite dat alleen met een
            // nieuwe tabel kan, komt er een nieuwe kolom en nemen alleen verstuurde en betaalde facturen hun nummer mee.
            migrationBuilder.RenameColumn(
                name: "Number",
                table: "Invoices",
                newName: "LegacyNumber");

            migrationBuilder.AddColumn<string>(
                name: "Number",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("UPDATE Invoices SET Number = LegacyNumber WHERE Status <> 'concept';");

            migrationBuilder.Sql("UPDATE Invoices SET VatRegime = 'verlegd' WHERE ReverseCharge = 1;");

            // Verstuurde en betaalde facturen houden hun nummer en krijgen de gegevens van beide partijen zoals ze nu zijn
            // (zelfde opschoning als InvoiceParty.Seller en InvoiceParty.Buyer).
            migrationBuilder.Sql("""
                UPDATE Invoices SET
                    Seller_Name = COALESCE(
                        (SELECT NULLIF(TRIM(s.CompanyName), '') FROM Settings s WHERE s.WorkspaceId = Invoices.WorkspaceId ORDER BY s.Id LIMIT 1),
                        (SELECT w.Name FROM Workspaces w WHERE w.Id = Invoices.WorkspaceId)),
                    Seller_Contact = (SELECT NULLIF(TRIM(s.OwnerName), '') FROM Settings s WHERE s.WorkspaceId = Invoices.WorkspaceId ORDER BY s.Id LIMIT 1),
                    Seller_Address = (SELECT NULLIF(TRIM(s.Address), '') FROM Settings s WHERE s.WorkspaceId = Invoices.WorkspaceId ORDER BY s.Id LIMIT 1),
                    Seller_City = (SELECT NULLIF(TRIM(s.City), '') FROM Settings s WHERE s.WorkspaceId = Invoices.WorkspaceId ORDER BY s.Id LIMIT 1),
                    Seller_Country = 'Nederland',
                    Seller_Email = (SELECT NULLIF(TRIM(s.Email), '') FROM Settings s WHERE s.WorkspaceId = Invoices.WorkspaceId ORDER BY s.Id LIMIT 1),
                    Seller_Phone = (SELECT NULLIF(TRIM(s.Phone), '') FROM Settings s WHERE s.WorkspaceId = Invoices.WorkspaceId ORDER BY s.Id LIMIT 1),
                    Seller_Website = (SELECT NULLIF(TRIM(s.Website), '') FROM Settings s WHERE s.WorkspaceId = Invoices.WorkspaceId ORDER BY s.Id LIMIT 1),
                    Seller_Kvk = (SELECT NULLIF(TRIM(s.Kvk), '') FROM Settings s WHERE s.WorkspaceId = Invoices.WorkspaceId ORDER BY s.Id LIMIT 1),
                    Seller_VatNumber = (SELECT NULLIF(TRIM(s.Btw), '') FROM Settings s WHERE s.WorkspaceId = Invoices.WorkspaceId ORDER BY s.Id LIMIT 1),
                    Seller_Iban = (SELECT NULLIF(TRIM(s.Iban), '') FROM Settings s WHERE s.WorkspaceId = Invoices.WorkspaceId ORDER BY s.Id LIMIT 1),
                    Buyer_Name = (SELECT COALESCE(NULLIF(TRIM(c.Company), ''), TRIM(c.Name)) FROM Customers c WHERE c.Id = Invoices.CustomerId),
                    Buyer_Contact = (SELECT CASE WHEN NULLIF(TRIM(c.Company), '') IS NULL THEN NULL ELSE NULLIF(TRIM(c.Name), '') END FROM Customers c WHERE c.Id = Invoices.CustomerId),
                    Buyer_Address = (SELECT NULLIF(TRIM(c.Address), '') FROM Customers c WHERE c.Id = Invoices.CustomerId),
                    Buyer_City = (SELECT NULLIF(TRIM(c.City), '') FROM Customers c WHERE c.Id = Invoices.CustomerId),
                    Buyer_Country = (SELECT c.Country FROM Customers c WHERE c.Id = Invoices.CustomerId),
                    Buyer_Email = (SELECT NULLIF(TRIM(c.Email), '') FROM Customers c WHERE c.Id = Invoices.CustomerId),
                    Buyer_Phone = (SELECT NULLIF(TRIM(c.Phone), '') FROM Customers c WHERE c.Id = Invoices.CustomerId),
                    Buyer_VatNumber = (SELECT NULLIF(TRIM(c.VatNumber), '') FROM Customers c WHERE c.Id = Invoices.CustomerId),
                    SentAt = IssueDate
                WHERE Status <> 'concept';
                """);

            // Creditnota's werden tot nu toe alleen met een notitie aan hun factuur gekoppeld: "Creditnota voor factuur 2026-0012 van ...".
            migrationBuilder.Sql("""
                UPDATE Invoices SET CreditForInvoiceId = (
                    SELECT o.Id FROM Invoices o
                    WHERE o.WorkspaceId = Invoices.WorkspaceId AND o.Id <> Invoices.Id
                      AND substr(Invoices.Notes, 1, length('Creditnota voor factuur ' || o.Number || ' van ')) = 'Creditnota voor factuur ' || o.Number || ' van '
                    ORDER BY o.Id LIMIT 1)
                WHERE Notes LIKE 'Creditnota voor factuur %';
                """);

            // Leverdatum: de periode van de uren op de factuur, bij een creditnota die van de oorspronkelijke factuur, anders de factuurdatum.
            migrationBuilder.Sql("""
                UPDATE Invoices SET
                    DeliveryFrom = (SELECT MIN(t.Date) FROM TimeEntries t WHERE t.InvoiceId = Invoices.Id),
                    DeliveryTo = (SELECT MAX(t.Date) FROM TimeEntries t WHERE t.InvoiceId = Invoices.Id);
                UPDATE Invoices SET
                    DeliveryFrom = (SELECT o.DeliveryFrom FROM Invoices o WHERE o.Id = Invoices.CreditForInvoiceId),
                    DeliveryTo = (SELECT o.DeliveryTo FROM Invoices o WHERE o.Id = Invoices.CreditForInvoiceId)
                WHERE CreditForInvoiceId IS NOT NULL AND DeliveryFrom IS NULL;
                UPDATE Invoices SET DeliveryFrom = IssueDate WHERE DeliveryFrom IS NULL;
                """);

            // Uren aan hun factuurregel koppelen waar dat zeker is: één urenregel op de factuur en alle uren van één project.
            migrationBuilder.Sql("""
                UPDATE TimeEntries SET InvoiceLineId = (
                    SELECT l.Id FROM InvoiceLines l WHERE l.InvoiceId = TimeEntries.InvoiceId AND l.Unit = 'uur')
                WHERE InvoiceId IS NOT NULL
                  AND (SELECT COUNT(*) FROM InvoiceLines l WHERE l.InvoiceId = TimeEntries.InvoiceId AND l.Unit = 'uur') = 1
                  AND (SELECT COUNT(DISTINCT t.ProjectId) FROM TimeEntries t WHERE t.InvoiceId = TimeEntries.InvoiceId) = 1;
                """);

            migrationBuilder.DropColumn(
                name: "ReverseCharge",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "LegacyNumber",
                table: "Invoices");

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Invoices_CreditForInvoiceId",
                table: "Invoices",
                column: "CreditForInvoiceId",
                principalTable: "Invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TimeEntries_InvoiceLines_InvoiceLineId",
                table: "TimeEntries",
                column: "InvoiceLineId",
                principalTable: "InvoiceLines",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Terug naar de oude vorm: btw verlegd weer als vinkje en concepten weer met een (tijdelijk) nummer.
            migrationBuilder.AddColumn<bool>(
                name: "ReverseCharge",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE Invoices SET ReverseCharge = 1 WHERE VatRegime = 'verlegd';");

            migrationBuilder.Sql("UPDATE Invoices SET Number = 'concept-' || Id WHERE Number IS NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Invoices_CreditForInvoiceId",
                table: "Invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_TimeEntries_InvoiceLines_InvoiceLineId",
                table: "TimeEntries");

            migrationBuilder.DropIndex(
                name: "IX_TimeEntries_InvoiceLineId",
                table: "TimeEntries");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_CreditForInvoiceId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "InvoiceLineId",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "VatRegime",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "Buyer_Address",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Buyer_City",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Buyer_Contact",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Buyer_Country",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Buyer_Email",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Buyer_Iban",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Buyer_Kvk",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Buyer_Name",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Buyer_Phone",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Buyer_VatNumber",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Buyer_Website",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "CreditForInvoiceId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "DeliveryFrom",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "DeliveryTo",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Seller_Address",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Seller_City",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Seller_Contact",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Seller_Country",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Seller_Email",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Seller_Iban",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Seller_Kvk",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Seller_Name",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Seller_Phone",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Seller_VatNumber",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Seller_Website",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "SentAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "VatRegime",
                table: "Invoices");

            migrationBuilder.AlterColumn<string>(
                name: "Number",
                table: "Invoices",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
