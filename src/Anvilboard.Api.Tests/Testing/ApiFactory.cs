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
