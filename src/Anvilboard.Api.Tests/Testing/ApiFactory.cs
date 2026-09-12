using System.Net;
using System.Net.Http.Json;
using Anvilboard.Application.Authorization;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Anvilboard.Api.Tests.Testing;

/// <summary>
/// Hosts the real API over a throwaway SQLite file so integration tests exercise the same startup,
/// middleware order, and migrations as production. Each instance gets its own database file, which
/// is what lets tests that bootstrap a workspace run independently of each other.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"anvilboard-api-tests-{Guid.NewGuid():N}.db");
    private readonly string backupDirectory = Path.Combine(Path.GetTempPath(), $"anvilboard-api-tests-backups-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> configurationOverrides;

    public ApiFactory(IReadOnlyDictionary<string, string?>? configurationOverrides = null) =>
        this.configurationOverrides = configurationOverrides is null
            ? []
            : new Dictionary<string, string?>(configurationOverrides);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(configuration =>
        {
            var settings = new Dictionary<string, string?>(configurationOverrides)
            {
                ["Database:DatabasePath"] = databasePath,
                ["Database:BackupDirectory"] = backupDirectory,
            };
            configuration.AddInMemoryCollection(settings);
        });
    }

    /// <summary>
    /// Runs the one-time bootstrap flow and returns the resulting session cookie, in the
    /// <c>name=value</c> form callers can pass straight to a <c>Cookie</c> header.
    /// </summary>
    public static async Task<string> BootstrapAndGetSessionCookieAsync(
        HttpClient client,
        string workspaceSlug = "test-workspace",
        string username = "admin")
    {
        var response = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            workspaceName = "Test workspace",
            workspaceSlug,
            administratorDisplayName = "Administrator",
            administratorUsername = username,
            administratorPassword = "correct horse battery staple",
            administratorEmail = $"{username}@example.test",
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        return setCookie.Split(';', 2)[0];
    }

    /// <summary>
    /// Seeds a second workspace with its own administrator directly through the DbContext, and
    /// returns that workspace's id. The bootstrap endpoint refuses to run twice, so tests that need
    /// two tenants on <em>one</em> host — the only way to prove cross-workspace isolation inside a
    /// single hub — cannot get the second one through the API.
    /// </summary>
    public async Task<WorkspaceId> SeedAdditionalWorkspaceAsync(
        string slug,
        string username,
        string password = "correct horse battery staple")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();

        var workspace = new Workspace
        {
            Id = WorkspaceId.New(),
            Name = slug,
            Slug = slug,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Workspaces.Add(workspace);
        db.Members.Add(new Member
        {
            Id = MemberId.New(),
            WorkspaceId = workspace.Id,
            DisplayName = username,
            Email = $"{username}@example.test",
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            Role = Role.Administrator,
        });

        await db.SaveChangesAsync();
        return workspace.Id;
    }

    /// <summary>
    /// Seeds a non-administrator member (default <see cref="Role.Contributor"/>) into the host's
    /// existing (first-bootstrapped) workspace directly through the DbContext, for tests that need
    /// to prove a permission is denied to a real session rather than merely absent from an
    /// unauthenticated request. Returns the session cookie in <c>name=value</c> form.
    /// </summary>
    public async Task<string> SeedMemberAndGetSessionCookieAsync(
        HttpClient client,
        string username,
        Role role = Role.Contributor,
        string password = "correct horse battery staple")
    {
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
            var workspaceId = await db.Workspaces.Select(workspace => workspace.Id).FirstAsync();

            db.Members.Add(new Member
            {
                Id = MemberId.New(),
                WorkspaceId = workspaceId,
                DisplayName = username,
                Email = $"{username}@example.test",
                Username = username,
                PasswordHash = PasswordHasher.Hash(password),
                Role = role,
            });
            await db.SaveChangesAsync();
        }

        return await LoginAndGetSessionCookieAsync(client, username, password);
    }

    /// <summary>
    /// Seeds a team and a single issue into <paramref name="workspaceId"/> directly through the
    /// DbContext, returning both ids. Cross-workspace isolation tests need a target that provably
    /// exists but belongs to somebody else, which no authenticated API surface will hand out.
    /// </summary>
    public async Task<(Guid TeamId, Guid IssueId)> SeedTeamWithIssueAsync(
        WorkspaceId workspaceId,
        string teamKey = "FOR",
        string issueTitle = "Foreign issue")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();

        var team = new Team
        {
            Id = TeamId.New(),
            WorkspaceId = workspaceId,
            Name = teamKey,
            Key = teamKey,
            NextIssueNumber = 2,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Teams.Add(team);

        var issue = new Issue
        {
            Id = IssueId.New(),
            TeamId = team.Id,
            Key = $"{teamKey}-1",
            Title = issueTitle,
            Status = IssueStatus.Backlog,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Issues.Add(issue);

        await db.SaveChangesAsync();
        return (team.Id.Value, issue.Id.Value);
    }

    /// <summary>
    /// Reads an issue straight from the database, bypassing every workspace guard, so a test can
    /// prove that a denied request also changed nothing.
    /// </summary>
    public async Task<Issue> ReadIssueAsync(Guid issueId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
        return await db.Issues.AsNoTracking().SingleAsync(issue => issue.Id == new IssueId(issueId));
    }

    /// <summary>
    /// Counts a team's issues, bypassing every workspace guard, so a test can prove that a denied
    /// create landed nowhere.
    /// </summary>
    public async Task<int> CountIssuesInTeamAsync(Guid teamId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
        return await db.Issues.CountAsync(issue => issue.TeamId == new TeamId(teamId));
    }

    /// <summary>
    /// Counts rows a denied write must not have created, bypassing every workspace guard.
    /// </summary>
    public async Task<(int Comments, int Artifacts, int Links)> CountChildRowsAsync(Guid issueId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
        var id = new IssueId(issueId);
        return (
            await db.Comments.CountAsync(comment => comment.IssueId == id),
            await db.Artifacts.CountAsync(artifact => artifact.IssueId == id),
            await db.IssueLinks.CountAsync(link => link.SourceIssueId == id || link.TargetIssueId == id));
    }

    /// <summary>
    /// Attaches a link between two issues straight through the DbContext. A delete-denial test needs
    /// a link that provably exists inside somebody else's workspace, which no scoped API will create.
    /// </summary>
    public async Task<Guid> SeedLinkAsync(Guid sourceIssueId, Guid targetIssueId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();

        var link = new IssueLink
        {
            Id = IssueLinkId.New(),
            SourceIssueId = new IssueId(sourceIssueId),
            TargetIssueId = new IssueId(targetIssueId),
            Type = "related",
            Description = "Foreign link",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.IssueLinks.Add(link);

        await db.SaveChangesAsync();
        return link.Id.Value;
    }

    /// <summary>
    /// Attaches an artifact straight through the DbContext, for the same reason as
    /// <see cref="SeedLinkAsync"/>: the delete-denial target must already exist elsewhere.
    /// </summary>
    public async Task<Guid> SeedArtifactAsync(Guid issueId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();

        var artifact = new Artifact
        {
            Id = ArtifactId.New(),
            IssueId = new IssueId(issueId),
            Kind = ArtifactKind.Link,
            Title = "Foreign artifact",
            ContentReference = "https://example.test/foreign",
            Source = "local",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Artifacts.Add(artifact);

        await db.SaveChangesAsync();
        return artifact.Id.Value;
    }

    /// <summary>
    /// Logs in with a username/password pair and returns the session cookie in <c>name=value</c> form.
    /// </summary>
    public static async Task<string> LoginAndGetSessionCookieAsync(
        HttpClient client,
        string username,
        string password = "correct horse battery staple")
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { username, password }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        return setCookie.Split(';', 2)[0];
    }

    public new ValueTask DisposeAsync()
    {
        Dispose();

        SqliteConnection.ClearAllPools();
        TryDeleteDatabaseFiles();
        TryDeleteBackupDirectory();
        return ValueTask.CompletedTask;
    }

    private void TryDeleteBackupDirectory()
    {
        try
        {
            if (Directory.Exists(backupDirectory))
            {
                Directory.Delete(backupDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private void TryDeleteDatabaseFiles()
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-shm", $"{databasePath}-wal" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }
}
