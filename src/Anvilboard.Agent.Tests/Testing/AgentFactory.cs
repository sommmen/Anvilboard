using System.Text.Json;
using System.Text.Json.Serialization;
using Anvilboard.Agent.Authorization;
using Anvilboard.Agent.Automation;
using Anvilboard.Agent.Hosting;
using Anvilboard.Application;
using Anvilboard.Application.Authorization;
using Anvilboard.Domain;
using Anvilboard.Domain.Serialization;
using Anvilboard.Infrastructure;
using Anvilboard.Infrastructure.Persistence;
using DotNetAgentSurface.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Anvilboard.Agent.Tests.Testing;

/// <summary>
/// Boots the agent host's real DI graph, catalog, and invoker against a throwaway SQLite file so
/// authorization tests exercise the same wiring the shipped host uses.
/// </summary>
/// <remarks>
/// Deliberately not a mock: the property under test is "a denied invocation never reaches an
/// application service", which is only observable against a real container and a real database.
/// </remarks>
public sealed class AgentFactory : IAsyncDisposable
{
    private readonly string databasePath =
        Path.Combine(Path.GetTempPath(), $"anvilboard-agent-tests-{Guid.NewGuid():N}.db");

    private readonly string backupDirectory =
        Path.Combine(Path.GetTempPath(), $"anvilboard-agent-tests-backups-{Guid.NewGuid():N}");

    private readonly ServiceProvider provider;
    private readonly AgentOptions options = new();

    public AgentFactory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:DatabasePath"] = databasePath,
                ["Database:BackupDirectory"] = backupDirectory,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAnvilboardInfrastructure(configuration);
        services.AddAnvilboardApplication();
        services.AddScoped<BoardAgentService>();
        services.AddSingleton(options);
        services.AddSingleton<AgentCredentialSource>();
        services.AddScoped<AgentActorAccessor>();
        services.AddScoped<AgentWorkspaceScope>();
        services.AddScoped<AgentIdempotency>();

        provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        JsonOptions.Converters.Add(new StronglyTypedIdJsonConverterFactory());
        JsonOptions.Converters.Add(new JsonStringEnumConverter());

        Catalog = OperationCatalog.Discover(typeof(BoardAgentService));
        Invoker = new OperationInvoker(
            new AgentInvocationTestServiceProvider(),
            JsonOptions,
            [new WorkspaceAuthorizationPolicy()]);
    }

    public OperationCatalog Catalog { get; }

    public OperationInvoker Invoker { get; }

    /// <summary>The serializer options the host renders operation results with.</summary>
    public JsonSerializerOptions JsonOptions { get; }

    /// <summary>Renders an invocation result the way the CLI renderer would put it on the wire.</summary>
    public JsonElement Render(OperationInvocationResult result) =>
        JsonSerializer.SerializeToElement(result.Value, JsonOptions);

    /// <summary>Sets the API token the host presents, mirroring <c>ANVILBOARD_AGENT__APITOKEN</c>.</summary>
    public void UseApiToken(string? apiToken) => options.ApiToken = apiToken;

    public async Task InitializeAsync()
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// Invokes an operation exactly as the CLI host does: inside one invocation scope, disposed
    /// when the invocation completes.
    /// </summary>
    public async Task<OperationInvocationResult> InvokeAsync(
        string operationName,
        IReadOnlyDictionary<string, JsonElement>? inputs = null)
    {
        var operation = Catalog.Operations.Single(o => o.Name == operationName);

        using (AgentInvocationScope.Begin(provider))
        {
            return await Invoker.InvokeAsync(operation, inputs);
        }
    }

    /// <summary>Builds operation inputs from plain CLR values.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Inputs(params (string Name, object? Value)[] values)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var (name, value) in values)
        {
            result[name] = JsonSerializer.SerializeToElement(value);
        }

        return result;
    }

    /// <summary>
    /// Creates a workspace with an administrator and a team, ready to accept issues.
    /// </summary>
    /// <remarks>
    /// The first call runs the production bootstrap path so the seeded workspace is shaped exactly
    /// like a real one. Bootstrap is deliberately once-per-database, so cross-workspace isolation
    /// tests that need a second workspace fall through to direct seeding.
    /// </remarks>
    public async Task<SeededWorkspace> BootstrapWorkspaceAsync(string slug = "test-workspace")
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();

        WorkspaceId workspaceId;
        MemberId administratorId;

        if (await db.Workspaces.AnyAsync())
        {
            (workspaceId, administratorId) = await SeedWorkspaceDirectlyAsync(db, slug);
        }
        else
        {
            var authorization = scope.ServiceProvider.GetRequiredService<IWorkspaceAuthorizationService>();
            var actor = await authorization.BootstrapFirstAdministratorAsync(new BootstrapRequest(
                WorkspaceName: slug,
                WorkspaceSlug: slug,
                AdministratorDisplayName: "Administrator",
                AdministratorUsername: $"{slug}-admin",
                AdministratorPassword: "correct horse battery staple",
                AdministratorEmail: $"{slug}-admin@example.test"));

            (workspaceId, administratorId) = (actor.WorkspaceId, actor.MemberId);
        }

        var team = new Team
        {
            Id = TeamId.New(),
            WorkspaceId = workspaceId,
            Name = $"{slug} team",
            Key = slug[..Math.Min(3, slug.Length)].ToUpperInvariant(),
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync();

        return new SeededWorkspace(workspaceId, administratorId, team.Id);
    }

    private static async Task<(WorkspaceId WorkspaceId, MemberId AdministratorId)> SeedWorkspaceDirectlyAsync(
        AnvilboardDbContext db,
        string slug)
    {
        var workspace = new Workspace
        {
            Id = WorkspaceId.New(),
            Name = slug,
            Slug = slug,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Workspaces.Add(workspace);

        var administrator = new Member
        {
            Id = MemberId.New(),
            WorkspaceId = workspace.Id,
            DisplayName = "Administrator",
            Email = $"{slug}-admin@example.test",
            Username = $"{slug}-admin",
            Role = Role.Administrator,
        };
        db.Members.Add(administrator);

        // Issue creation resolves an initial workflow state, so a workspace without states cannot
        // accept issues. Bootstrap seeds these; a directly seeded workspace must do the same.
        db.WorkflowStates.AddRange(
            new WorkflowState { Id = WorkflowStateId.New(), WorkspaceId = workspace.Id, Key = "backlog", DisplayName = "Backlog", Order = 0 },
            new WorkflowState { Id = WorkflowStateId.New(), WorkspaceId = workspace.Id, Key = "todo", DisplayName = "Todo", Order = 1 },
            new WorkflowState { Id = WorkflowStateId.New(), WorkspaceId = workspace.Id, Key = "done", DisplayName = "Done", Order = 2, IsTerminal = true });

        await db.SaveChangesAsync();
        return (workspace.Id, administrator.Id);
    }

    /// <summary>
    /// Mints a raw API token for a new member with the given role and grants, returning the
    /// plaintext token the host would be configured with.
    /// </summary>
    public async Task<string> IssueApiTokenAsync(
        WorkspaceId workspaceId,
        Role role,
        IReadOnlyList<Permission>? grantedPermissions = null,
        DateTimeOffset? expiresAt = null)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();

        var memberName = $"agent-{Guid.NewGuid():N}";
        var member = new Member
        {
            Id = MemberId.New(),
            WorkspaceId = workspaceId,
            DisplayName = memberName,
            Email = $"{memberName}@example.test",
            Username = memberName,
            IsAgent = true,
            Role = role,
        };
        db.Members.Add(member);

        var rawToken = $"tok-{Guid.NewGuid():N}";
        db.ApiTokens.Add(new ApiToken
        {
            Id = ApiTokenId.New(),
            WorkspaceId = workspaceId,
            MemberId = member.Id,
            TokenHash = TokenHasher.Hash(rawToken),
            GrantedPermissions = grantedPermissions ?? [.. RolePermissionMap.PermissionsFor(role)],
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
        });

        await db.SaveChangesAsync();
        return rawToken;
    }

    /// <summary>Runs an assertion against the database, outside any invocation scope.</summary>
    public async Task WithDbAsync(Func<AnvilboardDbContext, Task> assertion)
    {
        using var scope = provider.CreateScope();
        await assertion(scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>());
    }

    public async Task<T> WithDbAsync<T>(Func<AnvilboardDbContext, Task<T>> query)
    {
        using var scope = provider.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        await provider.DisposeAsync();

        // SQLite pools the underlying file handle, so the file stays locked until pools are
        // cleared; without this the temp files accumulate on Windows.
        SqliteConnection.ClearAllPools();
        TryDelete(databasePath);
        TryDelete($"{databasePath}-shm");
        TryDelete($"{databasePath}-wal");

        try
        {
            if (Directory.Exists(backupDirectory))
            {
                Directory.Delete(backupDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // See DisposeAsync.
        }
    }

    private sealed class AgentInvocationTestServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => AgentInvocationScope.Services.GetService(serviceType);
    }
}

public sealed record SeededWorkspace(WorkspaceId WorkspaceId, MemberId AdministratorId, TeamId TeamId);
