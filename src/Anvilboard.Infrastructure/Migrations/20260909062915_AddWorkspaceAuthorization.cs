using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anvilboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "Members",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "Members",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "Members",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApiTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MemberId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    GrantedPermissions = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Members_WorkspaceId_Username",
                table: "Members",
                columns: new[] { "WorkspaceId", "Username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_MemberId",
                table: "ApiTokens",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_TokenHash",
                table: "ApiTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_WorkspaceId",
                table: "ApiTokens",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiTokens");

            migrationBuilder.DropIndex(
                name: "IX_Members_WorkspaceId_Username",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "Username",
                table: "Members");
        }
    }
}
