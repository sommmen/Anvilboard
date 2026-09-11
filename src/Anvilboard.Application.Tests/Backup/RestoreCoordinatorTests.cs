using Anvilboard.Application.Backup;

namespace Anvilboard.Application.Tests.Backup;

/// <summary>
/// Covers <see cref="RestoreCoordinator"/> in isolation: singleton restore exclusion, database-work
/// admission open/close, active-operation draining, drain timeout, and the owner-exemption rule
/// that lets a restore's own ambient database-operation lease avoid waiting on itself
/// (`docs/plans/backup-and-restore.md` §8.1, §8.4).
/// </summary>
public sealed class RestoreCoordinatorTests
{
    [Fact]
    public void TryBeginRestore_WhileAnotherRestoreIsInProgress_ReturnsNull()
    {
        var coordinator = new RestoreCoordinator();
        using var first = coordinator.TryBeginRestore();
        Assert.NotNull(first);

        var second = coordinator.TryBeginRestore();
        Assert.Null(second);
    }

    [Fact]
    public void TryBeginRestore_AfterThePriorLeaseIsDisposed_CanBeginAgain()
    {
        var coordinator = new RestoreCoordinator();
        var first = coordinator.TryBeginRestore();
        Assert.NotNull(first);
        first!.Dispose();

        using var second = coordinator.TryBeginRestore();
        Assert.NotNull(second);
    }

    [Fact]
    public void TryBeginRestore_DisposingTwice_DoesNotDoubleReleaseTheLock()
    {
        var coordinator = new RestoreCoordinator();
        var first = coordinator.TryBeginRestore();
        first!.Dispose();
        first.Dispose();

        using var second = coordinator.TryBeginRestore();
        Assert.NotNull(second);
    }

    [Fact]
    public void TryBeginDatabaseOperation_WhileAdmissionIsOpen_Succeeds()
    {
        var coordinator = new RestoreCoordinator();
        using var lease = coordinator.TryBeginDatabaseOperation();
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task TryBeginDatabaseOperation_AfterAdmissionIsClosed_ReturnsNull()
    {
        var coordinator = new RestoreCoordinator();
        using var restoreLease = coordinator.TryBeginRestore();
        await coordinator.CloseAdmissionAndDrainAsync(restoreLease!, TimeSpan.FromSeconds(2));

        var operation = coordinator.TryBeginDatabaseOperation();
        Assert.Null(operation);
    }

    [Fact]
    public async Task ReopenAdmission_AfterAClose_AllowsNewDatabaseOperationsAgain()
    {
        var coordinator = new RestoreCoordinator();
        using var restoreLease = coordinator.TryBeginRestore();
        await coordinator.CloseAdmissionAndDrainAsync(restoreLease!, TimeSpan.FromSeconds(2));

        coordinator.ReopenAdmission(restoreLease!);

        using var operation = coordinator.TryBeginDatabaseOperation();
        Assert.NotNull(operation);
    }

    [Fact]
    public async Task CloseAdmissionAndDrainAsync_ClosesAdmissionBeforeTheDrainCompletes()
    {
        var coordinator = new RestoreCoordinator();
        // TryBeginRestore captures whatever lease is ambient at that instant as the exempt owner
        // lease, so the restore lease must be acquired before the *unrelated* blocking operation
        // below, otherwise the blocking operation would itself become the (wrongly) exempted one.
        using var restoreLease = coordinator.TryBeginRestore();

        var blockingOperation = coordinator.TryBeginDatabaseOperation();
        Assert.NotNull(blockingOperation);

        var drainTask = coordinator.CloseAdmissionAndDrainAsync(restoreLease!, TimeSpan.FromSeconds(5));

        // Admission closes synchronously, before the drain wait even starts polling.
        Assert.Null(coordinator.TryBeginDatabaseOperation());

        blockingOperation!.Dispose();
        await drainTask;
    }

    [Fact]
    public async Task CloseAdmissionAndDrainAsync_WaitsForActiveDatabaseOperationsToComplete()
    {
        var coordinator = new RestoreCoordinator();
        using var restoreLease = coordinator.TryBeginRestore();

        var blockingOperation = coordinator.TryBeginDatabaseOperation();
        Assert.NotNull(blockingOperation);

        var drainTask = coordinator.CloseAdmissionAndDrainAsync(restoreLease!, TimeSpan.FromSeconds(5));

        await Task.Delay(TimeSpan.FromMilliseconds(150));
        Assert.False(drainTask.IsCompleted);

        blockingOperation!.Dispose();

        var completed = await Task.WhenAny(drainTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(drainTask, completed);
        await drainTask;
    }

    [Fact]
    public async Task CloseAdmissionAndDrainAsync_TimesOut_WhenAnActiveOperationNeverCompletes()
    {
        var coordinator = new RestoreCoordinator();
        using var restoreLease = coordinator.TryBeginRestore();

        using var blockingOperation = coordinator.TryBeginDatabaseOperation();
        Assert.NotNull(blockingOperation);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => coordinator.CloseAdmissionAndDrainAsync(restoreLease!, TimeSpan.FromMilliseconds(150)));

        Assert.Contains("1", ex.Message);
    }

    [Fact]
    public async Task CloseAdmissionAndDrainAsync_ExemptsTheRestoresOwnAmbientDatabaseOperationLease()
    {
        var coordinator = new RestoreCoordinator();

        // Simulates the restore request itself already holding a database-operation lease (as a
        // request-admission gate wrapping the whole call would establish) at the moment the
        // restore begins: TryBeginRestore captures whatever lease is ambient right then.
        using var ownLease = coordinator.TryBeginDatabaseOperation();
        Assert.NotNull(ownLease);

        using var restoreLease = coordinator.TryBeginRestore();
        Assert.NotNull(restoreLease);

        // Must complete promptly despite the still-open owner lease, since it is exempted from
        // the drain wait.
        var drainTask = coordinator.CloseAdmissionAndDrainAsync(restoreLease!, TimeSpan.FromSeconds(5));
        var completed = await Task.WhenAny(drainTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(drainTask, completed);
        await drainTask;
    }

    [Fact]
    public async Task CloseAdmissionAndDrainAsync_StillWaitsForUnrelatedConcurrentDatabaseOperations()
    {
        var coordinator = new RestoreCoordinator();

        using var ownLease = coordinator.TryBeginDatabaseOperation();
        using var restoreLease = coordinator.TryBeginRestore();

        // A second, unrelated database operation (a different logical caller) begins after the
        // restore's own lease was already captured as the exempt owner above.
        var unrelatedOperation = coordinator.TryBeginDatabaseOperation();
        Assert.NotNull(unrelatedOperation);

        var drainTask = coordinator.CloseAdmissionAndDrainAsync(restoreLease!, TimeSpan.FromSeconds(5));

        await Task.Delay(TimeSpan.FromMilliseconds(150));
        Assert.False(drainTask.IsCompleted);

        unrelatedOperation!.Dispose();
        await drainTask;
    }

    [Fact]
    public void ReopenAdmission_WithALeaseNotCreatedByAnyCoordinator_ThrowsArgumentException()
    {
        var coordinator = new RestoreCoordinator();

        Assert.Throws<ArgumentException>(() => coordinator.ReopenAdmission(new UnrelatedRestoreLease()));
    }

    private sealed class UnrelatedRestoreLease : IRestoreLease
    {
        public void Dispose()
        {
        }
    }
}
