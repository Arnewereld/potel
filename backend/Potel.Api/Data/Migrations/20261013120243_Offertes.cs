using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Offertes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QuoteValidDays",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.CreateTable(
                name: "Quotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDemo = table.Column<bool>(type: "INTEGER", nullable: false),
                    Number = table.Column<string>(type: "TEXT", nullable: true),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: true),
                    LeadId = table.Column<int>(type: "INTEGER", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    PreviousVersionId = table.Column<int>(type: "INTEGER", nullable: true),
                    IssueDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    VatRegime = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: true),
                    Intro = table.Column<string>(type: "TEXT", nullable: true),
                    Terms = table.Column<string>(type: "TEXT", nullable: true),
                    DepositPercent = table.Column<decimal>(type: "TEXT", nullable: true),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecidedName = table.Column<string>(type: "TEXT", nullable: true),
                    DecidedIp = table.Column<string>(type: "TEXT", nullable: true),
                    RejectReason = table.Column<string>(type: "TEXT", nullable: true),
                    WithdrawnAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MailedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MailedTo = table.Column<string>(type: "TEXT", nullable: true),
                    Seller_Name = table.Column<string>(type: "TEXT", nullable: true),
                    Seller_Contact = table.Column<string>(type: "TEXT", nullable: true),
                    Seller_Address = table.Column<string>(type: "TEXT", nullable: true),
                    Seller_City = table.Column<string>(type: "TEXT", nullable: true),
                    Seller_Country = table.Column<string>(type: "TEXT", nullable: true),
                    Seller_Email = table.Column<string>(type: "TEXT", nullable: true),
                    Seller_Phone = table.Column<string>(type: "TEXT", nullable: true),
                    Seller_Website = table.Column<string>(type: "TEXT", nullable: true),
                    Seller_Kvk = table.Column<string>(type: "TEXT", nullable: true),
                    Seller_VatNumber = table.Column<string>(type: "TEXT", nullable: true),
                    Seller_Iban = table.Column<string>(type: "TEXT", nullable: true),
                    Buyer_Name = table.Column<string>(type: "TEXT", nullable: true),
                    Buyer_Contact = table.Column<string>(type: "TEXT", nullable: true),
                    Buyer_Address = table.Column<string>(type: "TEXT", nullable: true),
                    Buyer_City = table.Column<string>(type: "TEXT", nullable: true),
                    Buyer_Country = table.Column<string>(type: "TEXT", nullable: true),
                    Buyer_Email = table.Column<string>(type: "TEXT", nullable: true),
                    Buyer_Phone = table.Column<string>(type: "TEXT", nullable: true),
                    Buyer_Website = table.Column<string>(type: "TEXT", nullable: true),
                    Buyer_Kvk = table.Column<string>(type: "TEXT", nullable: true),
                    Buyer_VatNumber = table.Column<string>(type: "TEXT", nullable: true),
                    Buyer_Iban = table.Column<string>(type: "TEXT", nullable: true),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true),
                    DepositInvoiceId = table.Column<int>(type: "INTEGER", nullable: true),
                    InvoiceId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Quotes_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Quotes_Invoices_DepositInvoiceId",
                        column: x => x.DepositInvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Quotes_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Quotes_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Quotes_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "QuoteLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    QuoteId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    Unit = table.Column<string>(type: "TEXT", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    VatRate = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuoteLines_Quotes_QuoteId",
                        column: x => x.QuoteId,
                        principalTable: "Quotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuoteLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    QuoteId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastOpenedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Opens = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuoteLinks_Quotes_QuoteId",
                        column: x => x.QuoteId,
                        principalTable: "Quotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteLines_QuoteId",
                table: "QuoteLines",
                column: "QuoteId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteLinks_QuoteId",
                table: "QuoteLinks",
                column: "QuoteId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteLinks_TokenHash",
                table: "QuoteLinks",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuoteLinks_WorkspaceId",
                table: "QuoteLinks",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_CustomerId",
                table: "Quotes",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_DepositInvoiceId",
                table: "Quotes",
                column: "DepositInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_InvoiceId",
                table: "Quotes",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_LeadId",
                table: "Quotes",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_ProjectId",
                table: "Quotes",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_WorkspaceId",
                table: "Quotes",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_WorkspaceId_Number",
                table: "Quotes",
                columns: new[] { "WorkspaceId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuoteLines");

            migrationBuilder.DropTable(
                name: "QuoteLinks");

            migrationBuilder.DropTable(
                name: "Quotes");

            migrationBuilder.DropColumn(
                name: "QuoteValidDays",
                table: "Settings");
        }
    }
}
