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

        // Best-effort flush of anything buffered at shutdown, without waiting on cancellation.
        await DrainAsync(CancellationToken.None);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        foreach (var change in buffer.Drain())
        {
            try
            {
                metrics.RecordPublicationLatency(change);
                await transport.SendAsync(change, ct);
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
