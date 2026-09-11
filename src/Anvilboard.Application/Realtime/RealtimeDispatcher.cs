using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anvilboard.Application.Realtime;

/// <summary>
/// Drains the bounded coalescing buffer on a background loop and hands each surviving change to the
/// configured <see cref="IRealtimeTransport"/>. Running here — rather than on the mutation thread —
/// is what keeps a slow or disconnected client from ever delaying a committed write.
/// </summary>
public sealed class RealtimeDispatcher(
    RealtimeChangeBuffer buffer,
    RealtimeDispatchSignal signal,
    IRealtimeTransport transport,
    RealtimeMetrics metrics,
    IOptions<RealtimeOptions> options,
    ILogger<RealtimeDispatcher> logger) : BackgroundService
{
    private readonly RealtimeOptions options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await signal.WaitAsync(stoppingToken);

                // Let the rest of a burst land before draining, so N rapid updates to one issue
                // become one send instead of N.
                if (options.DebounceWindow > TimeSpan.Zero)
                {
                    await Task.Delay(options.DebounceWindow, stoppingToken);
                }

                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Realtime dispatcher loop failed; continuing.");
            }
        }

        // Bound shutdown flushing so a stalled transport cannot keep the host alive indefinitely.
        using var flushDeadline = new CancellationTokenSource(options.ShutdownFlushTimeout);
        try
        {
            await DrainAsync(flushDeadline.Token);
        }
        catch (OperationCanceledException) when (flushDeadline.IsCancellationRequested)
        {
            logger.LogWarning("Realtime shutdown flush exceeded its {Timeout} deadline.", options.ShutdownFlushTimeout);
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        foreach (var change in buffer.Drain())
        {
            try
            {
                metrics.RecordPublicationLatency(change);
                using var sendDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sendDeadline.CancelAfter(options.SendTimeout);
                await transport.SendAsync(change, sendDeadline.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                metrics.RecordSendFailure(change.EventType);
                logger.LogWarning(
                    ex,
                    "Realtime transport failed to send {EventType} for workspace {WorkspaceId}; the committed mutation is unaffected.",
                    change.EventType,
                    change.WorkspaceId);
            }
        }
    }
}
