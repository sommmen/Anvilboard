using Anvilboard.Domain;

namespace Anvilboard.Application.Authorization;

/// <summary>
/// The authenticated identity for the current request/invocation, resolved once by
/// <see cref="IWorkspaceAuthorizationService.AuthenticateAsync"/> and then passed unchanged to
/// every subsequent <see cref="IWorkspaceAuthorizationService.AuthorizeAsync"/> call for that same
/// request. Carries only non-secret identifiers (AC-108): never a raw token or session value.
/// </summary>
public sealed record ActorContext(
    MemberId MemberId,
    WorkspaceId WorkspaceId,
    Role Role,
    ApiTokenId? ApiTokenId = null,
    IReadOnlyList<Permission>? TokenGrantedPermissions = null)
{
    /// <summary>
    /// The permissions this actor may exercise: the role default, intersected with the token's
    /// granted subset when the actor authenticated via an API token.
    /// </summary>
    public IReadOnlySet<Permission> EffectivePermissions()
    {
        var roleDefaults = RolePermissionMap.PermissionsFor(Role);
        return TokenGrantedPermissions is null
            ? roleDefaults
            : roleDefaults.Intersect(TokenGrantedPermissions).ToHashSet();
    }
}
