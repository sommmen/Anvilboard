using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anvilboard.Domain;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Integrations.Linear;

/// <summary>
/// Handles inbound "Issue" webhook events, mounted by the host at <c>/webhooks/linear</c>.
/// Verifies the provider's HMAC signature header before trusting the payload.
/// </summary>
public sealed class LinearWebhookReceiver(IOptionsMonitor<LinearOptions> options) : IWebhookReceiver
{
    public PluginManifest Manifest { get; } = new("linear", "Linear-style Issue Sync", "1.0.0");

    public string RoutePrefix => "linear";

    public Task<WebhookResult> HandleAsync(WebhookRequest request, CancellationToken cancellationToken)
    {
        var opts = options.CurrentValue;
        if (!string.IsNullOrEmpty(opts.WebhookSecret) && !IsSignatureValid(request, opts.WebhookSecret))
        {
            return Task.FromResult(WebhookResult.Reject("Invalid signature."));
        }

        LinearWebhookEventDto? payload;
        try
        {
            payload = JsonSerializer.Deserialize(request.RawBody, LinearWebhookJsonContext.Default.LinearWebhookEventDto);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(WebhookResult.Reject($"Malformed payload: {ex.Message}"));
        }

        if (payload?.Type != "Issue" || payload.Data is null)
        {
            return Task.FromResult(WebhookResult.Accept());
        }

        var normalized = payload.Data.ToNormalizedIssue(opts.TeamKey);
        return Task.FromResult(WebhookResult.Accept(issues: [normalized]));
    }

    private static bool IsSignatureValid(WebhookRequest request, string secret)
    {
        var headerEntry = request.Headers.FirstOrDefault(h => string.Equals(h.Key, "Linear-Signature", StringComparison.OrdinalIgnoreCase));
        if (headerEntry.Value is not { } header)
        {
            return false;
        }

        var expected = Convert.FromHexString(header);
        var actual = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(request.RawBody));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

internal sealed class LinearWebhookEventDto
{
    public string? Type { get; set; }
    public LinearIssueDto? Data { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LinearWebhookEventDto))]
internal sealed partial class LinearWebhookJsonContext : JsonSerializerContext;
