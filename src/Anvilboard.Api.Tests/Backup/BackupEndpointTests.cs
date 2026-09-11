using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anvilboard.Api.Tests.Testing;
using Anvilboard.Application.Backup;
using Anvilboard.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Anvilboard.Api.Tests.Backup;

/// <summary>
/// End-to-end coverage of `Anvilboard.Api/Endpoints/BackupEndpoints.cs` and
/// `Anvilboard.Api/Middleware/DatabaseOperationMiddleware.cs` through the real host
/// (`docs/plans/backup-and-restore.md` §9, §15), exercised via <see cref="ApiFactory"/> over a
/// throwaway SQLite database and backup directory.
/// </summary>
public sealed class BackupEndpointTests
{
    [Fact]
    public async Task List_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/backups", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_NonAdministrator_ReturnsForbidden()
    {
        await using var factory = new ApiFactory();
        using var bootstrapClient = factory.CreateClient();
        await ApiFactory.BootstrapAndGetSessionCookieAsync(bootstrapClient);

        using var memberClient = factory.CreateClient();
        var memberCookie = await factory.SeedMemberAndGetSessionCookieAsync(memberClient, "contributor", Role.Contributor);
        memberClient.DefaultRequestHeaders.Add("Cookie", memberCookie);

        var response = await memberClient.GetAsync("/api/backups", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("WORKSPACE_ACCESS_DENIED", payload.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Create_ThenList_ThenVerify_HappyPath()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));

        var createResponse = await client.PostAsync("/api/backups", content: null, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        using var createPayload = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync(CancellationToken.None));
        var backupId = createPayload.RootElement.GetProperty("backupId").GetGuid();
        Assert.True(createPayload.RootElement.GetProperty("sizeBytes").GetInt64() > 0);

        var listResponse = await client.GetAsync("/api/backups", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var listPayload = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(CancellationToken.None));
        var listedIds = listPayload.RootElement.EnumerateArray().Select(entry => entry.GetProperty("backupId").GetGuid()).ToList();
        Assert.Contains(backupId, listedIds);

        var verifyResponse = await client.PostAsync($"/api/backups/{backupId}/verify", content: null, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        using var verifyPayload = JsonDocument.Parse(await verifyResponse.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.True(verifyPayload.RootElement.GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task Verify_UnknownBackupId_ReturnsNotFound()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));

        var response = await client.PostAsync($"/api/backups/{Guid.NewGuid()}/verify", content: null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("REFERENCED_ENTITY_NOT_FOUND", payload.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Restore_WrongCaseSlugConfirmation_ReturnsValidationFailed()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client, workspaceSlug: "test-workspace"));

        var createResponse = await client.PostAsync("/api/backups", content: null, CancellationToken.None);
        using var createPayload = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync(CancellationToken.None));
        var backupId = createPayload.RootElement.GetProperty("backupId").GetGuid();

        // The real slug is "test-workspace" (lowercase); confirmation is an exact ordinal match
        // (plan §11.2 defense in depth), so a differently-cased value must be rejected before any
        // restore mechanics run.
        var response = await client.PostAsJsonAsync(
            $"/api/backups/{backupId}/restore", new { confirmedWorkspaceSlug = "TEST-WORKSPACE" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("VALIDATION_FAILED", payload.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task DatabaseOperationMiddleware_AdmissionClosed_ReturnsRateLimitedWithRetryAfter()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));

        var coordinator = factory.Services.GetRequiredService<IRestoreCoordinator>();
        var restoreLease = coordinator.TryBeginRestore();
        Assert.NotNull(restoreLease);

        try
        {
            // No active database-operation leases are outstanding on this coordinator (this test
            // never took one out through the middleware), so the drain completes immediately and
            // only closes admission for anything registered afterwards.
            await coordinator.CloseAdmissionAndDrainAsync(restoreLease!, TimeSpan.FromSeconds(5), CancellationToken.None);

            var response = await client.GetAsync("/api/backups", CancellationToken.None);

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfterValues));
            Assert.NotEmpty(retryAfterValues!);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
            Assert.Equal("RATE_LIMITED", payload.RootElement.GetProperty("title").GetString());
        }
        finally
        {
            coordinator.ReopenAdmission(restoreLease!);
            restoreLease!.Dispose();
        }

        // Admission reopened: the same route now succeeds again.
        var followUpResponse = await client.GetAsync("/api/backups", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, followUpResponse.StatusCode);
    }

    [Fact]
    public async Task ConcurrentRestoreAttempt_ReturnsRateLimitedWithRetryAfter()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client, workspaceSlug: "test-workspace"));

        var createResponse = await client.PostAsync("/api/backups", content: null, CancellationToken.None);
        using var createPayload = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync(CancellationToken.None));
        var backupId = createPayload.RootElement.GetProperty("backupId").GetGuid();

        // Simulate a restore that is already in progress, at the IRestoreCoordinator level, so the
        // service's own RestoreAsync precondition (not the HTTP middleware) is what rejects the
        // second attempt (plan §8.4, AC-012e).
        var coordinator = factory.Services.GetRequiredService<IRestoreCoordinator>();
        var inProgressLease = coordinator.TryBeginRestore();
        Assert.NotNull(inProgressLease);

        try
        {
            var response = await client.PostAsJsonAsync(
                $"/api/backups/{backupId}/restore", new { confirmedWorkspaceSlug = "test-workspace" }, CancellationToken.None);

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfterValues));
            Assert.NotEmpty(retryAfterValues!);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
            Assert.Equal("RATE_LIMITED", payload.RootElement.GetProperty("title").GetString());
        }
        finally
        {
            inProgressLease!.Dispose();
        }
    }
}
