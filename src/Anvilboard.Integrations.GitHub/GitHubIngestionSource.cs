using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Anvilboard.Domain;
using Anvilboard.Plugins.Abstractions;
using Microsoft.Extensions.Options;

namespace Anvilboard.Integrations.GitHub;

/// <summary>
/// Polls the GitHub REST Issues API (deliberately raw <see cref="HttpClient"/> + source-generated
/// JSON rather than a full SDK, keeping this integration's footprint tiny) for every repository in
/// <see cref="GitHubOptions.Repositories"/> and yields them as <see cref="NormalizedIssue"/>s.
/// Uses the "since" query parameter (backed by <see cref="SyncCursor"/>) for incremental polling.
/// </summary>
public sealed class GitHubIngestionSource(HttpClient httpClient, IOptionsMonitor<GitHubOptions> options) : IIngestionSource
{
    public PluginManifest Manifest { get; } = new("github", "GitHub Issues", "1.0.0");

    public async IAsyncEnumerable<NormalizedIssue> SyncAsync(SyncCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled || opts.Repositories.Count == 0)
        {
            yield break;
        }

        var since = cursor.Token is { Length: > 0 } token && DateTimeOffset.TryParse(token, out var parsed)
            ? parsed
            : (DateTimeOffset?)null;

        foreach (var repo in opts.Repositories)
        {
            await foreach (var issue in FetchRepositoryIssuesAsync(repo, since, opts, cancellationToken))
            {
                yield return issue;
            }
        }
    }

    private async IAsyncEnumerable<NormalizedIssue> FetchRepositoryIssuesAsync(
        string repo,
        DateTimeOffset? since,
        GitHubOptions opts,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var url = $"repos/{repo}/issues?state=all&sort=updated&direction=asc&per_page=50";
        if (since is { } s)
        {
            url += $"&since={Uri.EscapeDataString(s.UtcDateTime.ToString("o"))}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request, opts);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var items = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.GitHubIssueDtoArray, cancellationToken)
            ?? [];

        foreach (var dto in items)
        {
            // GitHub's issues endpoint also returns pull requests; those aren't board work items here.
            if (dto.PullRequest is not null)
            {
                continue;
            }

            yield return dto.ToNormalizedIssue(repo, opts.TeamKey);
        }
    }

    private static void ApplyAuth(HttpRequestMessage request, GitHubOptions opts)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Anvilboard", "1.0"));
        if (!string.IsNullOrWhiteSpace(opts.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.Token);
        }
    }
}

internal sealed class GitHubIssueDto
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string? Body { get; set; }
    public string State { get; set; } = "open";
    public string? HtmlUrl { get; set; }
    public GitHubUserDto? Assignee { get; set; }
    public List<GitHubLabelDto> Labels { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("pull_request")]
    public object? PullRequest { get; set; }

    public NormalizedIssue ToNormalizedIssue(string repo, string teamKey) => new(
        Provider: IntegrationProvider.GitHub,
        SourceKey: $"{repo}#{Number}",
        TeamKey: teamKey,
        Title: Title,
        Description: Body,
        SuggestedStatus: State == "closed" ? IssueStatus.Done : IssueStatus.Backlog,
        SuggestedPriority: IssuePriority.None,
        Url: HtmlUrl,
        AssigneeEmail: null, // GitHub's API exposes a login, not an email; mapping is left to the host's member-matching config.
        LabelNames: Labels.Select(l => l.Name).ToList(),
        SyncFingerprint: $"{State}:{UpdatedAt:O}",
        RemoteUpdatedAt: UpdatedAt);
}

internal sealed class GitHubUserDto
{
    public string? Login { get; set; }
}

internal sealed class GitHubLabelDto
{
    public string Name { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubIssueDto[]))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
