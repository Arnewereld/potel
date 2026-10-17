using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Potel.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GitHubKoppeling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GitHubAuthors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Emails = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubAuthors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitHubAuthors_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GitHubCommits",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Sha = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeEntryId = table.Column<int>(type: "INTEGER", nullable: true),
                    Skipped = table.Column<bool>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubCommits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GitHubCommits_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GitHubCommits_TimeEntries_TimeEntryId",
                        column: x => x.TimeEntryId,
                        principalTable: "TimeEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "GitHubSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkspaceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProtectedToken = table.Column<string>(type: "TEXT", nullable: false),
                    TokenLast4 = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GitHubSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GitHubAuthors_UserId",
                table: "GitHubAuthors",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitHubAuthors_WorkspaceId",
                table: "GitHubAuthors",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_GitHubCommits_ProjectId",
                table: "GitHubCommits",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_GitHubCommits_TimeEntryId",
                table: "GitHubCommits",
                column: "TimeEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_GitHubCommits_WorkspaceId",
                table: "GitHubCommits",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_GitHubCommits_WorkspaceId_Sha",
                table: "GitHubCommits",
                columns: new[] { "WorkspaceId", "Sha" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GitHubSettings_WorkspaceId",
                table: "GitHubSettings",
                column: "WorkspaceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GitHubAuthors");

            migrationBuilder.DropTable(
                name: "GitHubCommits");

            migrationBuilder.DropTable(
                name: "GitHubSettings");
        }
    }
}
