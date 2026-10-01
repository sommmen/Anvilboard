using System.Reflection;
using Anvilboard.Application.Issues;

namespace Anvilboard.Application.Tests.Issues;

/// <summary>
/// A structural guard, not a behavioural one. Explicit <c>expectedVersion</c> parameters buy
/// precision over an EF concurrency token but give up the token's one virtue: being impossible to
/// forget. This test buys that virtue back — a future field-level mutation added to
/// <see cref="IssueService"/> without the parameter fails here rather than silently shipping a
/// lost-update window that no behavioural test would think to look for.
/// </summary>
public sealed class IssueMutationContractTests
{
    /// <summary>
    /// Mutations deliberately outside the conditional-write contract. Comments are append-only, so
    /// two concurrent authors both belong in the thread and there is nothing to lose. Ingestion
    /// upserts reconcile against an external system that owns the truth, and rejecting them on a
    /// version mismatch would stall a sync the user cannot retry by hand.
    /// </summary>
    private static readonly string[] ExemptMutations = ["CreateAsync", "AddCommentAsync"];

    [Theory]
    [InlineData(nameof(IssueService.ChangeStatusAsync))]
    [InlineData(nameof(IssueService.AssignAsync))]
    public void ConditionalMutation_ExposesAnOptionalExpectedVersion(string methodName)
    {
        var method = typeof(IssueService).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var parameter = Assert.Single(method!.GetParameters(), p => p.Name == "expectedVersion");
        Assert.Equal(typeof(int?), parameter.ParameterType);
        Assert.True(parameter.IsOptional, "expectedVersion must be optional so non-participating callers keep working.");
        Assert.Null(parameter.DefaultValue);
    }

    /// <summary>
    /// Catches the case this suite exists for: a new public field-level mutation that forgot the
    /// parameter. It matches by shape rather than by an enumerated allow-list of method names, so
    /// adding a mutation is what trips it, not forgetting to update a list here.
    /// </summary>
    [Fact]
    public void EveryFieldLevelMutation_AcceptsAnExpectedVersion()
    {
        var missing = typeof(IssueService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Where(m => !ExemptMutations.Contains(m.Name, StringComparer.Ordinal))
            .Where(m => !m.Name.StartsWith("UpsertFromExternal", StringComparison.Ordinal))
            .Where(IsFieldLevelMutation)
            .Where(m => m.GetParameters().All(p => p.Name != "expectedVersion"))
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(missing);
    }

    /// <summary>
    /// A field-level mutation takes an issue id and returns the mutated issue as non-null. Reads
    /// are told apart by their nullable <c>Task&lt;Issue?&gt;</c> — a lookup can miss, a mutation
    /// that reached its return statement cannot — which keeps the rule from depending on a naming
    /// convention a future method might not follow.
    /// </summary>
    private static bool IsFieldLevelMutation(MethodInfo method)
    {
        if (method.GetParameters().All(p => p.ParameterType != typeof(Domain.IssueId)))
        {
            return false;
        }

        var returnType = method.ReturnType;
        if (!returnType.IsGenericType || returnType.GetGenericArguments()[0] != typeof(Domain.Issue))
        {
            return false;
        }

        var returnNullability = new NullabilityInfoContext().Create(method.ReturnParameter);
        return returnNullability.GenericTypeArguments[0].ReadState != NullabilityState.Nullable;
    }
}
