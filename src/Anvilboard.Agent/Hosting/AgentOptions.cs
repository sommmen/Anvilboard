using Anvilboard.Application.Authorization;

namespace Anvilboard.Agent.Hosting;

/// <summary>
/// Host configuration for the CLI/MCP surface, bound from the <c>Agent</c> configuration section
/// (so <c>ANVILBOARD_AGENT__APITOKEN</c> or an <c>appsettings.json</c> <c>Agent:ApiToken</c> entry
/// both work).
/// </summary>
/// <remarks>
/// The token is deliberately <em>not</em> accepted as a command-line flag (plan <c>DR-AGT-001</c>):
/// a token in <c>argv</c> leaks into shell history and the OS process list, and
/// <c>OperationCommandLineAdapter</c> rejects flags that are not operation parameters, so a global
/// flag would additionally require host-side argument stripping.
/// </remarks>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>
    /// The API token the host authenticates with, minted by the REST host's
    /// <c>POST /api/auth/credentials</c>. Null or blank means "no credential presented", which
    /// every operation rejects with <c>AUTHENTICATION_REQUIRED</c>.
    /// </summary>
    public string? ApiToken { get; set; }
}

/// <summary>
/// Turns <see cref="AgentOptions"/> into the <see cref="ChannelCredential"/> the shared
/// <see cref="IWorkspaceAuthorizationService"/> understands, so the agent host presents its
/// credential through exactly the same type as the REST middleware.
/// </summary>
public sealed class AgentCredentialSource(AgentOptions options)
{
    /// <summary>
    /// The configured credential, or an empty credential when no token is configured. An empty
    /// credential authenticates as <c>AUTHENTICATION_REQUIRED</c> rather than throwing, so a
    /// misconfigured host still produces a catalog error rather than a stack trace.
    /// </summary>
    public ChannelCredential Current => string.IsNullOrWhiteSpace(options.ApiToken)
        ? new ChannelCredential()
        : ChannelCredential.FromApiToken(options.ApiToken);
}
