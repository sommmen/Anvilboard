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
                "attach-artifact",
                "change-issue-status",
                "comment-on-issue",
                "create-backup",
                "create-issue",
                "create-issue-link",
                "dashboard-summary",
                "get-issue",
                "list-artifacts",
                "list-backups",
                "list-issue-links",
                "list-issues",
                "remove-artifact",
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

    [Fact]
    public void Discover_DoesNotExposeRefreshArtifact()
    {
        // `refresh-artifact` is deliberately excluded (`docs/plans/artifacts.md` §9.3, N4): a pull
        // request artifact's state must only ever reflect what the provider reports, so the upsert
        // path stays reachable from plugin correlation logic only. An agent able to call it could
        // assert a PR was merged when it was not.
        var catalog = OperationCatalog.Discover(typeof(BoardAgentService));

        Assert.DoesNotContain(catalog.Operations, operation => operation.Name.Contains("refresh", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("list-artifacts", true)]
    [InlineData("attach-artifact", false)]
    [InlineData("remove-artifact", false)]
    public void Discover_ArtifactOperations_HaveExpectedCategoryAndIdempotency(string operationName, bool isIdempotent)
    {
        var catalog = OperationCatalog.Discover(typeof(BoardAgentService));

        var operation = Assert.Single(catalog.Operations, o => o.Name == operationName);

        Assert.Equal("issues", operation.Category);
        Assert.Equal(isIdempotent, operation.IsIdempotent);
    }
}
