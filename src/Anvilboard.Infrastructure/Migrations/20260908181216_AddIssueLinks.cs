using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anvilboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IssueLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceIssueId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetIssueId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedById = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueLinks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IssueLinks_SourceIssueId",
                table: "IssueLinks",
                column: "SourceIssueId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueLinks_SourceIssueId_TargetIssueId_Type",
                table: "IssueLinks",
                columns: new[] { "SourceIssueId", "TargetIssueId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueLinks_TargetIssueId",
                table: "IssueLinks",
                column: "TargetIssueId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IssueLinks");
        }
    }
}
