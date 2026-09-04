using Anvilboard.Application.Issues;
using Anvilboard.Plugins.Abstractions;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// Single dynamic route, <c>POST /webhooks/{provider}</c>, dispatching to whichever registered
/// <see cref="IWebhookReceiver"/> claims that <see cref="IWebhookReceiver.RoutePrefix"/> — this is
/// the only place in the API host that knows about webhooks at all; everything provider-specific
/// (signature verification, payload shape) lives inside the plugin itself.
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

            foreach (var normalized in result.Issues)
            {
                await issueService.UpsertFromExternalAsync(normalized, ct);
            }

            return Results.Ok(new { accepted = true, issuesProcessed = result.Issues.Count });
        }).WithTags("Webhooks");
    }
}
