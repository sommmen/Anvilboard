using System.Security.Cryptography;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Authorization;

/// <inheritdoc cref="IWorkspaceAuthorizationService"/>
public sealed class WorkspaceAuthorizationService(AnvilboardDbContext db, IAuditService auditService) : IWorkspaceAuthorizationService
{
    public async Task<AuthenticationResult> AuthenticateAsync(ChannelCredential credential, CancellationToken ct = default)
    {
        if (credential.ApiToken is { Length: > 0 } rawToken)
        {
            return await AuthenticateByApiTokenAsync(rawToken, ct);
        }

        if (credential.Username is { Length: > 0 } username && credential.Password is { Length: > 0 } password)
        {
            return await AuthenticateByUsernamePasswordAsync(username, password, ct);
        }

        return AuthenticationResult.Failed("AUTHENTICATION_REQUIRED");
    }

    private async Task<AuthenticationResult> AuthenticateByApiTokenAsync(string rawToken, CancellationToken ct)
    {
        var tokenHash = TokenHasher.Hash(rawToken);
        var now = DateTimeOffset.UtcNow;

        // TokenHash is unique (see ApiTokenConfiguration), so the hash lookup already narrows to at
        // most one row. The expiry check is applied client-side because EF Core's SQLite provider
        // cannot translate the `ExpiresAt == null || ExpiresAt > now` disjunction alongside the
        // surrounding conjunctions in a single query.
        var token = await db.ApiTokens
            .Where(t => t.TokenHash == tokenHash && t.RevokedAt == null)
            .SingleOrDefaultAsync(ct);
        if (token is null || (token.ExpiresAt is { } expiresAt && expiresAt <= now))
        {
            return AuthenticationResult.Failed("CREDENTIAL_INVALID_OR_EXPIRED");
        }

        var member = await db.Members.SingleOrDefaultAsync(m => m.Id == token.MemberId, ct);
        if (member is null)
        {
            return AuthenticationResult.Failed("CREDENTIAL_INVALID_OR_EXPIRED");
        }

        var actor = new ActorContext(member.Id, member.WorkspaceId, member.Role, token.Id, token.GrantedPermissions);
        return AuthenticationResult.Succeeded(actor);
    }

    private async Task<AuthenticationResult> AuthenticateByUsernamePasswordAsync(string username, string password, CancellationToken ct)
    {
        var member = await db.Members.SingleOrDefaultAsync(m => m.Username == username, ct);
        if (member?.PasswordHash is null || !PasswordHasher.Verify(password, member.PasswordHash))
        {
            return AuthenticationResult.Failed("CREDENTIAL_INVALID_OR_EXPIRED");
        }

        var actor = new ActorContext(member.Id, member.WorkspaceId, member.Role);
        return AuthenticationResult.Succeeded(actor);
    }

    public async Task<AuthorizationResult> AuthorizeAsync(
        ActorContext actor,
        WorkspaceId workspaceId,
        Permission action,
        CancellationToken ct = default)
    {
        if (actor.WorkspaceId != workspaceId)
        {
            return await DeniedAsync(actor, workspaceId, action, "WORKSPACE_ACCESS_DENIED", ct);
        }

        if (!actor.EffectivePermissions().Contains(action))
        {
            return await DeniedAsync(actor, workspaceId, action, "WORKSPACE_ACCESS_DENIED", ct);
        }

        await auditService.RecordAuthorizationDecisionAsync(actor, workspaceId, action.ToString(), "AUTHORIZED", Guid.NewGuid().ToString(), ct);
        return AuthorizationResult.Authorized();
    }

    private async Task<AuthorizationResult> DeniedAsync(
        ActorContext actor,
        WorkspaceId workspaceId,
        Permission action,
        string errorCode,
        CancellationToken ct)
    {
        await auditService.RecordAuthorizationDecisionAsync(actor, workspaceId, action.ToString(), errorCode, Guid.NewGuid().ToString(), ct);
        return AuthorizationResult.Denied(errorCode);
    }

    public async Task<ActorContext> BootstrapFirstAdministratorAsync(BootstrapRequest request, CancellationToken ct = default)
    {
        if (await db.Workspaces.AnyAsync(ct))
        {
            throw new WorkspaceAuthorizationException("VALIDATION_FAILED", "Bootstrap already completed: a workspace already exists.");
        }

        if (string.IsNullOrWhiteSpace(request.WorkspaceName) ||
            string.IsNullOrWhiteSpace(request.WorkspaceSlug) ||
            string.IsNullOrWhiteSpace(request.AdministratorUsername) ||
            string.IsNullOrWhiteSpace(request.AdministratorPassword))
        {
            throw new WorkspaceAuthorizationException("VALIDATION_FAILED", "Workspace name, slug, administrator username, and password are required.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var workspace = new Workspace
        {
            Id = WorkspaceId.New(),
            Name = request.WorkspaceName,
            Slug = request.WorkspaceSlug,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Workspaces.Add(workspace);

        var administrator = new Member
        {
            Id = MemberId.New(),
            WorkspaceId = workspace.Id,
            DisplayName = request.AdministratorDisplayName,
            Email = request.AdministratorEmail,
            Username = request.AdministratorUsername,
            PasswordHash = PasswordHasher.Hash(request.AdministratorPassword),
            Role = Role.Administrator,
        };
        db.Members.Add(administrator);

        var workflowStates = CreateDefaultWorkflowStates(workspace.Id);
        db.WorkflowStates.AddRange(workflowStates);
        db.WorkflowTransitions.AddRange(CreateDefaultWorkflowTransitions(workspace.Id, workflowStates));

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var actor = new ActorContext(administrator.Id, workspace.Id, administrator.Role);
        await auditService.RecordAuthorizationDecisionAsync(actor, workspace.Id, "WORKSPACE_BOOTSTRAPPED", "AUTHORIZED", Guid.NewGuid().ToString(), ct);
        return actor;
    }

    /// <summary>
    /// Mirrors the six-state default workflow that the <c>AddWorkflowStates</c> migration seeds for
    /// workspaces that already existed at migration time. Without this, a freshly bootstrapped
    /// workspace has no <see cref="WorkflowState"/> rows and every subsequent issue-creation request
    /// fails because <c>IssueService.GetInitialWorkflowStateIdAsync</c> has no state to assign.
    /// </summary>
    private static List<WorkflowState> CreateDefaultWorkflowStates(WorkspaceId workspaceId) =>
    [
        new() { Id = WorkflowStateId.New(), WorkspaceId = workspaceId, Key = "backlog", DisplayName = "Backlog", Order = 0 },
        new() { Id = WorkflowStateId.New(), WorkspaceId = workspaceId, Key = "todo", DisplayName = "Todo", Order = 1 },
        new() { Id = WorkflowStateId.New(), WorkspaceId = workspaceId, Key = "in_progress", DisplayName = "In Progress", Order = 2 },
        new() { Id = WorkflowStateId.New(), WorkspaceId = workspaceId, Key = "in_review", DisplayName = "In Review", Order = 3 },
        new() { Id = WorkflowStateId.New(), WorkspaceId = workspaceId, Key = "done", DisplayName = "Done", Order = 4, IsTerminal = true },
        new() { Id = WorkflowStateId.New(), WorkspaceId = workspaceId, Key = "cancelled", DisplayName = "Cancelled", Order = 5, IsTerminal = true },
    ];

    /// <summary>
    /// Mirrors the linear adjacency the <c>AddWorkflowStates</c> migration seeds: a straight
    /// progression through the non-terminal states, plus a "cancelled" escape hatch from any state.
    /// </summary>
    private static List<WorkflowTransition> CreateDefaultWorkflowTransitions(
        WorkspaceId workspaceId, IReadOnlyList<WorkflowState> states)
    {
        WorkflowStateId StateId(string key) => states.Single(state => state.Key == key).Id;

        var transitions = new List<WorkflowTransition>
        {
            Transition(StateId("backlog"), StateId("todo")),
            Transition(StateId("todo"), StateId("in_progress")),
            Transition(StateId("in_progress"), StateId("in_review")),
            Transition(StateId("in_review"), StateId("done")),
        };
        transitions.AddRange(states
            .Where(state => state.Key != "cancelled")
            .Select(state => Transition(state.Id, StateId("cancelled"))));

        return transitions;

        WorkflowTransition Transition(WorkflowStateId from, WorkflowStateId to) => new()
        {
            Id = WorkflowTransitionId.New(),
            WorkspaceId = workspaceId,
            FromStateId = from,
            ToStateId = to,
        };
    }

    public async Task RevokeCredentialAsync(
        WorkspaceId workspaceId,
        MemberId actorPerformingRevocation,
        ApiTokenId credentialId,
        CancellationToken ct = default)
    {
        var token = await db.ApiTokens.SingleOrDefaultAsync(t => t.Id == credentialId && t.WorkspaceId == workspaceId, ct);
        if (token is null)
        {
            throw new WorkspaceAuthorizationException("REFERENCED_ENTITY_NOT_FOUND", "No credential with that id exists in this workspace.");
        }

        var actor = await db.Members.SingleOrDefaultAsync(member =>
            member.Id == actorPerformingRevocation && member.WorkspaceId == workspaceId, ct);
        if (actor is null)
        {
            throw new WorkspaceAuthorizationException("REFERENCED_ENTITY_NOT_FOUND", "No member with that id exists in this workspace.");
        }

        token.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await auditService.RecordAuthorizationDecisionAsync(
            new ActorContext(actor.Id, workspaceId, actor.Role),
            workspaceId,
            "CREDENTIAL_REVOKED",
            "AUTHORIZED",
            Guid.NewGuid().ToString(),
            ct);
    }

    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    public async Task<SessionIssuedResult> IssueSessionAsync(ActorContext actor, CancellationToken ct = default)
    {
        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime);

        var session = new ApiToken
        {
            Id = ApiTokenId.New(),
            WorkspaceId = actor.WorkspaceId,
            MemberId = actor.MemberId,
            TokenHash = TokenHasher.Hash(rawToken),
            GrantedPermissions = RolePermissionMap.PermissionsFor(actor.Role).ToList(),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
        };
        db.ApiTokens.Add(session);
        await db.SaveChangesAsync(ct);

        return new SessionIssuedResult(session.Id, rawToken, expiresAt);
    }
}
