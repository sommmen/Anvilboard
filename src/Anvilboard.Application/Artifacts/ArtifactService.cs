using System.Text;
using System.Text.Json;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Artifacts;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Artifacts;

/// <inheritdoc cref="IArtifactService"/>
public sealed class ArtifactService(AnvilboardDbContext db, IArtifactStore store) : IArtifactService
{
    /// <summary>Matches the <c>Artifacts.Title</c> column so an over-long title fails with a
    /// catalogued validation error rather than a storage-level truncation error.</summary>
    private const int TitleMaxLength = 500;
    private const int SourceMaxLength = 100;
    private const int DedupKeyMaxLength = 500;

    /// <summary>Metadata is an opaque provider bag, so only its size is constrained — this keeps a
    /// misbehaving plugin from using it as unbounded storage without coupling us to its shape.</summary>
    private const int MetadataMaxBytes = 8 * 1024;

    private const string DefaultSource = "local";

    public async Task<ArtifactDto> AttachArtifactAsync(
        IssueId issueId,
        ArtifactKind kind,
        string title,
        string? contentReference = null,
        ArtifactInlineContent? inlineContent = null,
        string? source = null,
        MemberId? actorId = null,
        string? dedupKey = null,
        string? metadata = null,
        CancellationToken ct = default)
    {
        await EnsureIssueExistsAsync(issueId, ct);

        var normalizedTitle = ValidateTitle(title);
        var normalizedSource = ValidateSource(source);
        var normalizedDedupKey = ValidateDedupKey(dedupKey, required: false);
        ValidateKind(kind);
        ValidateMetadata(metadata);

        if (inlineContent is not null && !string.IsNullOrWhiteSpace(contentReference))
        {
            throw new ArtifactException(
                "VALIDATION_FAILED",
                "Specify either 'contentReference' or inline content, not both.");
        }

        // Content is stored before the row is persisted so a store failure throws while nothing has
        // been written to the Artifacts table. The inverse ordering could leave a row pointing at
        // content that was never stored, which is the corrupt state fail-closed handling forbids.
        var resolvedReference = inlineContent is not null
            ? await StoreContentAsync(inlineContent, ct)
            : ValidateContentReference(contentReference);

        var now = DateTimeOffset.UtcNow;
        var artifact = new Artifact
        {
            Id = ArtifactId.New(),
            IssueId = issueId,
            Kind = kind,
            Title = normalizedTitle,
            ContentReference = resolvedReference,
            Source = normalizedSource,
            AddedById = actorId,
            DedupKey = normalizedDedupKey,
            Metadata = metadata,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Artifacts.Add(artifact);
        RecordActivity(artifact, ActivityEventType.ArtifactAttached, actorId);
        await db.SaveChangesAsync(ct);

        return ArtifactDto.FromArtifact(artifact);
    }

    public async Task<IReadOnlyList<ArtifactDto>> ListArtifactsAsync(IssueId issueId, CancellationToken ct = default)
    {
        await EnsureIssueExistsAsync(issueId, ct);

        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(artifact => artifact.IssueId == issueId)
            .ToListAsync(ct);

        // Ordered client-side because SQLite cannot ORDER BY a DateTimeOffset column.
        return artifacts
            .OrderBy(artifact => artifact.CreatedAt)
            .Select(ArtifactDto.FromArtifact)
            .ToList();
    }

    public async Task<ArtifactDto> RefreshArtifactAsync(
        IssueId issueId,
        ArtifactKind kind,
        string dedupKey,
        string title,
        string contentReference,
        string? metadata = null,
        string source = "github",
        CancellationToken ct = default)
    {
        await EnsureIssueExistsAsync(issueId, ct);

        if (!IsRefreshable(kind))
        {
            throw new ArtifactException(
                "VALIDATION_FAILED",
                $"Artifact kind '{ArtifactKindConverter.ToWireValue(kind)}' is not refreshable.");
        }

        var normalizedDedupKey = ValidateDedupKey(dedupKey, required: true)!;
        var normalizedTitle = ValidateTitle(title);
        var normalizedSource = ValidateSource(source);
        var normalizedReference = ValidateContentReference(contentReference);
        ValidateMetadata(metadata);

        var existing = await db.Artifacts.FirstOrDefaultAsync(
            artifact => artifact.IssueId == issueId
                && artifact.Kind == kind
                && artifact.DedupKey == normalizedDedupKey,
            ct);

        // No match means this is the provider's first event for the resource, so the refresh
        // doubles as the attach and the caller never needs a separate create call.
        if (existing is null)
        {
            return await AttachArtifactAsync(
                issueId,
                kind,
                normalizedTitle,
                normalizedReference,
                source: normalizedSource,
                dedupKey: normalizedDedupKey,
                metadata: metadata,
                ct: ct);
        }

        // Identity, attribution, and creation time survive the update so the activity feed keeps
        // showing who first attached the artifact and when.
        existing.Title = normalizedTitle;
        existing.ContentReference = normalizedReference;
        existing.Metadata = metadata;
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        RecordActivity(existing, ActivityEventType.ArtifactRefreshed, actorId: null);
        await db.SaveChangesAsync(ct);

        return ArtifactDto.FromArtifact(existing);
    }

    public async Task RemoveArtifactAsync(
        IssueId issueId,
        ArtifactId artifactId,
        MemberId? actorId = null,
        CancellationToken ct = default)
    {
        var artifact = await db.Artifacts.FirstOrDefaultAsync(candidate => candidate.Id == artifactId, ct);
        if (artifact is null || artifact.IssueId != issueId)
        {
            throw new ArtifactException("REFERENCED_ENTITY_NOT_FOUND", "The artifact was not found for this issue.");
        }

        db.Artifacts.Remove(artifact);
        RecordActivity(artifact, ActivityEventType.ArtifactRemoved, actorId);
        await db.SaveChangesAsync(ct);

        // Purged after the row is gone: the artifact is already unreachable, so a store failure
        // here must not resurrect it. The store no-ops for references it does not own (external
        // URLs), making this safe for every kind.
        await store.DeleteAsync(artifact.ContentReference, ct);
    }

    /// <summary>
    /// Confirms the issue exists and is reachable through a team. Issues carry no workspace of
    /// their own, so the team join is what makes an orphaned issue unreachable. It does not scope
    /// the lookup to a caller's workspace: like <c>IssueService</c> and <c>IssueLinkService</c>,
    /// this service takes no <c>WorkspaceId</c> and relies on the authorization middleware that
    /// already ran. Direct CLI/MCP/hook callers are covered by audit findings MAJ-001/MAJ-015.
    /// </summary>
    private async Task EnsureIssueExistsAsync(IssueId issueId, CancellationToken ct)
    {
        var exists = await db.Issues
            .Where(issue => issue.Id == issueId)
            .Join(db.Teams, issue => issue.TeamId, team => team.Id, (issue, team) => team.WorkspaceId)
            .AnyAsync(ct);

        if (!exists)
        {
            throw new ArtifactException("REFERENCED_ENTITY_NOT_FOUND", "The issue was not found.");
        }
    }

    private async Task<string> StoreContentAsync(ArtifactInlineContent content, CancellationToken ct)
    {
        try
        {
            return await store.StoreAsync(content.Bytes, content.ContentType, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ArtifactException(
                "ARTIFACT_STORE_UNAVAILABLE",
                "Artifact content could not be stored; the artifact was not attached.");
        }
    }

    private static bool IsRefreshable(ArtifactKind kind) => kind is ArtifactKind.PullRequest;

    private static void ValidateKind(ArtifactKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArtifactException("VALIDATION_FAILED", $"'{(int)kind}' is not a supported artifact kind.");
        }
    }

    private static string ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArtifactException("VALIDATION_FAILED", "Artifact 'title' must not be empty.");
        }

        var normalized = title.Trim();
        if (normalized.Length > TitleMaxLength)
        {
            throw new ArtifactException(
                "VALIDATION_FAILED",
                $"Artifact 'title' must be {TitleMaxLength} characters or fewer.");
        }

        return normalized;
    }

    private static string ValidateContentReference(string? contentReference)
    {
        if (string.IsNullOrWhiteSpace(contentReference))
        {
            throw new ArtifactException("VALIDATION_FAILED", "Artifact 'contentReference' must not be empty.");
        }

        return contentReference.Trim();
    }

    private static string ValidateSource(string? source)
    {
        if (source is null)
        {
            return DefaultSource;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArtifactException("VALIDATION_FAILED", "Artifact 'source' must not be empty.");
        }

        var normalized = source.Trim();
        if (normalized.Length > SourceMaxLength)
        {
            throw new ArtifactException(
                "VALIDATION_FAILED",
                $"Artifact 'source' must be {SourceMaxLength} characters or fewer.");
        }

        return normalized;
    }

    private static string? ValidateDedupKey(string? dedupKey, bool required)
    {
        if (string.IsNullOrWhiteSpace(dedupKey))
        {
            if (required)
            {
                throw new ArtifactException("VALIDATION_FAILED", "Artifact 'dedupKey' must not be empty.");
            }

            return null;
        }

        var normalized = dedupKey.Trim();
        if (normalized.Length > DedupKeyMaxLength)
        {
            throw new ArtifactException(
                "VALIDATION_FAILED",
                $"Artifact 'dedupKey' must be {DedupKeyMaxLength} characters or fewer.");
        }

        return normalized;
    }

    private static void ValidateMetadata(string? metadata)
    {
        if (metadata is not null && Encoding.UTF8.GetByteCount(metadata) > MetadataMaxBytes)
        {
            throw new ArtifactException(
                "VALIDATION_FAILED",
                $"Artifact 'metadata' must be {MetadataMaxBytes} bytes or fewer.");
        }
    }

    /// <summary>
    /// Queues the activity event on the change tracker without saving, so the artifact mutation and
    /// its audit trail commit in the caller's single transaction — an artifact can never be written
    /// without its corresponding event.
    /// </summary>
    private void RecordActivity(Artifact artifact, ActivityEventType type, MemberId? actorId)
    {
        db.ActivityEvents.Add(new ActivityEvent
        {
            Id = ActivityEventId.New(),
            IssueId = artifact.IssueId,
            Type = type,
            ActorId = actorId,
            // Deliberately records only provenance metadata: artifact content and its opaque
            // storage reference never reach the activity feed.
            DataJson = JsonSerializer.Serialize(new
            {
                artifactId = artifact.Id.Value,
                kind = ArtifactKindConverter.ToWireValue(artifact.Kind),
                artifact.Title,
                artifact.Source,
            }),
            OccurredAt = DateTimeOffset.UtcNow,
        });
    }
}
