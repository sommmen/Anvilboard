using Anvilboard.Application.Authorization;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Authorization;

public sealed class WorkspaceAuthorizationServiceTests
{
    [Fact]
    public async Task AuthenticateAsync_ValidUsernamePassword_ReturnsActorContext()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.AuthenticateAsync(ChannelCredential.FromUsernamePassword("coordinator", "correct horse"));

        Assert.True(result.IsAuthenticated);
        Assert.Equal(fixture.Coordinator.Id, result.Actor!.MemberId);
        Assert.Equal(fixture.Workspace.Id, result.Actor.WorkspaceId);
        Assert.Equal(Role.Coordinator, result.Actor.Role);
        Assert.Null(result.Actor.ApiTokenId);
    }

    [Fact]
    public async Task AuthenticateAsync_UnknownUsername_FailsWithoutDisclosingExistence()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.AuthenticateAsync(ChannelCredential.FromUsernamePassword("nobody", "whatever"));

        Assert.False(result.IsAuthenticated);
        Assert.Equal("CREDENTIAL_INVALID_OR_EXPIRED", result.ErrorCode);
        Assert.Null(result.Actor);
    }

    [Fact]
    public async Task AuthenticateAsync_WrongPassword_FailsWithSameErrorCodeAsUnknownUsername()
    {
        // AC-101/AC-102: a wrong password and an unknown username must be indistinguishable to
        // the caller.
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.AuthenticateAsync(ChannelCredential.FromUsernamePassword("coordinator", "wrong password"));

        Assert.False(result.IsAuthenticated);
        Assert.Equal("CREDENTIAL_INVALID_OR_EXPIRED", result.ErrorCode);
    }

    [Fact]
    public async Task AuthenticateAsync_ValidApiToken_ReturnsActorContextWithTokenId()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();
        var (token, rawToken) = await fixture.AddApiTokenAsync(fixture.AutomationAgent, [Permission.ReadWriteIssues]);

        var result = await service.AuthenticateAsync(ChannelCredential.FromApiToken(rawToken));

        Assert.True(result.IsAuthenticated);
        Assert.Equal(fixture.AutomationAgent.Id, result.Actor!.MemberId);
        Assert.Equal(token.Id, result.Actor.ApiTokenId);
    }

    [Fact]
    public async Task AuthenticateAsync_RevokedApiToken_Fails()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();
        var (token, rawToken) = await fixture.AddApiTokenAsync(fixture.AutomationAgent, [Permission.ReadWriteIssues]);
        token.RevokedAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();

        var result = await service.AuthenticateAsync(ChannelCredential.FromApiToken(rawToken));

        Assert.False(result.IsAuthenticated);
        Assert.Equal("CREDENTIAL_INVALID_OR_EXPIRED", result.ErrorCode);
    }

    [Fact]
    public async Task AuthenticateAsync_ExpiredApiToken_Fails()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();
        var (token, rawToken) = await fixture.AddApiTokenAsync(fixture.AutomationAgent, [Permission.ReadWriteIssues], expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await service.AuthenticateAsync(ChannelCredential.FromApiToken(rawToken));

        Assert.False(result.IsAuthenticated);
        Assert.Equal("CREDENTIAL_INVALID_OR_EXPIRED", result.ErrorCode);
        _ = token;
    }

    [Fact]
    public async Task AuthenticateAsync_MalformedCredential_FailsWithAuthenticationRequired()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.AuthenticateAsync(new ChannelCredential());

        Assert.False(result.IsAuthenticated);
        Assert.Equal("AUTHENTICATION_REQUIRED", result.ErrorCode);
    }

    [Fact]
    public async Task AuthorizeAsync_ActorInDifferentWorkspace_DeniesWithoutLoadingWorkspaceData()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();
        var actor = new ActorContext(fixture.Coordinator.Id, fixture.Coordinator.WorkspaceId, Role.Coordinator);

        var result = await service.AuthorizeAsync(actor, fixture.OtherWorkspace.Id, Permission.ReadBoard);

        Assert.False(result.IsAuthorized);
        Assert.Equal("WORKSPACE_ACCESS_DENIED", result.ErrorCode);
    }

    [Fact]
    public async Task AuthorizeAsync_RoleGrantsPermission_Authorizes()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();
        var actor = new ActorContext(fixture.Coordinator.Id, fixture.Workspace.Id, Role.Coordinator);

        var result = await service.AuthorizeAsync(actor, fixture.Workspace.Id, Permission.ReadWriteIssues);

        Assert.True(result.IsAuthorized);
    }

    [Fact]
    public async Task AuthorizeAsync_RoleDoesNotGrantPermission_Denies()
    {
        // Contributor never has ReadWriteIssues (workspace-wide) — only ReadWriteAssignedIssues.
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();
        var actor = new ActorContext(fixture.Contributor.Id, fixture.Workspace.Id, Role.Contributor);

        var result = await service.AuthorizeAsync(actor, fixture.Workspace.Id, Permission.ReadWriteIssues);

        Assert.False(result.IsAuthorized);
        Assert.Equal("WORKSPACE_ACCESS_DENIED", result.ErrorCode);
    }

    [Fact]
    public async Task AuthorizeAsync_TokenGrantedSubsetNarrowsRolePermissions()
    {
        // An AutomationAgent's role default includes ReadWriteIssues, but a token minted with a
        // narrower grant must not allow permissions outside that grant (defense in depth).
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();
        var actor = new ActorContext(fixture.AutomationAgent.Id, fixture.Workspace.Id, Role.AutomationAgent,
            TokenGrantedPermissions: [Permission.ReadBoard]);

        var deniedResult = await service.AuthorizeAsync(actor, fixture.Workspace.Id, Permission.ReadWriteIssues);
        var authorizedResult = await service.AuthorizeAsync(actor, fixture.Workspace.Id, Permission.ReadBoard);

        Assert.False(deniedResult.IsAuthorized);
        Assert.True(authorizedResult.IsAuthorized);
    }

    [Fact]
    public async Task AuthorizeAsync_RecordsExactlyOneAuditEventPerCall()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();
        var actor = new ActorContext(fixture.Coordinator.Id, fixture.Workspace.Id, Role.Coordinator);

        await service.AuthorizeAsync(actor, fixture.Workspace.Id, Permission.ReadBoard);

        Assert.Single(fixture.AuditEvents);
        Assert.Equal("AUTHORIZED", fixture.AuditEvents[0].Outcome);
    }

    [Fact]
    public async Task BootstrapFirstAdministratorAsync_NoExistingWorkspace_CreatesWorkspaceAndAdministrator()
    {
        await using var fixture = await AuthorizationFixture.CreateEmptyAsync();
        var service = fixture.CreateService();
        var request = new BootstrapRequest("Acme", "acme", "Root Admin", "root", "s3cret!");

        var actor = await service.BootstrapFirstAdministratorAsync(request);

        Assert.Equal(Role.Administrator, actor.Role);
        var workspace = await fixture.Db.Workspaces.SingleAsync();
        Assert.Equal("Acme", workspace.Name);
        var member = await fixture.Db.Members.SingleAsync();
        Assert.Equal("root", member.Username);
        Assert.Equal(Role.Administrator, member.Role);
        Assert.NotNull(member.PasswordHash);
        Assert.NotEqual("s3cret!", member.PasswordHash);
    }

    [Fact]
    public async Task BootstrapFirstAdministratorAsync_WorkspaceAlreadyExists_ThrowsValidationFailed()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();
        var request = new BootstrapRequest("Acme", "acme", "Root Admin", "root", "s3cret!");

        var ex = await Assert.ThrowsAsync<WorkspaceAuthorizationException>(
            () => service.BootstrapFirstAdministratorAsync(request));

        Assert.Equal("VALIDATION_FAILED", ex.ErrorCode);
    }

    [Theory]
    [InlineData("", "acme", "Root Admin", "root", "s3cret!")]
    [InlineData("Acme", "", "Root Admin", "root", "s3cret!")]
    [InlineData("Acme", "acme", "Root Admin", "", "s3cret!")]
    [InlineData("Acme", "acme", "Root Admin", "root", "")]
    public async Task BootstrapFirstAdministratorAsync_MissingRequiredField_ThrowsValidationFailed(
        string workspaceName, string workspaceSlug, string displayName, string username, string password)
    {
        await using var fixture = await AuthorizationFixture.CreateEmptyAsync();
        var service = fixture.CreateService();
        var request = new BootstrapRequest(workspaceName, workspaceSlug, displayName, username, password);

        var ex = await Assert.ThrowsAsync<WorkspaceAuthorizationException>(
            () => service.BootstrapFirstAdministratorAsync(request));

        Assert.Equal("VALIDATION_FAILED", ex.ErrorCode);
    }

    [Fact]
    public async Task RevokeCredentialAsync_ExistingToken_MarksRevoked()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();
        var (token, _) = await fixture.AddApiTokenAsync(fixture.AutomationAgent, [Permission.ReadWriteIssues]);

        await service.RevokeCredentialAsync(fixture.Workspace.Id, fixture.Coordinator.Id, token.Id);

        var stored = await fixture.Db.ApiTokens.SingleAsync(t => t.Id == token.Id);
        Assert.NotNull(stored.RevokedAt);
    }

    [Fact]
    public async Task RevokeCredentialAsync_TokenInDifferentWorkspace_ThrowsNotFound()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();
        var (token, _) = await fixture.AddApiTokenAsync(fixture.AutomationAgent, [Permission.ReadWriteIssues]);

        var ex = await Assert.ThrowsAsync<WorkspaceAuthorizationException>(
            () => service.RevokeCredentialAsync(fixture.OtherWorkspace.Id, fixture.Coordinator.Id, token.Id));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public async Task RevokeCredentialAsync_UnknownToken_ThrowsNotFound()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();

        var ex = await Assert.ThrowsAsync<WorkspaceAuthorizationException>(
            () => service.RevokeCredentialAsync(fixture.Workspace.Id, fixture.Coordinator.Id, ApiTokenId.New()));

        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public async Task IssueSessionAsync_PersistsHashedTokenAndReturnsRawTokenOnce()
    {
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();
        var actor = new ActorContext(fixture.Coordinator.Id, fixture.Workspace.Id, Role.Coordinator);

        var session = await service.IssueSessionAsync(actor);

        Assert.NotEmpty(session.RawToken);
        Assert.True(session.ExpiresAt > DateTimeOffset.UtcNow);

        var stored = await fixture.Db.ApiTokens.SingleAsync(t => t.Id == session.ApiTokenId);
        Assert.Equal(fixture.Workspace.Id, stored.WorkspaceId);
        Assert.Equal(fixture.Coordinator.Id, stored.MemberId);
        Assert.NotEqual(session.RawToken, stored.TokenHash);
        Assert.Equal(RolePermissionMap.PermissionsFor(Role.Coordinator).OrderBy(p => p),
            stored.GrantedPermissions.OrderBy(p => p));
    }

    [Fact]
    public async Task IssueSessionAsync_IssuedTokenAuthenticatesBackToSameActor()
    {
        // End-to-end: the raw token IssueSessionAsync returns must round-trip through
        // AuthenticateAsync exactly like any other API token (it is one).
        await using var fixture = await AuthorizationFixture.CreateAsync();
        var service = fixture.CreateService();
        var actor = new ActorContext(fixture.Coordinator.Id, fixture.Workspace.Id, Role.Coordinator);

        var session = await service.IssueSessionAsync(actor);
        var result = await service.AuthenticateAsync(ChannelCredential.FromApiToken(session.RawToken));

        Assert.True(result.IsAuthenticated);
        Assert.Equal(fixture.Coordinator.Id, result.Actor!.MemberId);
        Assert.Equal(session.ApiTokenId, result.Actor.ApiTokenId);
    }

    private sealed record RecordedAuditEvent(ActorContext Actor, WorkspaceId WorkspaceId, string Action, string Outcome);

    private sealed class RecordingAuditService : IAuditService
    {
        public List<RecordedAuditEvent> Events { get; } = [];

        public Task RecordAuthorizationDecisionAsync(
            ActorContext actor,
            WorkspaceId workspaceId,
            string action,
            string outcome,
            string correlationId,
            CancellationToken ct = default)
        {
            Events.Add(new RecordedAuditEvent(actor, workspaceId, action, outcome));
            return Task.CompletedTask;
        }
    }

    private sealed class AuthorizationFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly RecordingAuditService auditService = new();

        private AuthorizationFixture(SqliteConnection connection, AnvilboardDbContext db, Workspace? workspace,
            Workspace? otherWorkspace, Member? coordinator, Member? contributor, Member? automationAgent)
        {
            this.connection = connection;
            Db = db;
            Workspace = workspace!;
            OtherWorkspace = otherWorkspace!;
            Coordinator = coordinator!;
            Contributor = contributor!;
            AutomationAgent = automationAgent!;
        }

        public AnvilboardDbContext Db { get; }
        public Workspace Workspace { get; }
        public Workspace OtherWorkspace { get; }
        public Member Coordinator { get; }
        public Member Contributor { get; }
        public Member AutomationAgent { get; }
        public IReadOnlyList<RecordedAuditEvent> AuditEvents => auditService.Events;

        public WorkspaceAuthorizationService CreateService() => new(Db, auditService);

        public async Task<(ApiToken Token, string RawToken)> AddApiTokenAsync(
            Member member, IReadOnlyList<Permission> grantedPermissions, DateTimeOffset? expiresAt = null)
        {
            var rawToken = Guid.NewGuid().ToString("N");
            var token = new ApiToken
            {
                Id = ApiTokenId.New(),
                WorkspaceId = member.WorkspaceId,
                MemberId = member.Id,
                TokenHash = TokenHasher.Hash(rawToken),
                GrantedPermissions = grantedPermissions,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = expiresAt,
            };
            Db.ApiTokens.Add(token);
            await Db.SaveChangesAsync();
            return (token, rawToken);
        }

        public static async Task<AuthorizationFixture> CreateEmptyAsync()
        {
            var (connection, db) = await OpenDatabaseAsync();
            return new AuthorizationFixture(connection, db, null, null, null, null, null);
        }

        public static async Task<AuthorizationFixture> CreateAsync()
        {
            var (connection, db) = await OpenDatabaseAsync();

            var workspace = new Workspace { Id = WorkspaceId.New(), Name = "Test workspace", Slug = "test-workspace", CreatedAt = DateTimeOffset.UtcNow };
            var otherWorkspace = new Workspace { Id = WorkspaceId.New(), Name = "Other workspace", Slug = "other-workspace", CreatedAt = DateTimeOffset.UtcNow };
            db.Workspaces.AddRange(workspace, otherWorkspace);

            var coordinator = new Member
            {
                Id = MemberId.New(),
                WorkspaceId = workspace.Id,
                DisplayName = "Coordinator",
                Username = "coordinator",
                PasswordHash = PasswordHasher.Hash("correct horse"),
                Role = Role.Coordinator,
            };
            var contributor = new Member
            {
                Id = MemberId.New(),
                WorkspaceId = workspace.Id,
                DisplayName = "Contributor",
                Username = "contributor",
                PasswordHash = PasswordHasher.Hash("battery staple"),
                Role = Role.Contributor,
            };
            var automationAgent = new Member
            {
                Id = MemberId.New(),
                WorkspaceId = workspace.Id,
                DisplayName = "Bot",
                IsAgent = true,
                Role = Role.AutomationAgent,
            };
            db.Members.AddRange(coordinator, contributor, automationAgent);
            await db.SaveChangesAsync();

            return new AuthorizationFixture(connection, db, workspace, otherWorkspace, coordinator, contributor, automationAgent);
        }

        private static async Task<(SqliteConnection Connection, AnvilboardDbContext Db)> OpenDatabaseAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(connection).Options;
            var db = new AnvilboardDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return (connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
