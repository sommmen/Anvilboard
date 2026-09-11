using Anvilboard.Api.Authorization;
using Anvilboard.Api.Middleware;
using Anvilboard.Application.Automation;
using Anvilboard.Application.Backup;
using Anvilboard.Domain;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// REST surface for whole-instance backup and restore (`docs/plans/backup-and-restore.md` §9.1,
/// §9.2). Every route requires <see cref="Permission.ManageBackupRestore"/> (Administrator-only per
/// <see cref="RolePermissionMap"/>), enforced by <see cref="WorkspaceAuthorizationMiddleware"/>
/// before any handler below runs (§11.2 single enforcement point) — no handler repeats that check.
/// Thin adapter over <see cref="IBackupService"/>: every route builds an explicit
/// <see cref="BackupOperationContext"/> from the authenticated actor, <see cref="AuditChannel.Rest"/>,
/// and the request's <see cref="CorrelationContext"/> rather than letting the service infer them
/// (plan §8.2). Error mapping reuses the existing §7.7 catalog via
/// <see cref="ErrorCodeCatalog.HttpStatusFor"/> — no new response envelope is introduced. The five
/// documented artifact-integrity causes on <see cref="RestoreResult"/>/<see cref="BackupVerification"/>
/// are examined-and-rejected outcomes, not thrown exceptions, so they are mapped to
/// <c>422 BACKUP_INTEGRITY_INVALID</c> directly from the returned value instead of a catch clause.
/// </summary>
public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backups").WithTags("Backups").RequirePermission(Permission.ManageBackupRestore);

        group.MapGet("/", async (HttpContext http, IBackupService backups, CancellationToken ct) =>
        {
            var workspaceId = http.GetActorContext().WorkspaceId;
            try
            {
                var manifests = await backups.ListBackupsAsync(workspaceId, ct);
                return Results.Ok(manifests);
            }
            catch (BackupOperationException ex)
            {
                return ToProblem(ex, http);
            }
        });

        group.MapPost("/", async (HttpContext http, IBackupService backups, CorrelationContext correlation, CancellationToken ct) =>
        {
            var actor = http.GetActorContext();
            var operation = new BackupOperationContext(actor.MemberId.Value.ToString(), AuditChannel.Rest, correlation.CorrelationId);
            try
            {
                var manifest = await backups.CreateBackupAsync(actor.WorkspaceId, operation, ct);
                return Results.Created($"/api/backups/{manifest.BackupId}", manifest);
            }
            catch (BackupOperationException ex)
            {
                return ToProblem(ex, http);
            }
        });

        group.MapPost("/{backupId:guid}/verify", async (Guid backupId, HttpContext http, IBackupService backups, CancellationToken ct) =>
        {
            var workspaceId = http.GetActorContext().WorkspaceId;
            try
            {
                var verification = await backups.VerifyBackupAsync(workspaceId, new BackupArtifactRef(backupId), ct);
                return verification.Verified ? Results.Ok(verification) : IntegrityProblem(verification.FailedCheck, verification.Detail);
            }
            catch (BackupOperationException ex)
            {
                return ToProblem(ex, http);
            }
        });

        group.MapPost("/{backupId:guid}/restore", async (
            Guid backupId, RestoreBackupRequest request, HttpContext http, IBackupService backups, CorrelationContext correlation, CancellationToken ct) =>
        {
            var actor = http.GetActorContext();
            var operation = new BackupOperationContext(actor.MemberId.Value.ToString(), AuditChannel.Rest, correlation.CorrelationId);
            try
            {
                var result = await backups.RestoreAsync(
                    actor.WorkspaceId, new BackupArtifactRef(backupId), request.ConfirmedWorkspaceSlug, operation, ct);
                return result.Success ? Results.Ok(result) : IntegrityProblem(result.FailedCheck, result.Detail);
            }
            catch (BackupOperationException ex)
            {
                return ToProblem(ex, http);
            }
        });
    }

    /// <summary>Maps a precondition failure thrown by <see cref="IBackupService"/> to the existing
    /// §7.7 catalog status code, attaching <c>Retry-After</c> for the restore-already-in-progress
    /// case so it advertises the same retry guidance as <see cref="DatabaseOperationMiddleware"/>'s
    /// admission-closed rejection.</summary>
    private static IResult ToProblem(BackupOperationException ex, HttpContext http)
    {
        if (ex.ErrorCode == BackupOperationException.RateLimited)
        {
            http.Response.Headers["Retry-After"] = DatabaseOperationMiddleware.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return Results.Problem(title: ex.ErrorCode, detail: ex.Message, statusCode: ErrorCodeCatalog.HttpStatusFor(ex.ErrorCode));
    }

    /// <summary>Maps an examined-and-rejected artifact (a populated <c>FailedCheck</c> on
    /// <see cref="RestoreResult"/> or <see cref="BackupVerification"/>) to the documented
    /// <c>422 BACKUP_INTEGRITY_INVALID</c> response, surfacing the stable machine-readable cause
    /// as a top-level <c>failedCheck</c> field (plan §9.2).</summary>
    private static IResult IntegrityProblem(string? failedCheck, string? detail) =>
        Results.Problem(
            title: "BACKUP_INTEGRITY_INVALID",
            detail: detail,
            statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?> { ["failedCheck"] = failedCheck });
}

public sealed record RestoreBackupRequest(string ConfirmedWorkspaceSlug);
