using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountsEnRechten : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Wie platformbeheerder is, staat voortaan in de database (zie Seed.PlatformAdmins).
            migrationBuilder.AddColumn<bool>(
                name: "IsPlatformAdmin",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Bij een bestaande installatie is de eerste beheerder de eigenaar die bij de eerste start is aangemaakt.
            // Die houdt de pagina Platform; op een nieuwe database is de tabel nog leeg en doet dit niets.
            migrationBuilder.Sql(
                "UPDATE Users SET IsPlatformAdmin = 1 WHERE Id = " +
                "(SELECT MIN(Id) FROM Users WHERE Role = 'beheerder' AND Active = 1)");

            // Bestaande gebruikers krijgen een lege stempel, net als hun huidige cookies: ze blijven ingelogd
            // tot hun wachtwoord, e-mailadres, rol of status verandert.
            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "TimeEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TimeEntries_WorkspaceId_ClientId",
                table: "TimeEntries",
                columns: new[] { "WorkspaceId", "ClientId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TimeEntries_WorkspaceId_ClientId",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "IsPlatformAdmin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "TimeEntries");
        }
    }
}
