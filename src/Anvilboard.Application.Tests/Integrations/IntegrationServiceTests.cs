using Anvilboard.Application.Authorization;
using Anvilboard.Application.Integrations;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Plugins.Abstractions;
using Anvilboard.Plugins.Abstractions.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Integrations;

public sealed class IntegrationServiceTests
{
    [Fact]
    public async Task ConfigureEnablePauseAndRemove_TransitionsLifecycleWithoutExposingCredentials()
    {
        await using var fixture = await IntegrationFixture.CreateAsync();
        var service = fixture.CreateService();

        var configured = await service.ConfigureAsync(
            fixture.Actor,
            IntegrationProvider.GitHub,
            new Dictionary<string, string> { ["token"] = "super-secret" },
            "{\"repositories\":[\"owner/repository\"]}");

        Assert.Equal(IntegrationStatus.Configured, configured.Status);
        Assert.DoesNotContain("super-secret", configured.SettingsJson);
        var persisted = await fixture.Db.Integrations.SingleAsync();
        Assert.NotNull(persisted.ProtectedCredentials);
        Assert.DoesNotContain("super-secret", persisted.ProtectedCredentials);

        var enabled = await service.EnableAsync(fixture.Actor, new IntegrationId(configured.Id));
        var paused = await service.PauseAsync(fixture.Actor, new IntegrationId(configured.Id));

        Assert.Equal(IntegrationStatus.Enabled, enabled.Status);
        Assert.Equal(IntegrationStatus.Paused, paused.Status);

        await service.RemoveAsync(fixture.Actor, new IntegrationId(configured.Id), confirm: true);

        var stored = await fixture.Db.Integrations.SingleAsync();
        Assert.Equal(IntegrationStatus.Removed, stored.Status);
        Assert.Null(stored.ProtectedCredentials);
    }

    [Fact]
    public async Task ConfigureAsync_RejectsMalformedSettings()
    {
        await using var fixture = await IntegrationFixture.CreateAsync();
        var service = fixture.CreateService();

        var exception = await Assert.ThrowsAsync<IntegrationException>(() => service.ConfigureAsync(
            fixture.Actor,
            IntegrationProvider.Linear,
            new Dictionary<string, string> { ["apiKey"] = "secret" },
            "not-json"));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
        Assert.Empty(fixture.Db.Integrations);
    }

    [Fact]
    public async Task RemoveAsync_RequiresExplicitConfirmation()
    {
        await using var fixture = await IntegrationFixture.CreateAsync();
        var service = fixture.CreateService();
        var configured = await service.ConfigureAsync(
            fixture.Actor,
            IntegrationProvider.Linear,
            new Dictionary<string, string> { ["apiKey"] = "secret" },
            "{}");

        var exception = await Assert.ThrowsAsync<IntegrationException>(() => service.RemoveAsync(
            fixture.Actor,
            new IntegrationId(configured.Id),
            confirm: false));

        Assert.Equal("VALIDATION_FAILED", exception.ErrorCode);
        Assert.Equal(IntegrationStatus.Configured, (await fixture.Db.Integrations.SingleAsync()).Status);
    }

    [Fact]
    public async Task ValidateAsync_UsesProviderValidatorWithoutImportingData()
    {
        await using var fixture = await IntegrationFixture.CreateAsync(new PassingValidator(IntegrationProvider.GitHub));
        var service = fixture.CreateService();
        var configured = await service.ConfigureAsync(
            fixture.Actor,
            IntegrationProvider.GitHub,
            new Dictionary<string, string> { ["token"] = "secret" },
            "{}");

        var validated = await service.ValidateAsync(fixture.Actor, new IntegrationId(configured.Id));

        Assert.Equal(configured.Id, validated.Id);
        Assert.Equal(1, fixture.ValidatorCalls);
    }

    private sealed class IntegrationFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IIntegrationValidator[] _validators;

        private IntegrationFixture(SqliteConnection connection, AnvilboardDbContext db, IIntegrationValidator[] validators)
        {
            _connection = connection;
            Db = db;
            _validators = validators;
            WorkspaceId = WorkspaceId.New();
            Actor = new ActorContext(MemberId.New(), WorkspaceId, Role.Administrator);
            SecretStore = new InMemorySecretStore();
        }

        public AnvilboardDbContext Db { get; }
        public WorkspaceId WorkspaceId { get; }
        public ActorContext Actor { get; }
        public InMemorySecretStore SecretStore { get; }
        public int ValidatorCalls => _validators.OfType<PassingValidator>().Sum(validator => validator.Calls);

        public static async Task<IntegrationFixture> CreateAsync(params IIntegrationValidator[] validators)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(connection).Options;
            var fixture = new IntegrationFixture(connection, new AnvilboardDbContext(options), validators);
            await fixture.Db.Database.EnsureCreatedAsync();
            return fixture;
        }

        public IIntegrationService CreateService() => new IntegrationService(
            Db,
            SecretStore,
            new AllowAllAuthorizationService(),
            _validators);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class AllowAllAuthorizationService : IWorkspaceAuthorizationService
    {
        public Task<AuthenticationResult> AuthenticateAsync(ChannelCredential credential, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AuthorizationResult> AuthorizeAsync(ActorContext actor, WorkspaceId workspaceId, Permission action, CancellationToken ct = default) => Task.FromResult(AuthorizationResult.Authorized());
        public Task<ActorContext> BootstrapFirstAdministratorAsync(BootstrapRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeCredentialAsync(WorkspaceId workspaceId, MemberId actorPerformingRevocation, ApiTokenId credentialId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionIssuedResult> IssueSessionAsync(ActorContext actor, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        public string Protect(IReadOnlyDictionary<string, string> secrets) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(secrets)));

        public IReadOnlyDictionary<string, string> Unprotect(string protectedPayload) =>
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedPayload)))!;
    }

    private sealed class PassingValidator(IntegrationProvider provider) : IIntegrationValidator
    {
        public IntegrationProvider Provider { get; } = provider;
        public int Calls { get; private set; }

        public Task ValidateAsync(IReadOnlyDictionary<string, string> credentials, string settingsJson, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
