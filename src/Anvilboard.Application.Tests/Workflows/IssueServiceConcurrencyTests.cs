using Anvilboard.Application.Auditing;
using Anvilboard.Application.Automation;
using Anvilboard.Application.Issues;
using Anvilboard.Application.Realtime;
using Anvilboard.Domain;
using Anvilboard.Plugins.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anvilboard.Application.Tests.Workflows;

/// <summary>
/// Covers the conditional-write contract on <see cref="IssueService"/>: supplying
/// <c>expectedVersion</c> turns a mutation into a compare-and-set, and a mismatch must abort before
/// anything is written, sequenced, or published. The read-modify-write window these tests close is
/// the one a board client opens between rendering an issue and acting on it.
/// </summary>
public sealed class IssueServiceConcurrencyTests
{
    [Fact]
    public async Task ChangeStatusAsync_StaleExpectedVersion_ThrowsConcurrencyConflict()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var backlog = fixture.CreateState("backlog", "Backlog", order: 0);
        var done = fixture.CreateState("done", "Done", order: 4, isTerminal: true);
        fixture.CreateTransition(backlog.Id, done.Id);
        var issue = fixture.CreateIssue(backlog.Id);
        issue.Version = 3;
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();

        var service = CreateIssueService(fixture);

        var exception = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => service.ChangeStatusAsync(fixture.WorkspaceId, issue.Id, done.Id, expectedVersion: 2));

        Assert.Equal("CONCURRENCY_CONFLICT", exception.ErrorCode);
        Assert.Equal(2, exception.ExpectedVersion);
        Assert.Equal(3, exception.CurrentVersion);
    }

    [Fact]
    public async Task ChangeStatusAsync_StaleExpectedVersion_LeavesNoTraceOfTheAttempt()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var backlog = fixture.CreateState("backlog", "Backlog", order: 0);
        var done = fixture.CreateState("done", "Done", order: 4, isTerminal: true);
        fixture.CreateTransition(backlog.Id, done.Id);
        var issue = fixture.CreateIssue(backlog.Id);
        issue.Version = 3;
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();
        var updatedAt = issue.UpdatedAt;
        var activityBefore = await fixture.Db.ActivityEvents.CountAsync();

        var publisher = new CountingRealtimeUpdatePublisher();
        var service = CreateIssueService(fixture, publisher);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => service.ChangeStatusAsync(fixture.WorkspaceId, issue.Id, done.Id, expectedVersion: 2));

        // Read through a fresh context: the fixture's tracker could report an in-memory mutation
        // that was never saved, which is exactly the failure mode this asserts against.
        await using var verification = fixture.CreateFreshContext();
        var persisted = await verification.Issues.AsNoTracking().SingleAsync(i => i.Id == issue.Id);
        Assert.Equal(backlog.Id, persisted.WorkflowStateId);
        Assert.Equal(3, persisted.Version);
        Assert.Equal(updatedAt, persisted.UpdatedAt);
        Assert.Equal(activityBefore, await verification.ActivityEvents.CountAsync());
        Assert.Equal(0, publisher.PublishCount);
    }

    [Fact]
    public async Task ChangeStatusAsync_MatchingExpectedVersion_AppliesAndIncrements()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var backlog = fixture.CreateState("backlog", "Backlog", order: 0);
        var done = fixture.CreateState("done", "Done", order: 4, isTerminal: true);
        fixture.CreateTransition(backlog.Id, done.Id);
        var issue = fixture.CreateIssue(backlog.Id);
        issue.Version = 3;
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();

        var service = CreateIssueService(fixture);
        var result = await service.ChangeStatusAsync(
            fixture.WorkspaceId, issue.Id, done.Id, expectedVersion: 3);

        Assert.Equal(done.Id, result.WorkflowStateId);
        Assert.Equal(4, result.Version);
    }

    [Fact]
    public async Task ChangeStatusAsync_OmittedExpectedVersion_KeepsLastWriterWins()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var backlog = fixture.CreateState("backlog", "Backlog", order: 0);
        var done = fixture.CreateState("done", "Done", order: 4, isTerminal: true);
        fixture.CreateTransition(backlog.Id, done.Id);
        var issue = fixture.CreateIssue(backlog.Id);
        issue.Version = 7;
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();

        var service = CreateIssueService(fixture);
        var result = await service.ChangeStatusAsync(fixture.WorkspaceId, issue.Id, done.Id);

        Assert.Equal(done.Id, result.WorkflowStateId);
        Assert.Equal(8, result.Version);
    }

    /// <summary>
    /// The guard runs before transition validation, so a caller acting on a stale read is told to
    /// refetch rather than being handed a workflow error that misdescribes why it failed — the
    /// transition it asked for may well be legal against the state it has not seen yet.
    /// </summary>
    [Fact]
    public async Task ChangeStatusAsync_StaleAndTransitionInvalid_ReportsTheConflictNotTheTransition()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var backlog = fixture.CreateState("backlog", "Backlog", order: 0);
        var done = fixture.CreateState("done", "Done", order: 4, isTerminal: true);
        var issue = fixture.CreateIssue(backlog.Id);
        issue.Version = 3;
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();

        var service = CreateIssueService(fixture);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => service.ChangeStatusAsync(fixture.WorkspaceId, issue.Id, done.Id, expectedVersion: 1));
    }

    /// <summary>
    /// The most dangerous variant of the lost update: the stale caller happens to request exactly
    /// the state a concurrent writer already applied. If the guard sat after the no-op short
    /// circuit it would return a vacuous success and the caller would never learn it was stale.
    /// </summary>
    [Fact]
    public async Task ChangeStatusAsync_StaleAndAlreadyInTargetState_StillConflicts()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var backlog = fixture.CreateState("backlog", "Backlog", order: 0);
        var issue = fixture.CreateIssue(backlog.Id);
        issue.Version = 3;
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();

        var service = CreateIssueService(fixture);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => service.ChangeStatusAsync(fixture.WorkspaceId, issue.Id, backlog.Id, expectedVersion: 1));
    }

    [Fact]
    public async Task AssignAsync_StaleExpectedVersion_ThrowsAndLeavesAssigneeUntouched()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var backlog = fixture.CreateState("backlog", "Backlog", order: 0);
        var issue = fixture.CreateIssue(backlog.Id);
        issue.Version = 5;
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();

        var service = CreateIssueService(fixture);

        var exception = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => service.AssignAsync(fixture.WorkspaceId, issue.Id, MemberId.New(), expectedVersion: 4));

        Assert.Equal(5, exception.CurrentVersion);
        await using var verification = fixture.CreateFreshContext();
        var persisted = await verification.Issues.AsNoTracking().SingleAsync(i => i.Id == issue.Id);
        Assert.Null(persisted.AssigneeId);
        Assert.Equal(5, persisted.Version);
    }

    [Fact]
    public async Task AssignAsync_MatchingExpectedVersion_AppliesAndIncrements()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var backlog = fixture.CreateState("backlog", "Backlog", order: 0);
        var issue = fixture.CreateIssue(backlog.Id);
        issue.Version = 5;
        fixture.Db.Issues.Add(issue);
        await fixture.Db.SaveChangesAsync();

        var assignee = MemberId.New();
        var service = CreateIssueService(fixture);
        var result = await service.AssignAsync(
            fixture.WorkspaceId, issue.Id, assignee, expectedVersion: 5);

        Assert.Equal(assignee, result.AssigneeId);
        Assert.Equal(6, result.Version);
    }

    /// <summary>
    /// A created issue starts at version 1, not 0, so the very first read a client takes can be
    /// used as an <c>expectedVersion</c> that is distinguishable from "no version supplied".
    /// </summary>
    [Fact]
    public async Task CreateAsync_SeedsVersionOne()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var service = CreateIssueService(fixture);

        var issue = await service.CreateAsync(fixture.WorkspaceId, fixture.TeamId, "Fresh");

        Assert.Equal(1, issue.Version);
        await using var verification = fixture.CreateFreshContext();
        var persisted = await verification.Issues.AsNoTracking().SingleAsync(i => i.Id == issue.Id);
        Assert.Equal(1, persisted.Version);
    }

    [Fact]
    public async Task CreateThenChangeStatus_UsingTheReturnedVersion_Succeeds()
    {
        await using var fixture = await WorkflowFixture.CreateAsync();
        var done = fixture.CreateState("done", "Done", order: 4, isTerminal: true);
        fixture.CreateTransition(fixture.Current.Id, done.Id);
        await fixture.Db.SaveChangesAsync();

        var service = CreateIssueService(fixture);
        var issue = await service.CreateAsync(fixture.WorkspaceId, fixture.TeamId, "Round trip");

        var moved = await service.ChangeStatusAsync(
            fixture.WorkspaceId, issue.Id, done.Id, expectedVersion: issue.Version);

        Assert.Equal(done.Id, moved.WorkflowStateId);
        Assert.Equal(2, moved.Version);
    }

    private static IssueService CreateIssueService(
        WorkflowFixture fixture, IRealtimeUpdatePublisher? publisher = null) => new(
        fixture.Db,
        new FakePluginRegistry(),
        fixture.Engine,
        publisher ?? new NullRealtimeUpdatePublisher(),
        CorrelationContext.FromHeaderOrNew(null),
        NullLogger<IssueService>.Instance);

    private sealed class FakePluginRegistry : IPluginRegistry
    {
        public IReadOnlyList<IAnvilboardPlugin> All { get; } = [];
        public IReadOnlyList<IIngestionSource> IngestionSources { get; } = [];
        public IReadOnlyList<IWebhookReceiver> WebhookReceivers { get; } = [];
        public IReadOnlyList<IIssueHook> IssueHooks { get; } = [];
    }

    private sealed class CountingRealtimeUpdatePublisher : IRealtimeUpdatePublisher
    {
        public int PublishCount { get; private set; }

        public ValueTask PublishAsync(RealtimeChange change, CancellationToken ct = default)
        {
            PublishCount++;
            return ValueTask.CompletedTask;
        }
    }
}
