using Anvilboard.Api.Authorization;
using Anvilboard.Application.Automation;
using Anvilboard.Application.Issues;
using Anvilboard.Domain;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// The HTTP adapter over <see cref="IBoardQueryService"/>. The service has existed — registered and
/// fully implemented — since the board milestone but had no caller, so none of its grouping,
/// ordering, or filter vocabulary was reachable from the web UI (MAJ-007). This is that caller.
/// </summary>
/// <remarks>
/// Filter values are exchanged as <em>symbolic</em> tokens (<c>workflow_state</c>, <c>updated_at</c>)
/// rather than the ordinals the legacy <c>/api/issues</c> route uses, because these are closed
/// vocabularies that appear verbatim in a shareable URL: an ordinal would silently change meaning
/// the day an enum member is inserted.
/// </remarks>
public static class BoardEndpoints
{
    public static void MapBoardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/board").WithTags("Board").RequirePermission(Permission.ReadBoard);

        group.MapGet("/", async (
            [AsParameters] BoardQueryRequest request,
            IBoardQueryService service,
            RestWorkspaceScope scope,
            CancellationToken ct) =>
        {
            try
            {
                var workflowStateId = await scope.RequireWorkflowStateAsync(request.WorkflowStateId, ct);
                var assigneeId = await scope.RequireMemberAsync(request.AssigneeId, ct);
                var projectId = await scope.RequireProjectAsync(request.ProjectId, ct);
                var labelId = await scope.RequireLabelAsync(request.LabelId, ct);

                var query = new BoardQuery(
                    scope.WorkspaceId,
                    workflowStateId,
                    assigneeId,
                    ParseEnum<IntegrationProvider>(request.Provider, nameof(request.Provider)),
                    projectId,
                    request.Priority,
                    request.Type,
                    labelId,
                    ParseEnum<BoardSyncCondition>(request.SyncCondition, nameof(request.SyncCondition)),
                    ParseEnum<BoardGroupBy>(request.GroupBy, nameof(request.GroupBy)) ?? BoardGroupBy.WorkflowState,
                    ParseEnum<BoardOrderBy>(request.OrderBy, nameof(request.OrderBy)) ?? BoardOrderBy.CreatedAt,
                    request.Page ?? 1,
                    request.Limit ?? 25,
                    request.Cursor,
                    request.IncludeArchived ?? false);

                var result = await service.QueryAsync(query, ct);
                return Results.Ok(BoardResponse.From(result));
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
            catch (BoardQueryException ex)
            {
                return Results.Problem(
                    title: ex.ErrorCode,
                    detail: ex.Message,
                    statusCode: ErrorCodeCatalog.HttpStatusFor(ex.ErrorCode));
            }
        });
    }

    /// <summary>
    /// Rejects an unrecognised token rather than ignoring it. <c>groupBy</c>, <c>orderBy</c>,
    /// <c>syncCondition</c> and <c>provider</c> are closed vocabularies, so a typo that silently
    /// fell back to the default would hand back a board that looks plausible but answers a
    /// different question than the URL says. (Contrast <c>priority</c>/<c>type</c>, which are
    /// ordinary filters: an unknown value there correctly matches nothing.)
    /// </summary>
    private static TEnum? ParseEnum<TEnum>(string? value, string parameterName) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Underscores are stripped so the wire form can stay snake_case ("workflow_state") while the
        // enum member stays idiomatic C# ("WorkflowState").
        var candidate = value.Replace("_", string.Empty, StringComparison.Ordinal);

        return Enum.TryParse<TEnum>(candidate, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new BoardQueryException(
                $"'{value}' is not a recognized value for {parameterName}. Expected one of: "
                + string.Join(", ", Enum.GetNames<TEnum>().Select(ToSnakeCase)) + ".");
    }

    private static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}

/// <summary>
/// Flat nullable primitives so ASP.NET Core can bind the whole filter set from the query string in
/// one parameter. Nullability is how "not supplied" is distinguished from "supplied as the default",
/// which matters because the URL is the board's shareable state.
/// </summary>
public sealed record BoardQueryRequest(
    Guid? WorkflowStateId = null,
    Guid? AssigneeId = null,
    string? Provider = null,
    Guid? ProjectId = null,
    string? Priority = null,
    string? Type = null,
    Guid? LabelId = null,
    string? SyncCondition = null,
    string? GroupBy = null,
    string? OrderBy = null,
    int? Page = null,
    int? Limit = null,
    string? Cursor = null,
    bool? IncludeArchived = null);

/// <summary>
/// Transport projection of <see cref="BoardQueryResult"/>. The service returns strongly-typed ids
/// and enums; this flattens them to the plain <see cref="Guid"/>/string forms the SPA consumes, and
/// echoes the resolved query so a client can tell which filters actually took effect.
/// </summary>
public sealed record BoardResponse(
    IReadOnlyList<BoardGroupResponse> Groups,
    int TotalCount,
    int Page,
    int Limit,
    string? NextCursor,
    BoardAppliedQueryResponse AppliedQuery)
{
    public static BoardResponse From(BoardQueryResult result) => new(
        result.Groups.Select(BoardGroupResponse.From).ToList(),
        result.TotalCount,
        result.Page,
        result.Limit,
        result.NextCursor,
        BoardAppliedQueryResponse.From(result.AppliedQuery));
}

public sealed record BoardGroupResponse(string Key, string DisplayName, IReadOnlyList<BoardIssueResponse> Issues)
{
    public static BoardGroupResponse From(BoardGroup group) =>
        new(group.Key, group.DisplayName, group.Issues.Select(BoardIssueResponse.From).ToList());
}

public sealed record BoardIssueResponse(
    Guid Id,
    string Key,
    string Title,
    Guid WorkflowStateId,
    string? Type,
    string Priority,
    Guid? AssigneeId,
    Guid? ProjectId,
    string Provider,
    IReadOnlyList<Guid> LabelIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt)
{
    public static BoardIssueResponse From(BoardIssue issue) => new(
        issue.Id.Value,
        issue.Key,
        issue.Title,
        issue.WorkflowStateId.Value,
        issue.Type,
        issue.Priority.ToString(),
        issue.AssigneeId?.Value,
        issue.ProjectId?.Value,
        issue.Provider.ToString(),
        issue.LabelIds.Select(label => label.Value).ToList(),
        issue.CreatedAt,
        issue.UpdatedAt,
        issue.ArchivedAt);
}

public sealed record BoardAppliedQueryResponse(
    Guid? WorkflowStateId,
    Guid? AssigneeId,
    string? Provider,
    Guid? ProjectId,
    string? Priority,
    string? Type,
    Guid? LabelId,
    string? SyncCondition,
    string GroupBy,
    string OrderBy,
    int Page,
    int Limit,
    bool IncludeArchived)
{
    public static BoardAppliedQueryResponse From(BoardQuery query) => new(
        query.WorkflowStateId?.Value,
        query.AssigneeId?.Value,
        query.Provider?.ToString(),
        query.ProjectId?.Value,
        query.Priority,
        query.Type,
        query.LabelId?.Value,
        query.SyncCondition?.ToString(),
        query.GroupBy.ToString(),
        query.OrderBy.ToString(),
        query.Page,
        query.Limit,
        query.IncludeArchived);
}
