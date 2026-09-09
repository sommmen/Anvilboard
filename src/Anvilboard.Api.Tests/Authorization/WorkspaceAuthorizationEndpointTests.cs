using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Anvilboard.Api.Tests.Authorization;

public sealed class WorkspaceAuthorizationEndpointTests
{
    [Fact]
    public async Task Bootstrap_FirstAdministrator_ReturnsAdministratorSession()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            workspaceName = "Test workspace",
            workspaceSlug = "test-workspace",
            administratorDisplayName = "Administrator",
            administratorUsername = "admin",
            administratorPassword = "correct horse battery staple",
            administratorEmail = "admin@example.test",
        }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("anvilboard_session=", response.Headers.GetValues("Set-Cookie").Single());

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("ADMINISTRATOR", payload.RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Bootstrap_AfterWorkspaceExists_ReturnsValidationFailure()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var request = new
        {
            workspaceName = "Test workspace",
            workspaceSlug = "test-workspace",
            administratorDisplayName = "Administrator",
            administratorUsername = "admin",
            administratorPassword = "correct horse battery staple",
        };

        _ = await client.PostAsJsonAsync("/api/auth/bootstrap", request, CancellationToken.None);
        var response = await client.PostAsJsonAsync("/api/auth/bootstrap", request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("VALIDATION_FAILED", payload.RootElement.GetProperty("title").GetString());
    }

    private sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"anvilboard-api-tests-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DatabasePath"] = databasePath,
                }));
        }

        public new ValueTask DisposeAsync()
        {
            Dispose();

            SqliteConnection.ClearAllPools();
            TryDeleteDatabaseFiles();
            return ValueTask.CompletedTask;
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
}
