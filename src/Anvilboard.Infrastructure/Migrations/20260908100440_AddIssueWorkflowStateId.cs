using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Anvilboard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueWorkflowStateId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Issues",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowStateId",
                table: "Issues",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE Issues
                SET WorkflowStateId = (
                    SELECT states.Id
                    FROM Teams AS teams
                    INNER JOIN WorkflowStates AS states ON states.WorkspaceId = teams.WorkspaceId
                    WHERE teams.Id = Issues.TeamId
                      AND states.Key = CASE Issues.Status
                          WHEN 0 THEN 'backlog'
                          WHEN 1 THEN 'todo'
                          WHEN 2 THEN 'in_progress'
                          WHEN 3 THEN 'in_review'
                          WHEN 4 THEN 'done'
                          WHEN 5 THEN 'cancelled'
                      END
                );
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "WorkflowStateId",
                table: "Issues",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "Issues");

            migrationBuilder.DropColumn(
                name: "WorkflowStateId",
                table: "Issues");
        }
    }
}
