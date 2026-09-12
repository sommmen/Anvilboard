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
                "assign-issue",
                "change-issue-status",
                "comment-on-issue",
                "create-backup",
                "create-issue",
                "create-issue-link",
                "dashboard-summary",
                "get-issue",
                "list-backups",
                "list-issue-links",
                "list-issues",
                "remove-issue-link",
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
