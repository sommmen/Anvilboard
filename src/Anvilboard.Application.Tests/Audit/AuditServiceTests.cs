using Anvilboard.Application.Auditing;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Tests.Audit;

public sealed class AuditServiceTests
{
    [Fact]
    public async Task RecordAsync_PersistsMetadataAndRedactsSecretSummary()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AnvilboardDbContext(new DbContextOptionsBuilder<AnvilboardDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();

        var workspaceId = WorkspaceId.New();
        var occurredBefore = DateTimeOffset.UtcNow;
        var request = new AuditEventRequest(
            workspaceId,
            "agent:automation",
            AuditChannel.Rest,
            "issue.create",
            "issue",
            "issue-42",
            "corr-123",
            "created with token=super-secret-value and note=ordinary prose\n"
            + "json: {\"token\": \"escaped \\\"secret\\\" value\", \"password\": \"line one\nline two\"}");

        await new AuditService(db).RecordAsync(request);

        var persisted = await db.AuditEvents.AsNoTracking().SingleAsync();
        Assert.NotEqual(Guid.Empty, persisted.Id.Value);
        Assert.Equal(workspaceId, persisted.WorkspaceId);
        Assert.Equal(request.ActorId, persisted.ActorId);
        Assert.Equal(request.Channel, persisted.Channel);
        Assert.Equal(request.Action, persisted.Action);
        Assert.Equal(request.TargetType, persisted.TargetType);
        Assert.Equal(request.TargetId, persisted.TargetId);
        Assert.Equal(request.CorrelationId, persisted.CorrelationId);
        Assert.InRange(persisted.OccurredAt, occurredBefore, DateTimeOffset.UtcNow);
        Assert.Contains("***REDACTED***", persisted.ResultSummary);
        Assert.DoesNotContain("super-secret-value", persisted.ResultSummary);
        Assert.DoesNotContain("escaped", persisted.ResultSummary);
        Assert.DoesNotContain("secret", persisted.ResultSummary);
        Assert.DoesNotContain("line one", persisted.ResultSummary);
        Assert.DoesNotContain("line two", persisted.ResultSummary);
        Assert.Contains("ordinary prose", persisted.ResultSummary);

        var rawChannel = await db.Database.SqlQueryRaw<string>(
            "SELECT Channel AS Value FROM AuditEvents").SingleAsync();
        Assert.Equal("REST", rawChannel);
    }
}
