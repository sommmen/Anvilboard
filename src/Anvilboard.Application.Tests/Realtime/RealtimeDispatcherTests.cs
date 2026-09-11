using System.Diagnostics.Metrics;
using Anvilboard.Application.Realtime;
using Anvilboard.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Application.Tests.Realtime;

public sealed class RealtimeDispatcherTests
{
    [Fact]
    public async Task StalledSend_TimesOutAndContinuesToLaterChanges()
    {
        var buffer = new RealtimeChangeBuffer(8);
        using var signal = new RealtimeDispatchSignal();
        var transport = new FirstSendStallsTransport();
        using var dispatcher = CreateDispatcher(buffer, signal, transport, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1));

        await dispatcher.StartAsync(CancellationToken.None);
        buffer.Offer(Change());
        buffer.Offer(Change());
        signal.Signal();

        await transport.SecondSend.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_StalledFlush_RespectsShutdownDeadline()
    {
        var buffer = new RealtimeChangeBuffer(8);
        using var signal = new RealtimeDispatchSignal();
        var transport = new AlwaysStallsTransport();
        using var dispatcher = CreateDispatcher(buffer, signal, transport, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(75));

        buffer.Offer(Change());
        var started = System.Diagnostics.Stopwatch.StartNew();
        await dispatcher.StartAsync(CancellationToken.None);
        await dispatcher.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        started.Stop();

        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private static RealtimeDispatcher CreateDispatcher(
        RealtimeChangeBuffer buffer,
        RealtimeDispatchSignal signal,
        IRealtimeTransport transport,
        TimeSpan sendTimeout,
        TimeSpan shutdownFlushTimeout)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        var provider = services.BuildServiceProvider();
        var metrics = new RealtimeMetrics(provider.GetRequiredService<IMeterFactory>());
        return new RealtimeDispatcher(
            buffer,
            signal,
            transport,
            metrics,
            Options.Create(new RealtimeOptions
            {
                DebounceWindow = TimeSpan.Zero,
                SendTimeout = sendTimeout,
                ShutdownFlushTimeout = shutdownFlushTimeout,
            }),
            NullLogger<RealtimeDispatcher>.Instance);
    }

    private static RealtimeIssueChange Change() => new(
        WorkspaceId.New(),
        IssueId.New(),
        0,
        RealtimeIssueChangeKind.Created,
        Guid.NewGuid().ToString("N"),
        DateTimeOffset.UtcNow);

    private sealed class FirstSendStallsTransport : IRealtimeTransport
    {
        private int sendCount;

        public TaskCompletionSource SecondSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendAsync(RealtimeChange change, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return;
            }

            SecondSend.TrySetResult();
        }
    }

    private sealed class AlwaysStallsTransport : IRealtimeTransport
    {
        public async Task SendAsync(RealtimeChange change, CancellationToken ct = default) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }
}
