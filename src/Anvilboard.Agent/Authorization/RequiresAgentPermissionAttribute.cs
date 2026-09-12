using Anvilboard.Domain;

namespace Anvilboard.Agent.Authorization;

/// <summary>
/// Operation metadata declaring the <see cref="Permission"/>(s) a caller must hold in its
/// authenticated workspace to invoke the annotated operation — holding any one of
/// <see cref="Permissions"/> is sufficient.
/// </summary>
/// <remarks>
/// The CLI/MCP counterpart of the REST host's <c>RequiresPermissionAttribute</c>, read by
/// <see cref="WorkspaceAuthorizationPolicy"/>. Operations never call
/// <c>IWorkspaceAuthorizationService.AuthorizeAsync</c> themselves (§11.2 single enforcement
/// point), and an operation with no annotation is <em>denied</em> rather than allowed, so adding
/// an operation without considering its permission fails closed.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresAgentPermissionAttribute(params Permission[] permissions) : Attribute
{
    public IReadOnlyList<Permission> Permissions { get; } = permissions;
}
