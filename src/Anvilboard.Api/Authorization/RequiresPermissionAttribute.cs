using Anvilboard.Domain;

namespace Anvilboard.Api.Authorization;

/// <summary>
/// Endpoint metadata declaring the <see cref="Permission"/>(s) a caller must hold in its
/// authenticated workspace to reach the annotated route — holding any one of
/// <see cref="Permissions"/> is sufficient (e.g. a route reachable by both a `Coordinator`'s
/// workspace-wide `ReadWriteIssues` and a `Contributor`'s narrower `ReadWriteAssignedIssues`; the
/// assignment/team scoping itself is enforced downstream by the owning component, per this
/// component's "no per-field business-rule guards" exclusion). Read by
/// <see cref="WorkspaceAuthorizationMiddleware"/> — endpoints never call
/// <c>IWorkspaceAuthorizationService.AuthorizeAsync</c> themselves (§11.2 single enforcement
/// point).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequiresPermissionAttribute(params Permission[] permissions) : Attribute
{
    public IReadOnlyList<Permission> Permissions { get; } = permissions;
}

/// <summary>
/// Fluent alternative to <see cref="RequiresPermissionAttribute"/> for minimal-API route
/// registrations.
/// </summary>
public static class RequiresPermissionEndpointExtensions
{
    /// <summary>Declares that <paramref name="builder"/>'s route requires at least one of
    /// <paramref name="permissions"/>.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, params Permission[] permissions)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RequiresPermissionAttribute(permissions));
        return builder;
    }
}
