using Anvilboard.Application.Realtime;
using Anvilboard.Domain;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anvilboard.Application.Tests.Realtime;

/// <summary>
/// Covers AC-RT-006: a plugin event reaches clients only when an operator approved its type, and
/// relaying never runs plugin lifecycle work.
/// </summary>
public sealed class PluginEventRelayTests
{
    private const string ApprovedEventType = "github.pull_request.merged";

    [Fact]
    public void Publish_ApprovedEventType_PublishesPluginEventChange()
    {
        var (relay, publisher) = CreateRelay(ApprovedEventType);
        var workspaceId = WorkspaceId.New();
        var issueId = IssueId.New();
        var occurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        relay.Publish(new PluginEvent(workspaceId, ApprovedEventType, issueId, occurredAt));

        var change = Assert.IsType<RealtimePluginEventChange>(Assert.Single(publisher.Published));
        Assert.Equal(workspaceId, change.WorkspaceId);
        Assert.Equal(ApprovedEventType, change.PluginEventType);
        Assert.Equal(issueId, change.IssueId);
        Assert.Equal(occurredAt, change.OccurredAt);
        Assert.Equal("plugin.event", change.EventType);
        Assert.False(string.IsNullOrWhiteSpace(change.CorrelationId));
    }

    [Fact]
    public void Publish_UnapprovedEventType_DropsEvent()
    {
        var (relay, publisher) = CreateRelay(ApprovedEventType);

        relay.Publish(new PluginEvent(WorkspaceId.New(), "github.pull_request.opened"));

        Assert.Empty(publisher.Published);
    }

    [Fact]
    public void Publish_NoApprovedEventTypesConfigured_DropsEverything()
    {
        var (relay, publisher) = CreateRelay();

        relay.Publish(new PluginEvent(WorkspaceId.New(), ApprovedEventType));

        Assert.Empty(publisher.Published);
    }

    [Fact]
    public void Publish_WithoutOccurredAt_StampsRelayTime()
    {
        var (relay, publisher) = CreateRelay(ApprovedEventType);
        var before = DateTimeOffset.UtcNow;

        relay.Publish(new PluginEvent(WorkspaceId.New(), ApprovedEventType));

        var change = Assert.Single(publisher.Published);
        Assert.InRange(change.OccurredAt, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Publish_PublisherThrows_SurfacesNothingToThePlugin()
    {
        var options = new RealtimeOptions { RelayedPluginEventTypes = [ApprovedEventType] };
        var relay = new PluginEventRelay(new ThrowingPublisher(), options, NullLogger<PluginEventRelay>.Instance);

        // The relay must never turn a realtime failure into a plugin-visible failure; the plugin's
        // own work has already committed by the time it publishes.
        var exception = Record.Exception(() => relay.Publish(new PluginEvent(WorkspaceId.New(), ApprovedEventType)));

        Assert.Null(exception);
    }

    [Fact]
    public void AddRealtime_PublicPluginPublisherIsDistinctFromTrustedPublisherButStillRelays()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAnvilboardApplication();
        services.AddAnvilboardRealtime(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        var publicPublisher = provider.GetRequiredService<IPluginEventPublisher>();
        var trustedPublisher = provider.GetRequiredService<ITrustedPluginEventPublisher>();

        // Enabling realtime must restore live delivery for plugins using the public contract
        // (AC-RT-006) rather than leaving IPluginEventPublisher on the NullPluginEventPublisher
        // default — but the type it resolves to must not be (or expose) the trusted,
        // workspace-authenticated capability itself.
        Assert.IsType<PublicPluginEventPublisher>(publicPublisher);
        Assert.IsType<PluginEventRelay>(trustedPublisher);
        Assert.False(publicPublisher is ITrustedPluginEventPublisher, "The public publisher must not also expose the trusted, workspace-authenticated capability.");
    }

    [Fact]
    public void AddRealtime_PublicPluginPublisher_RelaysApprovedEventsLikeTheTrustedPublisher()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAnvilboardApplication();
        services.AddAnvilboardRealtime(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Realtime:RelayedPluginEventTypes:0"] = ApprovedEventType,
            })
            .Build());
        services.RemoveAll<IRealtimeUpdatePublisher>();
        var recordingPublisher = new RecordingPublisher();
        services.AddSingleton<IRealtimeUpdatePublisher>(recordingPublisher);

        using var provider = services.BuildServiceProvider();
        var publicPublisher = provider.GetRequiredService<IPluginEventPublisher>();

        publicPublisher.Publish(new PluginEvent(WorkspaceId.New(), ApprovedEventType));

        Assert.Single(recordingPublisher.Published);
    }

    private static (PluginEventRelay Relay, RecordingPublisher Publisher) CreateRelay(params string[] approvedEventTypes)
    {
        var publisher = new RecordingPublisher();
        var options = new RealtimeOptions { RelayedPluginEventTypes = [.. approvedEventTypes] };
        return (new PluginEventRelay(publisher, options, NullLogger<PluginEventRelay>.Instance), publisher);
    }

    private sealed class RecordingPublisher : IRealtimeUpdatePublisher
    {
        public List<RealtimeChange> Published { get; } = [];

        public ValueTask PublishAsync(RealtimeChange change, CancellationToken ct = default)
        {
            Published.Add(change);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingPublisher : IRealtimeUpdatePublisher
    {
        public ValueTask PublishAsync(RealtimeChange change, CancellationToken ct = default) =>
            throw new InvalidOperationException("transport unavailable");
    }
}
