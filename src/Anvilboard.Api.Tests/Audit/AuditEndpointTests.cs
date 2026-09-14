using System.Net;
using System.Text.Json;
using Anvilboard.Api.Tests.Testing;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Anvilboard.Api.Tests.Audit;

/// <summary>
/// Exercises the audit history read path. Audit rows were write-only before this change: every
/// authorization decision was recorded and none of it was reachable from any surface (MAJ-018).
/// </summary>
public sealed class AuditEndpointTests
{
    [Fact]
    public async Task Query_AfterAuthorizedRequests_ReturnsRecordedEvents()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/audit-events", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadEventsAsync(response);

        // The request above is itself an authorized action, so the trail cannot be empty.
        Assert.NotEmpty(events);
        Assert.All(events, entry => Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("actorId").GetString())));
    }

    [Fact]
    public async Task Query_WithSeededHistory_ReturnsNewestFirst()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        await SeedAuditEventsAsync(factory);

        var response = await client.GetAsync(
            "/api/audit-events?targetType=seeded-target",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadEventsAsync(response);

        Assert.Equal(3, events.Count);
        Assert.Equal(
            new[] { "seed.third", "seed.second", "seed.first" },
            events.Select(entry => entry.GetProperty("action").GetString() ?? string.Empty).ToArray());

        var timestamps = events.Select(entry => entry.GetProperty("occurredAt").GetDateTimeOffset()).ToList();
        Assert.Equal(timestamps.OrderByDescending(value => value).ToList(), timestamps);
    }

    [Fact]
    public async Task Query_FilteredByActor_NarrowsResultSet()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        await SeedAuditEventsAsync(factory);

        var response = await client.GetAsync(
            "/api/audit-events?actorId=agent:seeded-two",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadEventsAsync(response);

        var entry = Assert.Single(events);
        Assert.Equal("seed.second", entry.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Query_FilteredByChannel_NarrowsResultSet()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        await SeedAuditEventsAsync(factory);

        var response = await client.GetAsync(
            "/api/audit-events?targetType=seeded-target&channel=Cli",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadEventsAsync(response);

        var entry = Assert.Single(events);
        Assert.Equal("seed.third", entry.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Query_UnknownChannel_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/audit-events?channel=telepathy", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Query_LimitAboveMaximum_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/audit-events?limit=99999", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Query_MalformedCursor_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/audit-events?cursor=not-a-cursor", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Query_InvertedTimeRange_ReturnsBadRequest()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync(
            "/api/audit-events?occurredAfter=2026-01-02T00:00:00Z&occurredBefore=2026-01-01T00:00:00Z",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Query_LimitOne_PagesThroughCursor()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);
        await SeedAuditEventsAsync(factory);

        var first = await client.GetAsync(
            "/api/audit-events?targetType=seeded-target&limit=1",
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstPayload = JsonDocument.Parse(await first.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.True(firstPayload.RootElement.GetProperty("hasMore").GetBoolean());
        var cursor = firstPayload.RootElement.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));
        Assert.Equal(
            "seed.third",
            Assert.Single(firstPayload.RootElement.GetProperty("events").EnumerateArray())
                .GetProperty("action").GetString());

        var second = await client.GetAsync(
            $"/api/audit-events?targetType=seeded-target&limit=1&cursor={Uri.EscapeDataString(cursor!)}",
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondEvents = await ReadEventsAsync(second);
        Assert.Equal("seed.second", Assert.Single(secondEvents).GetProperty("action").GetString());
    }

    [Fact]
    public async Task Query_EchoesAppliedDefaultLimit()
    {
        await using var factory = new ApiFactory();
        using var client = await CreateAuthenticatedClientAsync(factory);

        var response = await client.GetAsync("/api/audit-events", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(50, payload.RootElement.GetProperty("appliedQuery").GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Query_Unauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/audit-events", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Query_WithoutReadAuditPermission_ReturnsForbidden()
    {
        await using var factory = new ApiFactory();
        using var bootstrapClient = factory.CreateClient();
        await ApiFactory.BootstrapAndGetSessionCookieAsync(bootstrapClient);

        using var memberClient = factory.CreateClient();
        var memberCookie = await factory.SeedMemberAndGetSessionCookieAsync(memberClient, "contributor", Role.Contributor);
        memberClient.DefaultRequestHeaders.Add("Cookie", memberCookie);

        var response = await memberClient.GetAsync("/api/audit-events", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Rows are seeded straight through the DbContext because the only audit writes a REST test can
    /// provoke are authorization decisions, which all share one actor, action, and target — too
    /// uniform to prove that filtering and ordering actually discriminate.
    /// </summary>
    private static async Task SeedAuditEventsAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnvilboardDbContext>();
        var workspaceId = await db.Workspaces.Select(workspace => workspace.Id).FirstAsync();

        var baseline = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        db.AuditEvents.AddRange(
            NewEvent(workspaceId, "agent:seeded-one", AuditChannel.Rest, "seed.first", baseline),
            NewEvent(workspaceId, "agent:seeded-two", AuditChannel.Rest, "seed.second", baseline.AddMinutes(1)),
            NewEvent(workspaceId, "agent:seeded-three", AuditChannel.Cli, "seed.third", baseline.AddMinutes(2)));

        await db.SaveChangesAsync();
    }

    private static AuditEvent NewEvent(
        WorkspaceId workspaceId,
        string actorId,
        AuditChannel channel,
        string action,
        DateTimeOffset occurredAt) => new()
        {
            Id = AuditEventId.New(),
            WorkspaceId = workspaceId,
            ActorId = actorId,
            Channel = channel,
            Action = action,
            TargetType = "seeded-target",
            TargetId = "seeded-id",
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = occurredAt,
            ResultSummary = "OK",
        };

    private static async Task<List<JsonElement>> ReadEventsAsync(HttpResponseMessage response)
    {
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return payload.RootElement.GetProperty("events").EnumerateArray().ToList();
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", await ApiFactory.BootstrapAndGetSessionCookieAsync(client));
        return client;
    }
}
