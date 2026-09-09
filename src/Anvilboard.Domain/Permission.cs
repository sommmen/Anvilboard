namespace Anvilboard.Domain;

/// <summary>
/// A single authorizable action within a workspace. <see cref="RolePermissionMap"/> is the
/// single source of truth for which <see cref="Role"/> grants which permissions — no handler is
/// allowed to hardcode its own check.
/// </summary>
public enum Permission
{
    /// <summary>Change workspace-level settings (name, slug, secrets).</summary>
    ManageWorkspaceConfig,

    /// <summary>Connect, reconfigure, or remove ingestion integrations (GitHub, Linear, ...).</summary>
    ManageIntegrations,

    /// <summary>Install, enable, or disable plugins.</summary>
    ManagePlugins,

    /// <summary>Trigger a backup or restore of the workspace's data.</summary>
    ManageBackupRestore,

    /// <summary>Read recorded audit events.</summary>
    ReadAudit,

    /// <summary>Archive or replace workflow states/transitions.</summary>
    ManageWorkflowStates,

    /// <summary>Issue, rotate, or revoke credentials (API tokens, sessions) for other members.</summary>
    ManageCredentials,

    /// <summary>Read and write issues without a team/assignment restriction.</summary>
    ReadWriteIssues,

    /// <summary>Read and write issues assigned to, or scoped to a team of, the acting member.</summary>
    ReadWriteAssignedIssues,

    /// <summary>Read and write comments on issues the actor can otherwise access.</summary>
    ReadWriteComments,

    /// <summary>Read the board.</summary>
    ReadBoard,

    /// <summary>Read the dashboard.</summary>
    ReadDashboard,

    /// <summary>Read integration health/status (not integration secrets).</summary>
    ReadIntegrationHealth,
}

/// <summary>
/// The fixed, static role → permission map described in the Workspace Authorization spec
/// (§ Key Behaviors). This is the single enforcement-relevant lookup table:
/// <c>WorkspaceAuthorizationService.AuthorizeAsync</c> never checks a permission any other way.
/// </summary>
public static class RolePermissionMap
{
    private static readonly IReadOnlyDictionary<Role, IReadOnlySet<Permission>> Map =
        new Dictionary<Role, IReadOnlySet<Permission>>
        {
            [Role.Administrator] = new HashSet<Permission>
            {
                Permission.ManageWorkspaceConfig,
                Permission.ManageIntegrations,
                Permission.ManagePlugins,
                Permission.ManageBackupRestore,
                Permission.ReadAudit,
                Permission.ManageWorkflowStates,
                Permission.ManageCredentials,
                Permission.ReadWriteIssues,
                Permission.ReadWriteAssignedIssues,
                Permission.ReadWriteComments,
                Permission.ReadBoard,
                Permission.ReadDashboard,
                Permission.ReadIntegrationHealth,
            },
            [Role.Coordinator] = new HashSet<Permission>
            {
                Permission.ReadWriteIssues,
                Permission.ReadWriteComments,
                Permission.ReadBoard,
                Permission.ReadDashboard,
                Permission.ReadIntegrationHealth,
            },
            [Role.Contributor] = new HashSet<Permission>
            {
                Permission.ReadWriteAssignedIssues,
                Permission.ReadWriteComments,
                Permission.ReadBoard,
                Permission.ReadDashboard,
            },
            [Role.AutomationAgent] = new HashSet<Permission>
            {
                Permission.ReadWriteIssues,
                Permission.ReadWriteComments,
                Permission.ReadBoard,
                Permission.ReadDashboard,
            },
        };

    /// <summary>
    /// The permissions a <paramref name="role"/> grants by default, before any
    /// <see cref="Role.AutomationAgent"/> token-scoped subset is intersected in.
    /// </summary>
    public static IReadOnlySet<Permission> PermissionsFor(Role role) => Map[role];

    public static bool Grants(Role role, Permission permission) => Map[role].Contains(permission);
}
