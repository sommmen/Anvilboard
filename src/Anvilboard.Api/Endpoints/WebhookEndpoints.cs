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
                trustedWorkspaceId = await db.Teams
                    .Where(team => team.Key == result.TeamKey)
                    .Select(team => (WorkspaceId?)team.WorkspaceId)
                    .SingleOrDefaultAsync(ct)
                    ?? throw new InvalidOperationException($"No local team with key '{result.TeamKey}' is configured for this webhook.");
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
                // Prefer the workspaces this delivery actually touched. Webhooks carry no tenant
                // routing of their own, so for a delivery that upserted no issue (a repository-level
                // event such as a merged pull request) we fall back to the sole workspace — and only
                // if there is exactly one. That is the documented bootstrap invariant made explicit
                // rather than assumed: picking an unordered first row would notify an arbitrary
                // tenant, and before bootstrap it would publish against Guid.Empty.
                var workspaceIds = await db.Teams
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
