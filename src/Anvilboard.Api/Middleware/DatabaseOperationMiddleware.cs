using System.Globalization;
using Anvilboard.Application.Backup;

namespace Anvilboard.Api.Middleware;

/// <summary>
/// Host-wide database-operation admission gate (`docs/plans/backup-and-restore.md` §8.1, §8.4).
/// Every REST request under <c>/api</c> registers a short-lived lease with
/// <see cref="IRestoreCoordinator.TryBeginDatabaseOperation"/> before its endpoint runs, and
/// releases it once the response is produced. While a restore has closed admission — draining
/// already-admitted leases before its safety copy and file swap — new leases are refused here and
/// mapped to <c>RATE_LIMITED</c> (429) with a <c>Retry-After</c> header, the same code
/// <see cref="BackupOperationException.RateLimited"/> uses for a concurrent restore attempt
/// rejected by <c>IRestoreCoordinator.TryBeginRestore</c>.
///
/// Deliberately scoped to <c>/api</c> rather than every request: the SignalR hub
/// (<c>/hubs/workspace</c>) upgrades matching requests to a long-lived WebSocket connection, and
/// holding a lease for that connection's entire lifetime would starve
/// <see cref="IRestoreCoordinator.CloseAdmissionAndDrainAsync"/> for as long as any client stays
/// connected. Static files and the SPA fallback route never resolve <c>AnvilboardDbContext</c> at
/// all, so they need no lease either.
///
/// The restore request itself is <em>not</em> special-cased here — <see cref="IRestoreCoordinator"/>
/// already solves that: it captures whichever database-operation lease is ambient (via
/// <c>AsyncLocal</c>) at the moment <c>BackupService.RestoreAsync</c> calls
/// <c>TryBeginRestore</c>, and <c>CloseAdmissionAndDrainAsync</c> excludes exactly that one lease
/// from its drain wait. So the lease this middleware takes out for the restore's own HTTP request
/// flows down to become the exempted lease, and the request does not deadlock waiting on itself
/// (plan §8.4: "the restore request is marked as the coordinator owner so it does not lease
/// itself").
/// </summary>
public sealed class DatabaseOperationMiddleware(RequestDelegate next)
{
    /// <summary>Seconds suggested to a rate-limited caller before retrying. Shared with the
    /// concurrent-restore-attempt case surfaced by <c>Anvilboard.Api.Endpoints.BackupEndpoints</c>
    /// so both admission-control paths advertise the same retry guidance.</summary>
    public const int RetryAfterSeconds = 5;

    public async Task InvokeAsync(HttpContext context, IRestoreCoordinator coordinator)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        using var lease = coordinator.TryBeginDatabaseOperation();
        if (lease is null)
        {
            context.Response.Headers["Retry-After"] = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            await Results.Problem(title: "RATE_LIMITED", statusCode: StatusCodes.Status429TooManyRequests).ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
