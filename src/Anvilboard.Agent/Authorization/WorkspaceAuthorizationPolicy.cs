using System.Reflection;
using System.Text.Json;
using Anvilboard.Agent.Hosting;
using Anvilboard.Application.Authorization;
using Anvilboard.Domain;
using DotNetAgentSurface.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Anvilboard.Agent.Authorization;

/// <summary>
/// The single authorization enforcement point for the CLI and MCP channels: authenticates the
/// host's configured credential, checks the operation's declared permission, and publishes the
/// resulting <see cref="ActorContext"/> for the operation to attribute its writes to.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a global <see cref="IOperationInvocationPolicy"/> on <c>OperationInvoker</c>, so
/// it runs before argument binding and before the operation target is resolved. A denied
/// invocation therefore never constructs an application service and never opens a transaction.
/// </para>
/// <para>
/// This mirrors the REST host's <c>WorkspaceAuthorizationMiddleware</c>, including its
/// single-audit-event behaviour: permissions are pre-filtered against the actor's effective
/// permission set in memory so that exactly one permission is submitted to
/// <see cref="IWorkspaceAuthorizationService.AuthorizeAsync"/> and exactly one authorization
/// decision is recorded per invocation (AC-104).
/// </para>
/// </remarks>
public sealed class WorkspaceAuthorizationPolicy : IOperationInvocationPolicy
{
    public async ValueTask<OperationPolicyResult> EvaluateAsync(
        OperationDescriptor operation,
        IReadOnlyDictionary<string, JsonElement>? inputs,
        OperationConfirmation? confirmation = null,
        CancellationToken cancellationToken = default,
        OperationInvocationContext? invocationContext = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var services = AgentInvocationScope.Services;
        var credentials = services.GetRequiredService<AgentCredentialSource>();
        var authorization = services.GetRequiredService<IWorkspaceAuthorizationService>();

        var authentication = await authorization
            .AuthenticateAsync(credentials.Current, cancellationToken)
            .ConfigureAwait(false);

        if (!authentication.IsAuthenticated || authentication.Actor is not { } actor)
        {
            return OperationPolicyResult.Deny(authentication.ErrorCode ?? "AUTHENTICATION_REQUIRED");
        }

        var required = RequiredPermissions(operation);
        if (required.Count == 0)
        {
            // Fail closed: an operation that forgot its annotation is not a public operation.
            return OperationPolicyResult.Deny("WORKSPACE_ACCESS_DENIED");
        }

        var effective = actor.EffectivePermissions();
        var permissionToCheck = required.FirstOrDefault(effective.Contains, required[0]);

        var decision = await authorization
            .AuthorizeAsync(actor, actor.WorkspaceId, permissionToCheck, cancellationToken)
            .ConfigureAwait(false);

        if (!decision.IsAuthorized)
        {
            return OperationPolicyResult.Deny(decision.ErrorCode ?? "WORKSPACE_ACCESS_DENIED");
        }

        services.GetRequiredService<AgentActorAccessor>().Set(actor);
        return OperationPolicyResult.Allow();
    }

    /// <summary>
    /// Reads the permissions declared by <see cref="RequiresAgentPermissionAttribute"/> on the
    /// operation method.
    /// </summary>
    public static IReadOnlyList<Permission> RequiredPermissions(OperationDescriptor operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation.Method.GetCustomAttribute<RequiresAgentPermissionAttribute>()?.Permissions ?? [];
    }
}
