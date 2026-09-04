using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Anvilboard.Domain;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Integrations.Linear;

/// <summary>
/// Polls a remote GraphQL issue-tracking API for issues in <see cref="LinearOptions.TeamKeys"/>
/// updated since the last <see cref="SyncCursor"/>, mapping each into a <see cref="NormalizedIssue"/>.
/// Deliberately a single hand-written query (no generated client) to keep this integration small.
/// </summary>
public sealed class LinearIngestionSource(HttpClient httpClient, IOptionsMonitor<LinearOptions> options) : IIngestionSource
{
    private const string Query = """
        query Issues($after: DateTimeOrDuration) {
          issues(filter: { updatedAt: { gt: $after } }, orderBy: updatedAt) {
            nodes {
              identifier
              title
              description
              url
              updatedAt
              state { name type }
              priority
              assignee { email }
              labels { nodes { name } }
              team { key }
            }
          }
        }
        """;

    public PluginManifest Manifest { get; } = new("linear", "Linear-style Issue Sync", "1.0.0");

    public async IAsyncEnumerable<NormalizedIssue> SyncAsync(SyncCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled || string.IsNullOrWhiteSpace(opts.ApiKey))
        {
            yield break;
        }

        var since = cursor.Token ?? DateTimeOffset.UtcNow.AddYears(-5).ToString("O");

        using var request = new HttpRequestMessage(HttpMethod.Post, "graphql")
        {
            Content = JsonContent.Create(new { query = Query, variables = new { after = since } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(opts.ApiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync(LinearJsonContext.Default.LinearGraphQlResponse, cancellationToken);
        var nodes = payload?.Data?.Issues?.Nodes ?? [];

        foreach (var node in nodes)
        {
            if (opts.TeamKeys.Count > 0 && node.Team is { Key: { } remoteKey } && !opts.TeamKeys.Contains(remoteKey))
            {
                continue;
            }

            yield return node.ToNormalizedIssue(opts.TeamKey);
        }
    }
}

internal sealed class LinearGraphQlResponse
{
    public LinearData? Data { get; set; }
}

internal sealed class LinearData
{
    public LinearIssueConnection? Issues { get; set; }
}

internal sealed class LinearIssueConnection
{
    public List<LinearIssueDto> Nodes { get; set; } = [];
}

internal sealed class LinearIssueDto
{
    public string Identifier { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public LinearStateDto? State { get; set; }
    public int? Priority { get; set; }
    public LinearAssigneeDto? Assignee { get; set; }
    public LinearLabelConnection? Labels { get; set; }
    public LinearTeamDto? Team { get; set; }

    public NormalizedIssue ToNormalizedIssue(string localTeamKey) => new(
        Provider: IntegrationProvider.Linear,
        SourceKey: Identifier,
        TeamKey: localTeamKey,
        Title: Title,
        Description: Description,
        SuggestedStatus: MapStatus(State?.Type),
        SuggestedPriority: MapPriority(Priority),
        Url: Url,
        AssigneeEmail: Assignee?.Email,
        LabelNames: Labels?.Nodes.Select(l => l.Name).ToList() ?? [],
        SyncFingerprint: UpdatedAt.ToString("O"),
        RemoteUpdatedAt: UpdatedAt);

    private static IssueStatus MapStatus(string? stateType) => stateType switch
    {
        "backlog" => IssueStatus.Backlog,
        "unstarted" => IssueStatus.Todo,
        "started" => IssueStatus.InProgress,
        "completed" => IssueStatus.Done,
        "canceled" => IssueStatus.Cancelled,
        _ => IssueStatus.Backlog,
    };

    private static IssuePriority MapPriority(int? priority) => priority switch
    {
        1 => IssuePriority.Urgent,
        2 => IssuePriority.High,
        3 => IssuePriority.Medium,
        4 => IssuePriority.Low,
        _ => IssuePriority.None,
    };
}

internal sealed class LinearStateDto
{
    public string Name { get; set; } = "";
    public string? Type { get; set; }
}

internal sealed class LinearAssigneeDto
{
    public string? Email { get; set; }
}

internal sealed class LinearLabelConnection
{
    public List<LinearLabelDto> Nodes { get; set; } = [];
}

internal sealed class LinearLabelDto
{
    public string Name { get; set; } = "";
}

internal sealed class LinearTeamDto
{
    public string? Key { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LinearGraphQlResponse))]
internal sealed partial class LinearJsonContext : JsonSerializerContext;
