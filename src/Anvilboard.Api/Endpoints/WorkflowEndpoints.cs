using Anvilboard.Api.Authorization;
using Anvilboard.Api.Middleware;
using Anvilboard.Application.Automation;
using Anvilboard.Application.Workflows;
using Anvilboard.Domain;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// REST surface for workspace workflow configuration (<c>docs/plans/workflow-admin-surface.md</c>
/// §9.1) — the first enforcement site for <see cref="Permission.ManageWorkflowStates"/>, which
/// <see cref="RolePermissionMap"/> grants to Administrators only.
/// </summary>
/// <remarks>
/// <para>
/// Reads sit at <see cref="Permission.ReadBoard"/> because any board client needs the column set to
/// render; writes are layered on top per route, matching the split already used by
/// <see cref="IssueEndpoints"/>. Enforcement happens once, in
/// <see cref="WorkspaceAuthorizationMiddleware"/>, so no handler repeats the check.
/// </para>
/// <para>
/// Thin adapter over <see cref="IWorkflowService"/>: each mutation builds an explicit
/// <see cref="WorkflowOperationContext"/> from the authenticated actor,
/// <see cref="AuditChannel.Rest"/>, and the request's <see cref="CorrelationContext"/>, and the
/// service — not this file — writes the audit event.
/// </para>
/// </remarks>
public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workflow").WithTags("Workflow").RequirePermission(Permission.ReadBoard);

        group.MapGet("/states", async (
            HttpContext http, IWorkflowService workflow, bool includeArchived = false, CancellationToken ct = default) =>
        {
            var workspaceId = http.GetActorContext().WorkspaceId;
            return Results.Ok(await workflow.ListWorkflowStatesAsync(workspaceId, includeArchived, ct));
        });

        group.MapGet("/transitions", async (HttpContext http, IWorkflowService workflow, CancellationToken ct) =>
        {
            var workspaceId = http.GetActorContext().WorkspaceId;
            return Results.Ok(await workflow.ListWorkflowTransitionsAsync(workspaceId, ct));
        });

        group.MapPost("/states", async (
            CreateWorkflowStateRequest request,
            HttpContext http,
            IWorkflowService workflow,
            CorrelationContext correlation,
            CancellationToken ct) =>
        {
            try
            {
                var state = await workflow.CreateWorkflowStateAsync(
                    http.GetActorContext().WorkspaceId,
                    request.Key,
                    request.DisplayName,
                    request.Order,
                    request.IsTerminal,
                    Operation(http, correlation),
                    ct);

                return Results.Created($"/api/workflow/states/{state.Id}", state);
            }
            catch (WorkflowValidationException ex)
            {
                return ToProblem(ex);
            }
        }).RequirePermission(Permission.ManageWorkflowStates);

        group.MapPatch("/states/{stateId:guid}", async (
            Guid stateId,
            UpdateWorkflowStateRequest request,
            HttpContext http,
            IWorkflowService workflow,
            RestWorkspaceScope scope,
            CorrelationContext correlation,
            CancellationToken ct) =>
        {
            try
            {
                var id = await scope.RequireWorkflowStateAsync(stateId, ct);
                var state = await workflow.UpdateWorkflowStateAsync(
                    http.GetActorContext().WorkspaceId,
                    id,
                    request.DisplayName,
                    request.Order,
                    request.IsTerminal,
                    Operation(http, correlation),
                    request.Key,
                    ct);

                return Results.Ok(state);
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
            catch (WorkflowValidationException ex)
            {
                return ToProblem(ex);
            }
        }).RequirePermission(Permission.ManageWorkflowStates);

        group.MapDelete("/states/{stateId:guid}", async (
            Guid stateId,
            HttpContext http,
            IWorkflowService workflow,
            RestWorkspaceScope scope,
            CorrelationContext correlation,
            Guid? replacementStateId = null,
            CancellationToken ct = default) =>
        {
            try
            {
                var id = await scope.RequireWorkflowStateAsync(stateId, ct);
                var replacement = replacementStateId is { } value
                    ? await scope.RequireWorkflowStateAsync(value, ct)
                    : (WorkflowStateId?)null;

                await workflow.ArchiveWorkflowStateAsync(
                    http.GetActorContext().WorkspaceId, id, replacement, Operation(http, correlation), ct);

                return Results.NoContent();
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
            catch (WorkflowValidationException ex)
            {
                return ToProblem(ex);
            }
        }).RequirePermission(Permission.ManageWorkflowStates);

        group.MapPost("/transitions", async (
            CreateWorkflowTransitionRequest request,
            HttpContext http,
            IWorkflowService workflow,
            RestWorkspaceScope scope,
            CorrelationContext correlation,
            CancellationToken ct) =>
        {
            try
            {
                var from = await scope.RequireWorkflowStateAsync(request.FromStateId, ct);
                var to = await scope.RequireWorkflowStateAsync(request.ToStateId, ct);

                var transition = await workflow.CreateWorkflowTransitionAsync(
                    http.GetActorContext().WorkspaceId, from, to, Operation(http, correlation), ct);

                return Results.Created($"/api/workflow/transitions/{transition.Id}", transition);
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
            catch (WorkflowValidationException ex)
            {
                return ToProblem(ex);
            }
        }).RequirePermission(Permission.ManageWorkflowStates);

        group.MapDelete("/transitions/{transitionId:guid}", async (
            Guid transitionId,
            HttpContext http,
            IWorkflowService workflow,
            RestWorkspaceScope scope,
            CorrelationContext correlation,
            CancellationToken ct) =>
        {
            try
            {
                var id = await scope.RequireWorkflowTransitionAsync(transitionId, ct);
                await workflow.RemoveWorkflowTransitionAsync(
                    http.GetActorContext().WorkspaceId, id, Operation(http, correlation), ct);

                return Results.NoContent();
            }
            catch (WorkspaceScopeDeniedException)
            {
                return WorkspaceScopeResults.Denied();
            }
            catch (WorkflowValidationException ex)
            {
                return ToProblem(ex);
            }
        }).RequirePermission(Permission.ManageWorkflowStates);
    }

    private static WorkflowOperationContext Operation(HttpContext http, CorrelationContext correlation) =>
        new(http.GetActorContext().MemberId.Value.ToString(), AuditChannel.Rest, correlation.CorrelationId);

    /// <summary>
    /// Maps a rejection onto the existing §7.7 catalog rather than a hand-written status switch, so
    /// a code added to the catalog cannot acquire a different meaning here.
    /// </summary>
    private static IResult ToProblem(WorkflowValidationException ex) =>
        Results.Problem(
            title: ex.ErrorCode, detail: ex.Message, statusCode: ErrorCodeCatalog.HttpStatusFor(ex.ErrorCode));
}

public sealed record CreateWorkflowStateRequest(
    string Key,
    string DisplayName,
    int Order = 0,
    bool IsTerminal = false);

/// <summary>
/// Sparse patch body: a null member means "leave unchanged". <see cref="Key"/> exists only so a
/// caller that tries to rename a state gets an explicit rejection instead of a silent no-op.
/// </summary>
public sealed record UpdateWorkflowStateRequest(
    string? DisplayName = null,
    int? Order = null,
    bool? IsTerminal = null,
    string? Key = null);

public sealed record CreateWorkflowTransitionRequest(Guid FromStateId, Guid ToStateId);
