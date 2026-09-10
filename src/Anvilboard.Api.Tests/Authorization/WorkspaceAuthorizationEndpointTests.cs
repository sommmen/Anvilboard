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
    public async Task Credentials_List_ReturnsOnlyWorkspaceCredentialMetadata()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await BootstrapAndGetSessionCookieAsync(client));

        var response = await client.GetAsync("/api/auth/credentials", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        var credential = Assert.Single(payload.RootElement.EnumerateArray());
        Assert.True(credential.TryGetProperty("id", out _));
        Assert.True(credential.TryGetProperty("memberId", out _));
        Assert.True(credential.TryGetProperty("grantedPermissions", out _));
        Assert.True(credential.TryGetProperty("createdAt", out _));
        Assert.False(credential.TryGetProperty("tokenHash", out _));
        Assert.False(credential.TryGetProperty("rawToken", out _));
    }

    [Fact]
    public async Task Credentials_Revoke_InvalidatesCredentialOnNextRequest()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await BootstrapAndGetSessionCookieAsync(client));

        var credentialsResponse = await client.GetAsync("/api/auth/credentials", CancellationToken.None);
        using var credentialsPayload = JsonDocument.Parse(await credentialsResponse.Content.ReadAsStringAsync(CancellationToken.None));
        var credentialId = Assert.Single(credentialsPayload.RootElement.EnumerateArray()).GetProperty("id").GetGuid();

        var revokeResponse = await client.DeleteAsync($"/api/auth/credentials/{credentialId}", CancellationToken.None);
        var replayResponse = await client.GetAsync("/api/auth/credentials", CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
        using var replayPayload = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("CREDENTIAL_INVALID_OR_EXPIRED", replayPayload.RootElement.GetProperty("title").GetString());
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

    private static async Task<string> BootstrapAndGetSessionCookieAsync(HttpClient client)
    {
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
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        return setCookie.Split(';', 2)[0];
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
