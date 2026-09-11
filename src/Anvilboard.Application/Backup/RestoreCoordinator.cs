namespace Anvilboard.Application.Backup;

/// <summary>
/// Process-wide singleton implementation of <see cref="IRestoreCoordinator"/>
/// (`docs/plans/backup-and-restore.md` §8.1, §8.4). Uses a plain lock plus a short polling loop
/// for the drain wait rather than a condition variable: restores are rare and the drain target is
/// well under a minute (plan §12 restore mechanical step &lt; 60s), so the small added latency of
/// polling is immaterial and it keeps the implementation easy to reason about and test
/// deterministically.
/// </summary>
public sealed class RestoreCoordinator : IRestoreCoordinator
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Ambient reference to the database-operation lease (if any) already held by the current
    /// logical call chain. A future request-scoped admission gate (e.g. an ASP.NET Core
    /// middleware wrapping every request) would call <see cref="TryBeginDatabaseOperation"/>
    /// before routing reaches <c>IBackupService.RestoreAsync</c>; <see cref="TryBeginRestore"/>
    /// captures whatever lease is ambient at that point so the restore's own request is exempted
    /// from waiting on itself during <see cref="CloseAdmissionAndDrainAsync"/>, without exempting
    /// unrelated concurrent requests (plan §8.4: "the restore request is marked as the coordinator
    /// owner so it does not lease itself").
    /// </summary>
    private static readonly AsyncLocal<DatabaseOperationLease?> CurrentLease = new();

    private readonly object _gate = new();
    private readonly HashSet<DatabaseOperationLease> _activeOperations = [];
    private bool _restoreInProgress;
    private bool _admissionOpen = true;

    public IRestoreLease? TryBeginRestore()
    {
        lock (_gate)
        {
            if (_restoreInProgress)
            {
                return null;
            }

            _restoreInProgress = true;
        }

        return new RestoreLease(this, CurrentLease.Value);
    }

    public IDatabaseOperationLease? TryBeginDatabaseOperation()
    {
        lock (_gate)
        {
            if (!_admissionOpen)
            {
                return null;
            }

            var lease = new DatabaseOperationLease(this);
            _activeOperations.Add(lease);
            CurrentLease.Value = lease;
            return lease;
        }
    }

    public async Task CloseAdmissionAndDrainAsync(IRestoreLease restoreLease, TimeSpan drainTimeout, CancellationToken ct = default)
    {
        var owned = AsOwned(restoreLease);

        lock (_gate)
        {
            _admissionOpen = false;
        }

        var deadline = DateTime.UtcNow + drainTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int remaining;
            lock (_gate)
            {
                remaining = owned.OwnerLease is null
                    ? _activeOperations.Count
                    : _activeOperations.Count(lease => !ReferenceEquals(lease, owned.OwnerLease));
            }

            if (remaining == 0)
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Timed out after {drainTimeout} waiting for {remaining} active database " +
                    "operation(s) to drain before restore.");
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    public void ReopenAdmission(IRestoreLease restoreLease)
    {
        AsOwned(restoreLease);

        lock (_gate)
        {
            _admissionOpen = true;
        }
    }

    private static RestoreLease AsOwned(IRestoreLease restoreLease) =>
        restoreLease as RestoreLease
            ?? throw new ArgumentException($"{nameof(restoreLease)} was not created by this coordinator.", nameof(restoreLease));

    private void Release(DatabaseOperationLease lease)
    {
        lock (_gate)
        {
            _activeOperations.Remove(lease);
        }
    }

    private void ReleaseRestore()
    {
        lock (_gate)
        {
            _restoreInProgress = false;
        }
    }

    private sealed class RestoreLease(RestoreCoordinator owner, DatabaseOperationLease? ownerLease) : IRestoreLease
    {
        private int _disposed;

        /// <summary>The ambient database-operation lease held by the restore's own request, if any.</summary>
        public DatabaseOperationLease? OwnerLease { get; } = ownerLease;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ReleaseRestore();
            }
        }
    }

    private sealed class DatabaseOperationLease(RestoreCoordinator owner) : IDatabaseOperationLease
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(this);
            }
        }
    }
}
