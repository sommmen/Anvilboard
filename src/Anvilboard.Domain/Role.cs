namespace Anvilboard.Domain;

/// <summary>
/// The fixed set of roles a <see cref="Member"/> can hold within a single <see cref="Workspace"/>.
/// Roles are workspace-scoped: the same person could in principle hold different roles in
/// different workspaces, though most self-hosted instances have exactly one workspace and one
/// administrator. See <see cref="Permission"/> for the fixed role → permission mapping.
/// </summary>
public enum Role
{
    /// <summary>Full workspace configuration, integration/plugin management, backup/restore,
    /// audit read, and the only role that can archive/replace workflow states or revoke
    /// credentials.</summary>
    Administrator,

    /// <summary>Read/write on issues, the board, and the dashboard, plus integration health
    /// read; cannot change workspace configuration or secrets.</summary>
    Coordinator,

    /// <summary>Read/write on assigned or team-scoped issues and comments; cannot reassign
    /// issues outside its own team scope.</summary>
    Contributor,

    /// <summary>Non-human actor (ingestion bot, coding agent) scoped to the permission subset
    /// granted to its API token at issuance time; never allowed to read secrets.</summary>
    AutomationAgent,
}
