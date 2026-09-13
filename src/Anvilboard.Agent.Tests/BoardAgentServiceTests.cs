using Anvilboard.Agent;
using DotNetAgentSurface.Core;

namespace Anvilboard.Agent.Tests;

public sealed class BoardAgentServiceTests
{
    [Fact]
    public void Discover_ExposesExpectedOperations()
    {
        var catalog = OperationCatalog.Discover(typeof(BoardAgentService));

        Assert.Equal(
            [
                "archive-workflow-state",
                "assign-issue",
                "change-issue-status",
                "comment-on-issue",
                "create-backup",
                "create-issue",
                "create-issue-link",
                "create-workflow-state",
                "create-workflow-transition",
                "dashboard-summary",
                "get-issue",
                "list-backups",
                "list-issue-links",
                "list-issues",
                "list-workflow-states",
                "list-workflow-transitions",
                "remove-issue-link",
                "remove-workflow-transition",
                "update-workflow-state",
                "verify-backup",
            ],
            catalog.Operations.Select(operation => operation.Name));
    }

    [Theory]
    [InlineData("create-backup", false)]
    [InlineData("list-backups", true)]
    [InlineData("verify-backup", true)]
    public void Discover_BackupOperations_HaveExpectedCategoryAndIdempotency(string operationName, bool isIdempotent)
    {
        var catalog = OperationCatalog.Discover(typeof(BoardAgentService));

        var operation = Assert.Single(catalog.Operations, o => o.Name == operationName);

        Assert.Equal("backup", operation.Category);
        Assert.Equal(isIdempotent, operation.IsIdempotent);
    }

    [Theory]
    [InlineData("list-workflow-states", true)]
    [InlineData("list-workflow-transitions", true)]
    [InlineData("create-workflow-state", false)]
    [InlineData("update-workflow-state", false)]
    [InlineData("archive-workflow-state", false)]
    [InlineData("create-workflow-transition", false)]
    [InlineData("remove-workflow-transition", false)]
    public void Discover_WorkflowOperations_HaveExpectedCategoryAndIdempotency(
        string operationName,
        bool isIdempotent)
    {
        var catalog = OperationCatalog.Discover(typeof(BoardAgentService));

        var operation = Assert.Single(catalog.Operations, o => o.Name == operationName);

        Assert.Equal("workflow", operation.Category);
        Assert.Equal(isIdempotent, operation.IsIdempotent);
    }

    [Fact]
    public void Discover_DoesNotExposeRestore()
    {
        // `restore` is deliberately excluded from the agent surface
        // (`docs/plans/agent-surface-authorization.md` DR-AGT-004). Authorization now exists, so the
        // blocker is no longer identity: restore is instance-wide and destructive, so it would need
        // `AgentSafetyLevel.Dangerous` plus an `IConfirmationEnforcingPolicy` — and MCP has no
        // trustworthy interactive confirmation channel, since a client can set the confirmed flag
        // itself. This guards against it being silently reintroduced.
        var catalog = OperationCatalog.Discover(typeof(BoardAgentService));

        Assert.DoesNotContain(catalog.Operations, operation => operation.Name.Contains("restore", StringComparison.OrdinalIgnoreCase));
    }
}
