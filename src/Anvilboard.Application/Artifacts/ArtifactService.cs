using System.Text;
using System.Text.Json;
using Anvilboard.Application.Auditing;
using Anvilboard.Application.Authorization;
using Anvilboard.Application.Automation;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Artifacts;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Anvilboard.Application.Artifacts;

/// <summary>
/// The single mutation and read path for issue artifacts, and the only caller of
/// <see cref="IArtifactStore"/>. Workspace scope is always re-derived server-side from the parent
/// issue's team, so a caller can never widen scope by supplying an identifier from another
/// workspace. No store, EF, or I/O exception escapes: every anticipated failure surfaces as an
/// <see cref="ArtifactException"/> carrying a stable error code from the tech-design §7.7 catalog.
/// </summary>
public sealed class ArtifactService(
    AnvilboardDbContext db,
    IArtifactStore store,
    IAuditService audit,
    CorrelationContext correlation,
    ILogger<ArtifactService> logger) : IArtifactService
{
    private const int TitleMaxLength = 500;
    private const int SourceMaxLength = 100;
    private const int DedupKeyMaxLength = 500;

    /// <summary>Metadata is an opaque provider payload, never parsed here — only bounded, so a
    /// runaway provider response cannot bloat the single-file database.</summary>
    private const int MetadataMaxBytes = 8 * 1024;

    /// <summary>The source key recorded for a human-attached artifact. Automation callers must
    /// supply their own hook key instead (BR-ART-1), which is what keeps <c>AddedById == null</c>
    /// meaningful as the automation discriminator (BR-ART-2).</summary>
    private const string LocalSource = "local";

    public async Task<ArtifactDto> AttachArtifactAsync(
        IssueId issueId,
        string kind,
        string title,
        string contentReference,
        string? source = null,
        MemberId? actorId = null,
        string? metadata = null,
        AuditChannel channel = AuditChannel.System,
        CancellationToken ct = default)
    {
        var workspaceId = await ResolveWorkspaceAsync(issueId, ct);
        var parsedKind = ParseKind(kind);
        var normalizedTitle = NormalizeTitle(title);
        var normalizedReference = NormalizeContentReference(contentReference);
        var normalizedSource = NormalizeSource(source, actorId);
        ValidateMetadata(metadata);

        var artifact = NewArtifact(issueId, parsedKind, normalizedTitle, normalizedReference, normalizedSource, actorId, dedupKey: null, metadata);
        db.Artifacts.Add(artifact);
        RecordActivity(artifact, ActivityEventType.ArtifactAttached, actorId);
        await SaveChangesAsync(ct);

        await RecordAuditAsync(workspaceId, artifact, "ArtifactAttached", actorId, channel, ct);
        return ArtifactDto.FromArtifact(artifact);
    }

    public async Task<ArtifactDto> AttachArtifactAsync(
        IssueId issueId,
        string kind,
        string title,
        byte[] content,
        string? contentType,
        string source,
        MemberId? actorId = null,
        string? metadata = null,
        AuditChannel channel = AuditChannel.System,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var workspaceId = await ResolveWorkspaceAsync(issueId, ct);
        var parsedKind = ParseKind(kind);
        var normalizedTitle = NormalizeTitle(title);
        var normalizedSource = NormalizeSource(source, actorId);
        ValidateMetadata(metadata);

        // Every validation above runs before the store is touched, and the store call runs while
        // the change tracker is still empty. A store outage therefore has nothing to roll back —
        // which is the whole of AC-ART-104.
        string reference;
        try
        {
            reference = await store.StoreAsync(content, contentType, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ArtifactException(
                "ARTIFACT_STORE_UNAVAILABLE",
                "Artifact content could not be stored; no artifact was attached.");
        }

        var artifact = NewArtifact(issueId, parsedKind, normalizedTitle, reference, normalizedSource, actorId, dedupKey: null, metadata);
        db.Artifacts.Add(artifact);
        var activity = RecordActivity(artifact, ActivityEventType.ArtifactAttached, actorId);

        try
        {
            await SaveChangesAsync(ct);
        }
        catch
        {
            // The blob landed but the row did not. Detach first: the compensating delete shares this
            // DbContext, so leaving the failed insert tracked would replay it inside the cleanup.
            db.Entry(artifact).State = EntityState.Detached;
            db.Entry(activity).State = EntityState.Detached;
            await TryPurgeAsync(reference, ct);
            throw;
        }

        await RecordAuditAsync(workspaceId, artifact, "ArtifactAttached", actorId, channel, ct);
        return ArtifactDto.FromArtifact(artifact);
    }

    public async Task<IReadOnlyList<ArtifactDto>> ListArtifactsAsync(IssueId issueId, CancellationToken ct = default)
    {
        var artifacts = await db.Artifacts.AsNoTracking()
            .Where(artifact => artifact.IssueId == issueId)
            .ToListAsync(ct);

        // Ordered in memory rather than in SQL so the Id tie-break compares the same Guid values
        // every surface sees; SQLite would otherwise collate the stored text form.
        return [.. artifacts
            .OrderBy(artifact => artifact.CreatedAt)
            .ThenBy(artifact => artifact.Id.Value)
            .Select(ArtifactDto.FromArtifact)];
    }

    public async Task<ArtifactDto> RefreshArtifactAsync(
        IssueId issueId,
        string kind,
        string dedupKey,
        string title,
        string contentReference,
        string source,
        string? metadata = null,
        CancellationToken ct = default)
    {
        var workspaceId = await ResolveWorkspaceAsync(issueId, ct);
        var parsedKind = ParseKind(kind);
        if (parsedKind != ArtifactKind.PullRequest)
        {
            throw new ArtifactException(
                "VALIDATION_FAILED",
                $"Artifacts of kind '{ArtifactKindConverter.ToWireValue(parsedKind)}' are not refreshable.");
        }

        var normalizedDedupKey = NormalizeDedupKey(dedupKey);
        var normalizedTitle = NormalizeTitle(title);
        var normalizedReference = NormalizeContentReference(contentReference);
        var normalizedSource = NormalizeSource(source, actorId: null);
        ValidateMetadata(metadata);

        var existing = await db.Artifacts.FirstOrDefaultAsync(
            artifact => artifact.IssueId == issueId
                && artifact.Kind == parsedKind
                && artifact.DedupKey == normalizedDedupKey,
            ct);

        if (existing is not null)
        {
            ApplyRefresh(existing, normalizedTitle, normalizedReference, metadata);
            RecordActivity(existing, ActivityEventType.ArtifactRefreshed, actorId: null);
            await SaveChangesAsync(ct);
            await RecordAuditAsync(workspaceId, existing, "ArtifactRefreshed", actorId: null, AuditChannel.System, ct);
            return ArtifactDto.FromArtifact(existing);
        }

        var inserted = NewArtifact(
            issueId, parsedKind, normalizedTitle, normalizedReference, normalizedSource,
            actorId: null, normalizedDedupKey, metadata);
        db.Artifacts.Add(inserted);
        var activity = RecordActivity(inserted, ActivityEventType.ArtifactAttached, actorId: null);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent delivery for the same pull request may have won the (IssueId, DedupKey)
            // unique index. The index *is* the concurrency control here: rather than escalating to a
            // transaction, converge on the winner's row and apply the update path instead.
            db.Entry(inserted).State = EntityState.Detached;
            db.Entry(activity).State = EntityState.Detached;

            var winner = await db.Artifacts.FirstOrDefaultAsync(
                artifact => artifact.IssueId == issueId
                    && artifact.Kind == parsedKind
                    && artifact.DedupKey == normalizedDedupKey,
                ct);
            if (winner is null)
            {
                throw new ArtifactException(
                    "VALIDATION_FAILED", $"The artifact could not be persisted: {ex.GetBaseException().Message}");
            }

            logger.LogWarning(
                "Concurrent refresh for an artifact dedup key on issue {IssueId} lost the insert race; converging on the existing row.",
                issueId.Value);

            ApplyRefresh(winner, normalizedTitle, normalizedReference, metadata);
            RecordActivity(winner, ActivityEventType.ArtifactRefreshed, actorId: null);
            await SaveChangesAsync(ct);
            await RecordAuditAsync(workspaceId, winner, "ArtifactRefreshed", actorId: null, AuditChannel.System, ct);
            return ArtifactDto.FromArtifact(winner);
        }

        await RecordAuditAsync(workspaceId, inserted, "ArtifactAttached", actorId: null, AuditChannel.System, ct);
        return ArtifactDto.FromArtifact(inserted);
    }

    public async Task RemoveArtifactAsync(
        IssueId issueId,
        ArtifactId artifactId,
        MemberId? actorId = null,
        AuditChannel channel = AuditChannel.System,
        CancellationToken ct = default)
    {
        var artifact = await db.Artifacts.FirstOrDefaultAsync(
            candidate => candidate.Id == artifactId && candidate.IssueId == issueId, ct);
        if (artifact is null)
        {
            // Deliberately one code for both "already gone" and "belongs to another issue"
            // (AC-ART-109): a caller must not be able to probe for artifact IDs outside its scope.
            throw new ArtifactException("REFERENCED_ENTITY_NOT_FOUND", "The artifact was not found for this issue.");
        }

        var workspaceId = await ResolveWorkspaceAsync(issueId, ct);
        var reference = artifact.ContentReference;

        db.Artifacts.Remove(artifact);
        RecordActivity(artifact, ActivityEventType.ArtifactRemoved, actorId);
        await SaveChangesAsync(ct);

        await RecordAuditAsync(workspaceId, artifact, "ArtifactRemoved", actorId, channel, ct);

        // Inverted relative to attach on purpose: the removal is already durable, so a failed purge
        // is reclaimable space rather than user-visible corruption (BR-ART-7).
        await TryPurgeAsync(reference, ct);
    }

    private static Artifact NewArtifact(
        IssueId issueId,
        ArtifactKind kind,
        string title,
        string contentReference,
        string source,
        MemberId? actorId,
        string? dedupKey,
        string? metadata)
    {
        var now = DateTimeOffset.UtcNow;
        return new Artifact
        {
            Id = ArtifactId.New(),
            IssueId = issueId,
            Kind = kind,
            Title = title,
            ContentReference = contentReference,
            Source = source,
            AddedById = actorId,
            DedupKey = dedupKey,
            Metadata = metadata,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Applied even when nothing changed: a refresh is idempotent, not a no-op, so the
    /// board can still show that the provider was heard from (§7.4).</summary>
    private static void ApplyRefresh(Artifact artifact, string title, string contentReference, string? metadata)
    {
        artifact.Title = title;
        artifact.ContentReference = contentReference;
        artifact.Metadata = metadata;
        artifact.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task<WorkspaceId> ResolveWorkspaceAsync(IssueId issueId, CancellationToken ct)
    {
        var workspaceId = await db.Issues
            .Where(issue => issue.Id == issueId)
            .Join(db.Teams, issue => issue.TeamId, team => team.Id, (_, team) => (WorkspaceId?)team.WorkspaceId)
            .FirstOrDefaultAsync(ct);

        return workspaceId
            ?? throw new ArtifactException("REFERENCED_ENTITY_NOT_FOUND", "The issue was not found.");
    }

    private static ArtifactKind ParseKind(string kind)
    {
        if (!ArtifactKindConverter.TryParse(kind, out var parsed))
        {
            throw new ArtifactException("VALIDATION_FAILED", $"'{kind}' is not a recognized artifact kind.");
        }

        return parsed;
    }

    private static string NormalizeTitle(string title) =>
        NormalizeRequired(title, TitleMaxLength, "title");

    private static string NormalizeContentReference(string contentReference) =>
        NormalizeRequired(contentReference, int.MaxValue, "contentReference");

    private static string NormalizeDedupKey(string dedupKey) =>
        NormalizeRequired(dedupKey, DedupKeyMaxLength, "dedupKey");

    private static string NormalizeSource(string? source, MemberId? actorId)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return actorId is not null
                ? LocalSource
                : throw new ArtifactException(
                    "VALIDATION_FAILED",
                    "An automation caller must supply an explicit artifact source key.");
        }

        return NormalizeRequired(source, SourceMaxLength, "source");
    }

    private static string NormalizeRequired(string value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArtifactException("VALIDATION_FAILED", $"Artifact {name} must not be empty.");
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArtifactException(
                "VALIDATION_FAILED", $"Artifact {name} must not exceed {maxLength} characters.");
        }

        return normalized;
    }

    private static void ValidateMetadata(string? metadata)
    {
        if (metadata is not null && Encoding.UTF8.GetByteCount(metadata) > MetadataMaxBytes)
        {
            throw new ArtifactException(
                "VALIDATION_FAILED", $"Artifact metadata must not exceed {MetadataMaxBytes} bytes.");
        }
    }

    private ActivityEvent RecordActivity(Artifact artifact, ActivityEventType type, MemberId? actorId)
    {
        var activity = new ActivityEvent
        {
            Id = ActivityEventId.New(),
            IssueId = artifact.IssueId,
            Type = type,
            ActorId = actorId,
            DataJson = JsonSerializer.Serialize(new
            {
                artifactId = artifact.Id.Value,
                kind = ArtifactKindConverter.ToWireValue(artifact.Kind),
                artifact.Title,
                artifact.Source,
                artifact.DedupKey,
            }),
            OccurredAt = DateTimeOffset.UtcNow,
        };

        db.ActivityEvents.Add(activity);
        return activity;
    }

    /// <summary>Summaries carry identifiers and provenance only — never the content reference or
    /// the metadata payload, either of which can leak private-repository detail (§11.3).</summary>
    private Task RecordAuditAsync(
        WorkspaceId workspaceId,
        Artifact artifact,
        string action,
        MemberId? actorId,
        AuditChannel channel,
        CancellationToken ct) =>
        audit.RecordAsync(new AuditEventRequest(
            workspaceId,
            actorId is { } member ? $"member:{member.Value}" : $"automation:{artifact.Source}",
            channel,
            action,
            "artifact",
            artifact.Id.Value.ToString(),
            correlation.CorrelationId,
            $"issueId={artifact.IssueId.Value};kind={ArtifactKindConverter.ToWireValue(artifact.Kind)};source={artifact.Source}"), ct);

    private async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            throw new ArtifactException("VALIDATION_FAILED", $"The artifact could not be persisted: {ex.GetBaseException().Message}");
        }
    }

    private async Task TryPurgeAsync(string reference, CancellationToken ct)
    {
        try
        {
            await store.DeleteAsync(reference, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Artifact content {Reference} could not be purged; the artifact itself is already gone.", reference);
        }
    }
}
