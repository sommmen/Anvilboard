using Anvilboard.Application.Issues;
using Anvilboard.Application.Realtime;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Anvilboard.Plugins.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// Single dynamic route, <c>POST /webhooks/{provider}</c>, dispatching to whichever registered
/// <see cref="IWebhookReceiver"/> claims that <see cref="IWebhookReceiver.RoutePrefix"/> — this is
/// the only place in the API host that knows about webhooks at all; everything provider-specific
/// (signature verification, payload shape) lives inside the plugin itself. Anonymous at the
/// workspace-authorization layer (external providers hold no Anvilboard credential) — each
/// receiver verifies its own provider-specific HMAC signature before trusting the payload.
/// </summary>
public static class WebhookEndpoints
{
    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/{provider}", async (
            string provider,
            HttpRequest httpRequest,
            IPluginRegistry plugins,
            IssueService issueService,
            ITrustedPluginEventPublisher pluginEvents,
            AnvilboardDbContext db,
            CancellationToken ct) =>
        {
            var receiver = plugins.WebhookReceivers.FirstOrDefault(r =>
                string.Equals(r.RoutePrefix, provider, StringComparison.OrdinalIgnoreCase));
            if (receiver is null)
            {
                return Results.NotFound($"No webhook receiver registered for '{provider}'.");
            }

            using var reader = new StreamReader(httpRequest.Body);
            var rawBody = await reader.ReadToEndAsync(ct);
            var headers = httpRequest.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());

            var result = await receiver.HandleAsync(new WebhookRequest(headers, rawBody), ct);
            if (!result.Accepted)
            {
                return Results.BadRequest(new { error = result.RejectionReason });
            }

            WorkspaceId? trustedWorkspaceId = null;
            if (result.TeamKey is not null)
            {
                var matchingWorkspaceIds = await db.Teams
                    .Where(team => team.Key == result.TeamKey)
                    .Select(team => team.WorkspaceId)
                    .Distinct()
                    .ToListAsync(ct);

                if (matchingWorkspaceIds.Count == 0)
                {
                    return Results.BadRequest(new { error = $"No local team with key '{result.TeamKey}' is configured for this webhook." });
                }

                if (matchingWorkspaceIds.Count > 1)
                {
                    // Team keys are unique only within a workspace (`TeamConfiguration` enforces
                    // (WorkspaceId, Key)), and this webhook payload carries no workspace identity of
                    // its own to pick among several same-keyed teams. Reject the delivery instead of
                    // guessing (or throwing an unhandled exception that would surface as a 500) so a
                    // host operator sees the misconfiguration and can rename one of the teams.
                    return Results.BadRequest(new
                    {
                        error = $"Team key '{result.TeamKey}' exists in {matchingWorkspaceIds.Count} workspaces; this webhook has no way to disambiguate which one it belongs to. Rename one of the teams so the key is unique host-wide.",
                    });
                }

                trustedWorkspaceId = matchingWorkspaceIds[0];
            }

            var touchedTeamIds = new List<TeamId>();
            foreach (var normalized in result.Issues)
            {
                var issue = await issueService.UpsertFromExternalAsync(normalized, trustedWorkspaceId, ct);
                touchedTeamIds.Add(issue.TeamId);
            }

            if (result.EventTypes.Count > 0)
            {
                // Relayed after any issue upsert, so a client re-fetching because of the event
                // observes the state the same delivery produced. The publisher never blocks and
                // never invokes lifecycle hooks (AC-RT-006).
                //
                // Prefer the delivery's own trusted workspace (resolved above from `TeamKey`) — this
                // is what lets a repository-level event with no issues (e.g. a merged pull request)
                // still reach the right tenant. Only fall back to the workspaces this delivery
                // actually touched, and finally to the sole-workspace bootstrap invariant, when the
                // webhook carried no team-key routing of its own. Picking an unordered first row
                // would notify an arbitrary tenant, and before bootstrap it would publish against
                // Guid.Empty, so both fallbacks require an unambiguous single candidate.
                List<WorkspaceId> workspaceIds;
                if (trustedWorkspaceId is not null)
                {
                    workspaceIds = [trustedWorkspaceId.Value];
                }
                else
                {
                    workspaceIds = await db.Teams
                        .AsNoTracking()
                        .Where(team => touchedTeamIds.Contains(team.Id))
                        .Select(team => team.WorkspaceId)
                        .Distinct()
                        .ToListAsync(ct);

                    if (workspaceIds.Count == 0)
                    {
                        var allWorkspaceIds = await db.Workspaces
                            .AsNoTracking()
                            .Select(workspace => workspace.Id)
                            .Take(2)
                            .ToListAsync(ct);

                        if (allWorkspaceIds.Count == 1)
                        {
                            workspaceIds = allWorkspaceIds;
                        }
                    }
                }

                foreach (var workspaceId in workspaceIds)
                {
                    foreach (var eventType in result.EventTypes)
                    {
                        pluginEvents.Publish(new PluginEvent(workspaceId, eventType));
                    }
                }
            }

            return Results.Ok(new { accepted = true, issuesProcessed = result.Issues.Count });
        }).WithTags("Webhooks").AllowAnonymous();
    }
}
