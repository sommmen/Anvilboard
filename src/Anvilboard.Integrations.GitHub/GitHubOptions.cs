namespace Anvilboard.Integrations.GitHub;

/// <summary>Configuration bound from the <c>Plugins:github</c> configuration section.</summary>
public sealed class GitHubOptions
{
    public bool Enabled { get; set; }

    /// <summary>Personal access token or fine-grained token with issues:read (and write for hooks that comment back).</summary>
    public string? Token { get; set; }

    /// <summary>Repositories to poll, as "owner/repo" strings.</summary>
    public List<string> Repositories { get; set; } = [];

    /// <summary>Local team key new issues from any of <see cref="Repositories"/> should be filed under.</summary>
    public string TeamKey { get; set; } = "ENG";

    /// <summary>Shared secret configured on the GitHub webhook, used to verify the X-Hub-Signature-256 header.</summary>
    public string? WebhookSecret { get; set; }

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);
}
