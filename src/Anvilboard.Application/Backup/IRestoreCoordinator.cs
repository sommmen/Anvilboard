namespace Anvilboard.Application.Backup;

/// <summary>
/// Singleton restore lock plus host-wide database-work admission and active-operation draining
/// (`docs/plans/backup-and-restore.md` §8.1, §8.4). A restore is exclusive across the whole
/// process: while one is in flight, a second attempt is rejected immediately, which the caller
/// maps to <see cref="BackupOperationException.RateLimited"/> (AC-012e). Once a restore has passed
/// every fail-closed validation check, it closes admission for new database-backed work and waits
/// for already-admitted operations to finish before the safety copy and file swap, so no request
/// observes a partially swapped or unmigrated database.
/// </summary>
public interface IRestoreCoordinator
{
    /// <summary>
    /// Attempts to begin an exclusive restore. Returns <see langword="null"/> if another restore
    /// is already in progress. The caller must dispose the returned lease exactly once, on both
    /// the success and the failure path, to release exclusivity.
    /// </summary>
    IRestoreLease? TryBeginRestore();

    /// <summary>
    /// Registers one database-backed operation (e.g. an incoming API request) with the drain
    /// mechanism. Returns <see langword="null"/> if admission is currently closed (a restore is
    /// draining or swapping), which the caller maps to <see cref="BackupOperationException.RateLimited"/>.
    /// The returned lease must be disposed when the operation completes.
    /// </summary>
    IDatabaseOperationLease? TryBeginDatabaseOperation();

    /// <summary>
    /// Closes admission for new <see cref="TryBeginDatabaseOperation"/> callers and waits for
    /// every already-admitted lease to complete, up to <paramref name="drainTimeout"/>. The
    /// database-operation lease (if any) held by the same logical caller as
    /// <paramref name="restoreLease"/> — i.e. the restore's own request — is exempted from the
    /// wait, since it would otherwise wait on itself. Only the holder of
    /// <paramref name="restoreLease"/> may call this. Throws <see cref="TimeoutException"/> if the
    /// drain does not complete in time.
    /// </summary>
    Task CloseAdmissionAndDrainAsync(IRestoreLease restoreLease, TimeSpan drainTimeout, CancellationToken ct = default);

    /// <summary>Reopens admission after a restore attempt (success or failure) is done.</summary>
    void ReopenAdmission(IRestoreLease restoreLease);
}

/// <summary>Exclusive restore token returned by <see cref="IRestoreCoordinator.TryBeginRestore"/>.</summary>
public interface IRestoreLease : IDisposable
{
}

/// <summary>
/// Token for one database-backed operation registered via
/// <see cref="IRestoreCoordinator.TryBeginDatabaseOperation"/>.
/// </summary>
public interface IDatabaseOperationLease : IDisposable
{
}
