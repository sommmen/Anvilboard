using Anvilboard.Api.Authorization;
using Anvilboard.Application.Auditing;
using Anvilboard.Application.Automation;
using Anvilboard.Domain;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// The HTTP adapter over <see cref="IAuditQueryService"/>. Audit rows have been written on every
/// security-sensitive action since the audit milestone but had no reader, so the trail was recorded
/// and never surfaced (MAJ-018). This is that reader's web entry point.
/// </summary>
/// <remarks>
/// There is deliberately no <c>workspaceId</c> parameter: the workspace comes from the
/// authenticated actor via <see cref="RestWorkspaceScope"/>, so the route cannot be aimed at
/// another tenant's history. Access is gated entirely by <c>WorkspaceAuthorizationMiddleware</c>
/// reading the <see cref="Permission.ReadAudit"/> metadata declared on the group, exactly as it is
/// for <c>/api/board</c> — no entity id is resolved here, so there is nothing for the handler
/// itself to authorize.
/// </remarks>
public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit-events").WithTags("Audit").RequirePermission(Permission.ReadAudit);

        group.MapGet("/", async (
            [AsParameters] AuditEventsRequest request,
            IAuditQueryService service,
            RestWorkspaceScope scope,
            CancellationToken ct) =>
        {
            try
            {
                var query = new AuditQuery(
                    request.ActorId,
                    request.OccurredAfter,
                    request.OccurredBefore,
                    request.TargetType,
                    request.TargetId,
                    request.Action,
                    ParseEnum<AuditChannel>(request.Channel, nameof(request.Channel)),
                    request.Limit,
                    request.Cursor);

                var result = await service.QueryAsync(scope.WorkspaceId, query, ct);
                return Results.Ok(result);
            }
            catch (AuditQueryException ex)
            {
                return Results.Problem(
                    title: ex.ErrorCode,
                    detail: ex.Message,
                    statusCode: ErrorCodeCatalog.HttpStatusFor(ex.ErrorCode));
            }
        });
    }

    /// <summary>
    /// Rejects an unrecognised token rather than ignoring it. <c>channel</c> is a closed vocabulary,
    /// so a typo that silently fell back to "no channel filter" would hand back a wider history than
    /// the URL claims — the opposite of what an investigator reading an audit trail needs.
    /// (Contrast <c>action</c>/<c>targetType</c>, which are open-ended strings: an unknown value
    /// there correctly matches nothing.)
    /// </summary>
    private static TEnum? ParseEnum<TEnum>(string? value, string parameterName) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new AuditQueryException(
                "VALIDATION_FAILED",
                $"'{value}' is not a recognized value for {parameterName}. Expected one of: "
                + string.Join(", ", Enum.GetNames<TEnum>()) + ".");
    }
}

/// <summary>
/// Flat nullable primitives so ASP.NET Core can bind the whole filter set from the query string in
/// one parameter. Nullability is how "not supplied" is distinguished from "supplied as the
/// default", which is what lets <c>AuditQueryResult.AppliedQuery</c> echo back the defaults the
/// service actually resolved.
/// </summary>
public sealed record AuditEventsRequest(
    string? ActorId = null,
    DateTimeOffset? OccurredAfter = null,
    DateTimeOffset? OccurredBefore = null,
    string? TargetType = null,
    string? TargetId = null,
    string? Action = null,
    string? Channel = null,
    int? Limit = null,
    string? Cursor = null);
