using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anvilboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPluginConfigAndState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PluginConfigs",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PluginKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ConfigKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    IsSecret = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginConfigs", x => new { x.WorkspaceId, x.PluginKey, x.ConfigKey });
                });

            migrationBuilder.CreateTable(
                name: "PluginStates",
                columns: table => new
                {
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PluginKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    StateKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PluginStates", x => new { x.WorkspaceId, x.PluginKey, x.StateKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_PluginConfigs_WorkspaceId",
                table: "PluginConfigs",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_PluginStates_WorkspaceId",
                table: "PluginStates",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PluginConfigs");

            migrationBuilder.DropTable(
                name: "PluginStates");
        }
    }
}
