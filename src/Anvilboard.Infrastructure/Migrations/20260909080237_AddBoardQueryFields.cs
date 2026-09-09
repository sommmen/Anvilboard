using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anvilboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBoardQueryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                table: "Issues",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Issues",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Issues");
        }
    }
}
