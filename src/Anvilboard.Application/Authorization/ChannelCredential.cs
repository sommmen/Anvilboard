namespace Anvilboard.Application.Authorization;

/// <summary>
/// The raw credential presented by a caller, in whichever shape the calling channel (REST, CLI,
/// MCP) received it. Exactly one of <see cref="ApiToken"/> or (<see cref="Username"/> and
/// <see cref="Password"/>) is expected to be set; <see cref="IWorkspaceAuthorizationService.AuthenticateAsync"/>
/// treats any other combination as malformed. This type is never persisted or logged — only its
/// resolved hash is ever compared against storage.
/// </summary>
public sealed record ChannelCredential
{
    /// <summary>A workspace-scoped API token bearer value, as used by automation.</summary>
    public string? ApiToken { get; init; }

    /// <summary>A local human username, used together with <see cref="Password"/>.</summary>
    public string? Username { get; init; }

    /// <summary>A local human password, used together with <see cref="Username"/>.</summary>
    public string? Password { get; init; }

    public static ChannelCredential FromApiToken(string token) => new() { ApiToken = token };

    public static ChannelCredential FromUsernamePassword(string username, string password) =>
        new() { Username = username, Password = password };
}
