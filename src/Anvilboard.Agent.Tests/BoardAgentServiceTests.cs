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
                "create-issue",
                "create-issue-link",
                "dashboard-summary",
                "get-issue",
                "list-issue-links",
                "list-issues",
                "remove-issue-link",
            ],
            catalog.Operations.Select(operation => operation.Name));
    }
}
