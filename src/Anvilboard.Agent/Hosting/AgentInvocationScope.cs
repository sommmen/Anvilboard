using Microsoft.Extensions.DependencyInjection;

namespace Anvilboard.Agent.Hosting;

/// <summary>
/// The single DI scope shared by everything participating in one CLI/MCP operation invocation.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the previous "fresh scope per <see cref="IServiceProvider.GetService"/> call"
/// approach, which had two defects that block workspace-scoped authorization: the authorization
/// policy and the operation target resolved <em>different</em> <c>AnvilboardDbContext</c>
/// instances (so an <c>ActorContext</c> established by the policy had nowhere to live), and every
/// resolution leaked an undisposed scope for the lifetime of an MCP session.
/// </para>
/// <para>
/// The current scope is held in an <see cref="AsyncLocal{T}"/> so it flows across the
/// <c>await</c> boundaries between the host, <c>OperationInvoker</c>, the policy, and the
/// operation method without threading a parameter through the agent-surface package's API.
/// </para>
/// </remarks>
public static class AgentInvocationScope
{
    private static readonly AsyncLocal<IServiceScope?> Current = new();

    /// <summary>
    /// Opens a scope for one invocation. Dispose the returned handle when the invocation
    /// completes; the previous scope (if any) is restored, so sequential and nested invocations
    /// never bleed into one another.
    /// </summary>
    public static Handle Begin(IServiceProvider root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var previous = Current.Value;
        var scope = root.CreateScope();
        Current.Value = scope;
        return new Handle(scope, previous);
    }

    /// <summary>
    /// The provider for the invocation in flight.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No invocation is in flight. This is a host wiring bug rather than a caller error: every
    /// entry point must open a scope before invoking an operation.
    /// </exception>
    public static IServiceProvider Services =>
        Current.Value?.ServiceProvider ?? throw new InvalidOperationException(
            "No agent invocation scope is active. The CLI/MCP host must call "
            + $"{nameof(AgentInvocationScope)}.{nameof(Begin)} before invoking an operation.");

    /// <summary>Whether an invocation scope is currently active.</summary>
    public static bool IsActive => Current.Value is not null;

    public readonly struct Handle(IServiceScope scope, IServiceScope? previous) : IDisposable
    {
        public IServiceProvider Services => scope.ServiceProvider;

        public void Dispose()
        {
            Current.Value = previous;
            scope.Dispose();
        }
    }
}

/// <summary>
/// The <see cref="IServiceProvider"/> handed to <c>OperationInvoker</c>. The invoker is built once
/// and reused for every invocation, so it must resolve operation targets from whichever invocation
/// scope is currently active rather than from the root container.
/// </summary>
internal sealed class AgentInvocationServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => AgentInvocationScope.Services.GetService(serviceType);
}
