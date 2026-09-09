using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anvilboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotencyRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ResultPayload = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_WorkspaceId_ActorId_Operation_Key",
                table: "IdempotencyRecords",
                columns: new[] { "WorkspaceId", "ActorId", "Operation", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdempotencyRecords");
        }
    }
}
