using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anvilboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    IsTerminal = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromStateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToStateId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowTransitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowStates_WorkspaceId_Key",
                table: "WorkflowStates",
                columns: new[] { "WorkspaceId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowTransitions_WorkspaceId_FromStateId_ToStateId",
                table: "WorkflowTransitions",
                columns: new[] { "WorkspaceId", "FromStateId", "ToStateId" },
                unique: true);

            // A workspace-owned default workflow lets existing boards transition without relying on
            // the retired enum ordering. IDs are generated during migration, then transitions join
            // back to the state keys so every workspace receives an isolated adjacency graph.
            migrationBuilder.Sql("""
                INSERT INTO WorkflowStates (Id, WorkspaceId, Key, DisplayName, "Order", IsTerminal, IsArchived)
                SELECT lower(hex(randomblob(16))), w.Id, state.Key, state.DisplayName, state.SortOrder, state.IsTerminal, 0
                FROM Workspaces AS w
                CROSS JOIN (
                    SELECT 'backlog' AS Key, 'Backlog' AS DisplayName, 0 AS SortOrder, 0 AS IsTerminal
                    UNION ALL SELECT 'todo', 'Todo', 1, 0
                    UNION ALL SELECT 'in_progress', 'In Progress', 2, 0
                    UNION ALL SELECT 'in_review', 'In Review', 3, 0
                    UNION ALL SELECT 'done', 'Done', 4, 1
                    UNION ALL SELECT 'cancelled', 'Cancelled', 5, 1
                ) AS state;
                """);

            migrationBuilder.Sql("""
                INSERT INTO WorkflowTransitions (Id, WorkspaceId, FromStateId, ToStateId)
                SELECT lower(hex(randomblob(16))), source.WorkspaceId, source.Id, target.Id
                FROM WorkflowStates AS source
                INNER JOIN WorkflowStates AS target
                    ON target.WorkspaceId = source.WorkspaceId
                WHERE (source.Key = 'backlog' AND target.Key = 'todo')
                   OR (source.Key = 'todo' AND target.Key = 'in_progress')
                   OR (source.Key = 'in_progress' AND target.Key = 'in_review')
                   OR (source.Key = 'in_review' AND target.Key IN ('done', 'cancelled'))
                   OR (source.Key <> 'cancelled' AND target.Key = 'cancelled');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowStates");

            migrationBuilder.DropTable(
                name: "WorkflowTransitions");
        }
    }
}
