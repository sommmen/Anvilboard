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
        // `restore` is deliberately excluded from the agent surface (`docs/plans/backup-and-restore.md`
        // §9.3, MAJ-015): the agent host has no workspace-scoped authorization or actor identity, so
        // exposing an instance-wide destructive operation here would be a privilege escalation. This
        // guards against it being silently reintroduced.
        var catalog = OperationCatalog.Discover(typeof(BoardAgentService));

        Assert.DoesNotContain(catalog.Operations, operation => operation.Name.Contains("restore", StringComparison.OrdinalIgnoreCase));
    }
}
