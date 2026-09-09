using Anvilboard.Application.Automation;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Automation;

public sealed class IdempotencyServiceTests
{
    [Fact]
    public async Task TryBeginAsync_NoExistingRecord_ReturnsNew()
    {
        await using var fixture = await IdempotencyFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.TryBeginAsync(
            fixture.WorkspaceId, "actor-1", "issues.create", "key-1", "hash-1");

        Assert.Equal(IdempotencyOutcome.New, result.Outcome);
        Assert.Null(result.StoredResultPayload);
    }

    [Fact]
    public async Task CommitAsync_ThenTryBeginAsync_SameHash_ReturnsReplayOriginalWithStoredPayload()
    {
        // AC-007: replaying the identical key and canonical payload must return the original
        // result without re-executing the mutation.
        await using var fixture = await IdempotencyFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.CommitAsync(
            fixture.WorkspaceId, "actor-1", "issues.create", "key-1", "hash-1",
            """{"id":"abc"}""", IdempotencyService.DefaultRetention);

        var result = await service.TryBeginAsync(
            fixture.WorkspaceId, "actor-1", "issues.create", "key-1", "hash-1");

        Assert.Equal(IdempotencyOutcome.ReplayOriginal, result.Outcome);
        Assert.Equal("""{"id":"abc"}""", result.StoredResultPayload);
    }

    [Fact]
    public async Task CommitAsync_ThenTryBeginAsync_DifferentHash_ReturnsKeyReusedWithDifferentPayload()
    {
        // AC-008: reusing a committed key with a different payload must be rejected.
        await using var fixture = await IdempotencyFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.CommitAsync(
            fixture.WorkspaceId, "actor-1", "issues.create", "key-1", "hash-1",
            """{"id":"abc"}""", IdempotencyService.DefaultRetention);

        var result = await service.TryBeginAsync(
            fixture.WorkspaceId, "actor-1", "issues.create", "key-1", "hash-2");

        Assert.Equal(IdempotencyOutcome.KeyReusedWithDifferentPayload, result.Outcome);
        Assert.Null(result.StoredResultPayload);
    }

    [Fact]
    public async Task CommitAsync_ThenTryBeginAsync_DifferentActor_ReturnsNew()
    {
        // §7.4 Edge Case Handling: the actor identity is part of the key tuple so two different
        // actors sharing a key can never collide.
        await using var fixture = await IdempotencyFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.CommitAsync(
            fixture.WorkspaceId, "actor-1", "issues.create", "key-1", "hash-1",
            """{"id":"abc"}""", IdempotencyService.DefaultRetention);

        var result = await service.TryBeginAsync(
            fixture.WorkspaceId, "actor-2", "issues.create", "key-1", "hash-1");

        Assert.Equal(IdempotencyOutcome.New, result.Outcome);
    }

    [Fact]
    public async Task CommitAsync_ThenTryBeginAsync_DifferentOperation_ReturnsNew()
    {
        await using var fixture = await IdempotencyFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.CommitAsync(
            fixture.WorkspaceId, "actor-1", "issues.create", "key-1", "hash-1",
            """{"id":"abc"}""", IdempotencyService.DefaultRetention);

        var result = await service.TryBeginAsync(
            fixture.WorkspaceId, "actor-1", "issues.transition", "key-1", "hash-1");

        Assert.Equal(IdempotencyOutcome.New, result.Outcome);
    }

    [Fact]
    public async Task CommitAsync_PersistsRecord_WithExpiresAtEqualToCreatedAtPlusRetention()
    {
        await using var fixture = await IdempotencyFixture.CreateAsync();
        var service = fixture.CreateService();
        var retention = TimeSpan.FromDays(30);

        var before = DateTimeOffset.UtcNow;
        await service.CommitAsync(
            fixture.WorkspaceId, "actor-1", "issues.create", "key-1", "hash-1",
            """{"id":"abc"}""", retention);
        var after = DateTimeOffset.UtcNow;

        var record = await fixture.Db.IdempotencyRecords.AsNoTracking().SingleAsync();
        Assert.Equal(fixture.WorkspaceId, record.WorkspaceId);
        Assert.Equal("actor-1", record.ActorId);
        Assert.Equal("issues.create", record.Operation);
        Assert.Equal("key-1", record.Key);
        Assert.Equal("hash-1", record.RequestHash);
        Assert.Equal("""{"id":"abc"}""", record.ResultPayload);
        Assert.InRange(record.CreatedAt, before, after);
        Assert.Equal(record.CreatedAt + retention, record.ExpiresAt);
    }

    [Fact]
    public void DefaultRetention_Is30Days()
    {
        // tech-design §10.1: the standard retention policy is 30 days from CreatedAt.
        Assert.Equal(TimeSpan.FromDays(30), IdempotencyService.DefaultRetention);
    }

    private sealed class IdempotencyFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private IdempotencyFixture(SqliteConnection connection, AnvilboardDbContext db)
        {
            this.connection = connection;
            Db = db;
        }

        public AnvilboardDbContext Db { get; }
        public WorkspaceId WorkspaceId { get; } = WorkspaceId.New();

        public IdempotencyService CreateService() => new(Db);

        public static async Task<IdempotencyFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(connection).Options;
            var db = new AnvilboardDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new IdempotencyFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
