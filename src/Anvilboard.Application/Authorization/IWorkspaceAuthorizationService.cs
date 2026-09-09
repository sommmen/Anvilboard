using Anvilboard.Domain;

namespace Anvilboard.Application.Authorization;

/// <summary>
/// The single enforcement point for authentication and authorization, invoked identically by the
/// REST middleware, the CLI, and the MCP host — no channel is allowed to implement its own ad hoc
/// checks.
/// </summary>
public interface IWorkspaceAuthorizationService
{
    /// <summary>
    /// Resolves the raw <paramref name="credential"/> presented by a caller into an
    /// <see cref="ActorContext"/>. Never discloses whether a workspace, username, or token exists
    /// (AC-101/AC-102): a malformed, unknown, expired, or revoked credential all fail with the
    /// same <c>CREDENTIAL_INVALID_OR_EXPIRED</c>/<c>AUTHENTICATION_REQUIRED</c> error codes.
    /// </summary>
    Task<AuthenticationResult> AuthenticateAsync(ChannelCredential credential, CancellationToken ct = default);

    /// <summary>
    /// Checks whether <paramref name="actor"/> may perform <paramref name="action"/> inside
    /// <paramref name="workspaceId"/>. Denies immediately, without loading any workspace data, if
    /// the actor was authenticated against a different workspace.
    /// </summary>
    Task<AuthorizationResult> AuthorizeAsync(
        ActorContext actor,
        WorkspaceId workspaceId,
        Permission action,
        CancellationToken ct = default);

    /// <summary>
    /// Creates the first <see cref="Workspace"/> and its first <see cref="Role.Administrator"/>
    /// member. Rejected with <c>VALIDATION_FAILED</c> once any workspace already exists.
    /// </summary>
    Task<ActorContext> BootstrapFirstAdministratorAsync(BootstrapRequest request, CancellationToken ct = default);

    /// <summary>
    /// Revokes the <see cref="ApiToken"/> identified by <paramref name="credentialId"/>. The
    /// caller must already hold <see cref="Permission.ManageCredentials"/> in
    /// <paramref name="workspaceId"/>; revocation takes effect immediately, never deferred to a
    /// background sweep.
    /// </summary>
    Task RevokeCredentialAsync(
        WorkspaceId workspaceId,
        MemberId actorPerformingRevocation,
        ApiTokenId credentialId,
        CancellationToken ct = default);

    /// <summary>
    /// Mints a new browser session credential for the already-authenticated <paramref name="actor"/>
    /// (§ Included: "HTTP-only secure session cookie for the SPA"), persisting only its hash as an
    /// <see cref="ApiToken"/> row scoped to the actor's role-default permissions. The raw value is
    /// returned exactly once, in <see cref="SessionIssuedResult"/>, for the caller to set as the
    /// session cookie — it is never recoverable afterward (NFR-SEC-001).
    /// </summary>
    Task<SessionIssuedResult> IssueSessionAsync(ActorContext actor, CancellationToken ct = default);
}
