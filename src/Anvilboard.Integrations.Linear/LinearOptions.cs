namespace Anvilboard.Integrations.Linear;

/// <summary>Configuration bound from the <c>Plugins:linear</c> configuration section.</summary>
public sealed class LinearOptions
{
    public bool Enabled { get; set; }

    /// <summary>API key for the remote issue-tracking GraphQL API.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Remote team key(s) to poll, e.g. "ENG".</summary>
    public List<string> TeamKeys { get; set; } = [];

    /// <summary>Local team key synced issues should be filed under.</summary>
    public string TeamKey { get; set; } = "ENG";

    /// <summary>Shared secret used to verify the inbound webhook signature.</summary>
    public string? WebhookSecret { get; set; }

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);
}
