using Anvilboard.Application.Artifacts;
using Anvilboard.Application.Auditing;
using Anvilboard.Application.Automation;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Artifacts;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anvilboard.Application.Tests.Artifacts;

/// <summary>
/// A real SQLite database (in-memory, but the genuine provider) seeded with two issues in one
/// workspace. Using the real provider matters here: the filtered unique index on
/// <c>(IssueId, DedupKey)</c> is the refresh path's concurrency control, and an in-memory fake
/// would not enforce it.
/// </summary>
internal sealed class ArtifactFixture : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly RaceInterceptor raceInterceptor;

    private ArtifactFixture(
        SqliteConnection connection,
        RaceInterceptor raceInterceptor,
        AnvilboardDbContext db,
        Issue issue,
        Issue otherIssue,
        Member member)
    {
        this.connection = connection;
        this.raceInterceptor = raceInterceptor;
        Db = db;
        Issue = issue;
        OtherIssue = otherIssue;
        Member = member;
    }

    public AnvilboardDbContext Db { get; }
    public Issue Issue { get; }
    public Issue OtherIssue { get; }
    public Member Member { get; }
    public RecordingArtifactStore Store { get; } = new();

    /// <summary>The workspace owning <see cref="Issue"/> and <see cref="OtherIssue"/>.</summary>
    public WorkspaceId WorkspaceId { get; private init; }

    /// <summary>A different workspace, owning <see cref="ForeignIssue"/>.</summary>
    public WorkspaceId ForeignWorkspaceId { get; private init; }

    /// <summary>An issue that exists but belongs to <see cref="ForeignWorkspaceId"/>.</summary>
    public Issue ForeignIssue { get; private init; } = null!;

    public ArtifactService CreateService(IArtifactStore? store = null) => new(
        Db,
        store ?? Store,
        new AuditService(Db),
        CorrelationContext.FromHeaderOrNew(null),
        NullLogger<ArtifactService>.Instance);

    /// <summary>
    /// Arranges for <paramref name="competitor"/> to be inserted through a second DbContext at the
    /// moment the service issues its own insert, reproducing a genuine lost race against the
    /// filtered <c>(IssueId, DedupKey)</c> unique index rather than merely pre-seeding the row
    /// (which the service's own lookup would have found first).
    /// </summary>
    public ArtifactId RaceInCompetingArtifactOnNextSave(Artifact competitor)
    {
        raceInterceptor.Arm(competitor, connection);
        return competitor.Id;
    }

    public static async Task<ArtifactFixture> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var raceInterceptor = new RaceInterceptor();
        var options = new DbContextOptionsBuilder<AnvilboardDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(raceInterceptor)
            .Options;
        var db = new AnvilboardDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var workspaceId = WorkspaceId.New();
        var teamId = TeamId.New();
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Test workspace", Slug = "test-workspace", CreatedAt = DateTimeOffset.UtcNow });
        db.Teams.Add(new Team { Id = teamId, WorkspaceId = workspaceId, Name = "Test team", Key = "TST", CreatedAt = DateTimeOffset.UtcNow });

        var member = new Member
        {
            Id = MemberId.New(),
            WorkspaceId = workspaceId,
            DisplayName = "Contributor",
            Email = "contributor@example.test",
            Username = "contributor",
            Role = Role.Contributor,
        };
        db.Members.Add(member);

        static Issue MakeIssue(TeamId team, string key) => new()
        {
            Id = IssueId.New(),
            TeamId = team,
            Key = key,
            Title = $"Issue {key}",
            Status = IssueStatus.Backlog,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var issue = MakeIssue(teamId, "TST-1");
        var otherIssue = MakeIssue(teamId, "TST-2");
        db.Issues.AddRange(issue, otherIssue);

        // A second, fully separate workspace: the workspace-scoping tests need a real issue that
        // legitimately exists but belongs to someone else, which is the case a scope check must
        // reject and an existence check alone would let through.
        var foreignWorkspaceId = WorkspaceId.New();
        var foreignTeamId = TeamId.New();
        db.Workspaces.Add(new Workspace { Id = foreignWorkspaceId, Name = "Other workspace", Slug = "other-workspace", CreatedAt = DateTimeOffset.UtcNow });
        db.Teams.Add(new Team { Id = foreignTeamId, WorkspaceId = foreignWorkspaceId, Name = "Other team", Key = "OTH", CreatedAt = DateTimeOffset.UtcNow });
        var foreignIssue = MakeIssue(foreignTeamId, "OTH-1");
        db.Issues.Add(foreignIssue);

        await db.SaveChangesAsync();

        return new ArtifactFixture(connection, raceInterceptor, db, issue, otherIssue, member)
        {
            WorkspaceId = workspaceId,
            ForeignWorkspaceId = foreignWorkspaceId,
            ForeignIssue = foreignIssue,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await connection.DisposeAsync();
    }
}

/// <summary>
/// Fires a single competing insert on a separate DbContext just before the first tracked
/// <c>SaveChanges</c> reaches the database, so a test can reproduce a lost unique-index race
/// deterministically instead of relying on real concurrent timing.
/// </summary>
internal sealed class RaceInterceptor : SaveChangesInterceptor
{
    private Artifact? competitor;
    private SqliteConnection? connection;

    public void Arm(Artifact artifact, SqliteConnection sharedConnection)
    {
        competitor = artifact;
        connection = sharedConnection;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (competitor is { } racing && connection is { } sharedConnection)
        {
            // Disarm first: the service retries its save after converging, and re-firing would
            // deadlock the test against its own competitor.
            competitor = null;
            connection = null;

            var options = new DbContextOptionsBuilder<AnvilboardDbContext>().UseSqlite(sharedConnection).Options;
            await using var competingContext = new AnvilboardDbContext(options);
            competingContext.Artifacts.Add(racing);
            await competingContext.SaveChangesAsync(cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>An <see cref="IArtifactStore"/> that records calls so tests can assert the
/// store-before-row ordering and the compensating purge, without touching real storage.</summary>
internal sealed class RecordingArtifactStore : IArtifactStore
{
    private readonly Dictionary<string, ArtifactContent> blobs = [];

    public List<string> Deleted { get; } = [];
    public int StoreCallCount { get; private set; }

    public Task<string> StoreAsync(byte[] content, string? contentType, CancellationToken ct = default)
    {
        StoreCallCount++;
        var reference = Guid.NewGuid().ToString("N");
        blobs[reference] = new ArtifactContent(content, contentType);
        return Task.FromResult(reference);
    }

    public Task<ArtifactContent?> RetrieveAsync(string reference, CancellationToken ct = default) =>
        Task.FromResult(blobs.TryGetValue(reference, out var content) ? content : null);

    public Task DeleteAsync(string reference, CancellationToken ct = default)
    {
        Deleted.Add(reference);
        blobs.Remove(reference);
        return Task.CompletedTask;
    }
}

/// <summary>Simulates the storage outage behind <c>ARTIFACT_STORE_UNAVAILABLE</c>.</summary>
internal sealed class ThrowingArtifactStore : IArtifactStore
{
    public int StoreCallCount { get; private set; }

    public Task<string> StoreAsync(byte[] content, string? contentType, CancellationToken ct = default)
    {
        StoreCallCount++;
        throw new IOException("artifact storage is unavailable");
    }

    public Task<ArtifactContent?> RetrieveAsync(string reference, CancellationToken ct = default) =>
        throw new IOException("artifact storage is unavailable");

    public Task DeleteAsync(string reference, CancellationToken ct = default) =>
        throw new IOException("artifact storage is unavailable");
}
