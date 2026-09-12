using System.Reflection;
using Anvilboard.Agent.Authorization;
using Anvilboard.Agent.Contracts;
using Anvilboard.Domain;
using DotNetAgentSurface.Core;

namespace Anvilboard.Agent.Tests;

/// <summary>
/// Catalog-wide invariants that make the surface's security and contract properties structural: a
/// newly added operation that forgets any of them fails here rather than shipping a hole.
/// </summary>
public sealed class AgentCatalogInvariantsTests
{
    private static readonly OperationCatalog Catalog = OperationCatalog.Discover(typeof(BoardAgentService));

    /// <summary>
    /// Operations whose effects are confined to a single retry-safe write, or which do not write at
    /// all, and therefore do not carry an idempotency key. Backup creation is excluded deliberately
    /// (DR-AGT-005): a duplicate snapshot is wasteful but never incorrect, whereas a key would
    /// suppress a legitimately intended second snapshot.
    /// </summary>
    private static readonly HashSet<string> OperationsWithoutIdempotencyKey =
    [
        "list-issues", "get-issue", "dashboard-summary", "list-issue-links",
        "create-backup", "list-backups", "verify-backup",
        "list-workflow-states", "list-workflow-transitions",
    ];

    [Fact]
    public void EveryOperation_DeclaresRequiredPermissions()
    {
        var unannotated = Catalog.Operations
            .Where(operation => WorkspaceAuthorizationPolicy.RequiredPermissions(operation).Count == 0)
            .Select(operation => operation.Name)
            .ToArray();

        Assert.Empty(unannotated);
    }

    [Fact]
    public void EveryOperation_ReturnsVersionedEnvelope()
    {
        var unwrapped = Catalog.Operations
            .Where(operation => !ReturnsAgentResponse(operation.Method))
            .Select(operation => operation.Name)
            .ToArray();

        Assert.Empty(unwrapped);
    }

    [Fact]
    public void EveryMutatingOperation_RequiresAnIdempotencyKey()
    {
        var missing = Catalog.Operations
            .Where(operation => !OperationsWithoutIdempotencyKey.Contains(operation.Name))
            .Where(operation => !operation.Parameters.Any(p => p.Name == "idempotencyKey"))
            .Select(operation => operation.Name)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void IdempotencyKey_IsRequired_NotOptional()
    {
        // An optional key would silently degrade to at-least-once semantics exactly when it matters:
        // an agent retrying after a timeout has no way to know suppression was skipped.
        var optional = Catalog.Operations
            .SelectMany(operation => operation.Parameters.Select(p => (operation.Name, Parameter: p)))
            .Where(entry => entry.Parameter.Name == "idempotencyKey" && entry.Parameter.IsOptional)
            .Select(entry => entry.Name)
            .ToArray();

        Assert.Empty(optional);
    }

    [Fact]
    public void NoOperation_AcceptsACallerSuppliedWorkspaceOrActorId()
    {
        // The workspace and actor come from the authenticated credential. Accepting either as an
        // input would let any valid credential act on any workspace or impersonate any member.
        var spoofable = Catalog.Operations
            .SelectMany(operation => operation.Parameters.Select(p => (operation.Name, Parameter: p)))
            .Where(entry => entry.Parameter.Name is "workspaceId" or "actorId" or "authorId" or "createdById")
            .Select(entry => $"{entry.Name}.{entry.Parameter.Name}")
            .ToArray();

        Assert.Empty(spoofable);
    }

    [Fact]
    public void EveryOperation_IsSafe_SoNoConfirmationEnforcingPolicyIsRequired()
    {
        // OperationInvoker throws at construction if any operation is above Safe and no
        // IConfirmationEnforcingPolicy is supplied. Keeping every operation Safe is what lets the
        // host register only the authorization policy.
        var elevated = Catalog.Operations
            .Where(operation => operation.SafetyLevel != AgentSafetyLevel.Safe)
            .Select(operation => operation.Name)
            .ToArray();

        Assert.Empty(elevated);
    }

    [Fact]
    public void BackupOperations_RequireBackupPermission()
    {
        // Role.AutomationAgent deliberately lacks ManageBackupRestore, so a plain automation token
        // cannot take or inspect backups even though it can manage issues.
        foreach (var name in new[] { "create-backup", "list-backups", "verify-backup" })
        {
            var operation = Assert.Single(Catalog.Operations, o => o.Name == name);
            Assert.Equal(
                [Permission.ManageBackupRestore],
                WorkspaceAuthorizationPolicy.RequiredPermissions(operation));
        }
    }

    [Fact]
    public void EveryExample_UsesTheFlagSyntaxTheCommandLineAdapterAccepts()
    {
        // OperationCommandLineAdapter.ParseInputs requires `--name value` pairs and rejects anything
        // else, so a `name=value` example is not merely stylistically off — it cannot run.
        var invalid = Catalog.Operations
            .SelectMany(operation => operation.Examples.Select(example => (operation.Name, Example: example)))
            .Where(entry => !IsRunnableExample(entry.Name, entry.Example))
            .Select(entry => $"{entry.Name}: {entry.Example}")
            .ToArray();

        Assert.Empty(invalid);
    }

    private static bool IsRunnableExample(string operationName, string example)
    {
        var tokens = Tokenize(example);
        if (tokens.Count == 0 || tokens[0] != operationName)
        {
            return false;
        }

        for (var i = 1; i < tokens.Count; i += 2)
        {
            if (!tokens[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= tokens.Count)
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> Tokenize(string example)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in example)
        {
            switch (ch)
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;
                case ' ' when !inQuotes:
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }

                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static bool ReturnsAgentResponse(MethodInfo method)
    {
        var returnType = method.ReturnType;
        if (returnType.IsGenericType
            && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            returnType = returnType.GetGenericArguments()[0];
        }

        return returnType.IsGenericType
            && returnType.GetGenericTypeDefinition() == typeof(AgentResponse<>);
    }
}
