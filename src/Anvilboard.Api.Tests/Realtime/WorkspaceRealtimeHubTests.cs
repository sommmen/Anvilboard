using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Anvilboard.Api.Realtime;
using Anvilboard.Api.Tests.Testing;
using Anvilboard.Integrations.GitHub;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Anvilboard.Api.Tests.Realtime;

/// <summary>
/// Covers TC-RT-002/TC-RT-005: only an authenticated, authorized client may hold a realtime
/// connection, and a committed mutation reaches that client's workspace group.
/// </summary>
public sealed class WorkspaceRealtimeHubTests
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Connect_WithoutCredential_IsRejected()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await ApiFactory.BootstrapAndGetSessionCookieAsync(client);

        await using var connection = BuildConnection(factory, sessionCookie: null);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Connect_WithInvalidCredential_IsRejected()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await ApiFactory.BootstrapAndGetSessionCookieAsync(client);

        await using var connection = BuildConnection(factory, "anvilboard_session=not-a-real-token");

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Connect_WithValidSessionCookie_Succeeds()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var cookie = await ApiFactory.BootstrapAndGetSessionCookieAsync(client);

        await using var connection = BuildConnection(factory, cookie);
        await connection.StartAsync(CancellationToken.None);

        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task CommittedIssueMutation_ReachesTheConnectedWorkspaceClient()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var cookie = await ApiFactory.BootstrapAndGetSessionCookieAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var teamId = await CreateTeamAsync(client);

        await using var connection = BuildConnection(factory, cookie);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(WorkspaceRealtimeHub.ChangeMethodName, envelope =>
        {
            if (envelope.GetProperty("eventType").GetString() == "issue.changed")
            {
                received.TrySetResult(envelope);
            }
        });
        await connection.StartAsync(CancellationToken.None);

        var createResponse = await client.PostAsJsonAsync(
            "/api/issues", new { teamId, title = "Realtime issue" }, CancellationToken.None);
        createResponse.EnsureSuccessStatusCode();

        var envelope = await received.Task.WaitAsync(ReceiveTimeout, CancellationToken.None);
        Assert.Equal("CREATED", envelope.GetProperty("changeKind").GetString());
        Assert.NotEqual(Guid.Empty, envelope.GetProperty("issueId").GetGuid());
        // A client is only ever in its own workspace group, so the envelope must not leak the id.
        Assert.False(envelope.TryGetProperty("workspaceId", out _));
    }

    [Fact]
    public async Task CommittedMutation_IsNotDeliveredToADifferentWorkspacesClient()
    {
        // Both workspaces must live on the SAME host: with two hosts the observer's hub is a
        // different instance that the mutating host could never reach anyway, so the assertion
        // would hold even if the transport broadcast to every connected client.
        await using var factory = new ApiFactory();

        using var mutatingClient = factory.CreateClient();
        var mutatingCookie = await ApiFactory.BootstrapAndGetSessionCookieAsync(mutatingClient, "workspace-a");
        mutatingClient.DefaultRequestHeaders.Add("Cookie", mutatingCookie);
        var teamId = await CreateTeamAsync(mutatingClient);

        await factory.SeedAdditionalWorkspaceAsync("workspace-b", "admin-b");
        using var observingClient = factory.CreateClient();
        var observingCookie = await ApiFactory.LoginAndGetSessionCookieAsync(observingClient, "admin-b");

        await using var observer = BuildConnection(factory, observingCookie);
        var observed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.On<JsonElement>(WorkspaceRealtimeHub.ChangeMethodName, envelope => observed.TrySetResult(envelope));
        await observer.StartAsync(CancellationToken.None);

        // Control: a client in the mutating workspace must actually receive the broadcast, which is
        // what makes the observer's silence below evidence of scoping rather than of a dead pipeline.
        await using var insider = BuildConnection(factory, mutatingCookie);
        var insiderReceived = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        insider.On<JsonElement>(WorkspaceRealtimeHub.ChangeMethodName, envelope => insiderReceived.TrySetResult(envelope));
        await insider.StartAsync(CancellationToken.None);

        var createResponse = await mutatingClient.PostAsJsonAsync(
            "/api/issues", new { teamId, title = "Not yours" }, CancellationToken.None);
        createResponse.EnsureSuccessStatusCode();

        await insiderReceived.Task.WaitAsync(ReceiveTimeout, CancellationToken.None);

        var completed = await Task.WhenAny(observed.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.NotSame(observed.Task, completed);
    }

    [Fact]
    public async Task SlowClient_DoesNotDelayTheMutationResponse()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var cookie = await ApiFactory.BootstrapAndGetSessionCookieAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var teamId = await CreateTeamAsync(client);

        await using var connection = BuildConnection(factory, cookie);
        // A handler that never completes is the cleanest available stand-in for a client that has
        // stopped draining its connection.
        connection.On<JsonElement>(
            WorkspaceRealtimeHub.ChangeMethodName, _ => Task.Delay(Timeout.Infinite, CancellationToken.None));
        await connection.StartAsync(CancellationToken.None);

        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/issues", new { teamId, title = $"Burst {i}" }, CancellationToken.None);
            response.EnsureSuccessStatusCode();
        }

        started.Stop();
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(10),
            $"Mutations took {started.Elapsed} with a stalled realtime client attached.");
    }

    [Fact]
    public async Task ApprovedPluginEvent_FromAWebhook_ReachesTheConnectedWorkspaceClient()
    {
        const string mergedPullRequestBody = """{"action":"closed","pull_request":{"merged":true}}""";

        await using var factory = new ApiFactory(new Dictionary<string, string?>
        {
            ["Realtime:RelayedPluginEventTypes:0"] = GitHubWebhookReceiver.PullRequestMergedEventType,
        });
        using var client = factory.CreateClient();
        var cookie = await ApiFactory.BootstrapAndGetSessionCookieAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var teamResponse = await client.PostAsJsonAsync("/api/teams", new { name = "Engineering", key = "ENG" });
        teamResponse.EnsureSuccessStatusCode();

        await using var connection = BuildConnection(factory, cookie);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(WorkspaceRealtimeHub.ChangeMethodName, envelope =>
        {
            if (envelope.GetProperty("eventType").GetString() == "plugin.event")
            {
                received.TrySetResult(envelope);
            }
        });
        await connection.StartAsync(CancellationToken.None);

        client.DefaultRequestHeaders.Add("X-GitHub-Event", "pull_request");
        var webhookResponse = await client.PostAsync(
            "/webhooks/github",
            new StringContent(mergedPullRequestBody, Encoding.UTF8, "application/json"),
            CancellationToken.None);
        webhookResponse.EnsureSuccessStatusCode();

        var envelope = await received.Task.WaitAsync(ReceiveTimeout, CancellationToken.None);
        Assert.Equal(
            GitHubWebhookReceiver.PullRequestMergedEventType,
            envelope.GetProperty("pluginEventType").GetString());
    }

    [Fact]
    public async Task UnapprovedPluginEvent_FromAWebhook_ReachesNoClient()
    {
        const string mergedPullRequestBody = """{"action":"closed","pull_request":{"merged":true}}""";

        // No Realtime:RelayedPluginEventTypes entry: the plugin still reports the event, but an
        // operator never approved it, so it must not reach a browser (AC-RT-006).
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var cookie = await ApiFactory.BootstrapAndGetSessionCookieAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var teamResponse = await client.PostAsJsonAsync("/api/teams", new { name = "Engineering", key = "ENG" });
        teamResponse.EnsureSuccessStatusCode();

        await using var connection = BuildConnection(factory, cookie);
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JsonElement>(WorkspaceRealtimeHub.ChangeMethodName, envelope => received.TrySetResult(envelope));
        await connection.StartAsync(CancellationToken.None);

        client.DefaultRequestHeaders.Add("X-GitHub-Event", "pull_request");
        var webhookResponse = await client.PostAsync(
            "/webhooks/github",
            new StringContent(mergedPullRequestBody, Encoding.UTF8, "application/json"),
            CancellationToken.None);
        webhookResponse.EnsureSuccessStatusCode();

        var delivered = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.NotSame(received.Task, delivered);
    }

    private static async Task<Guid> CreateTeamAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/teams", new { name = "Realtime team", key = "RT" }, CancellationToken.None);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return payload.RootElement.GetProperty("id").GetGuid();
    }

    private static HubConnection BuildConnection(ApiFactory factory, string? sessionCookie) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress, WorkspaceRealtimeHub.HubPath.TrimStart('/')),
                options =>
                {
                    // TestServer has no real socket, so SignalR must be routed through its handler
                    // and pinned to long polling rather than negotiating a WebSocket.
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                    if (sessionCookie is not null)
                    {
                        options.Headers["Cookie"] = sessionCookie;
                    }
                })
            .Build();
}

