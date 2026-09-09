namespace Anvilboard.Application.Authorization;

/// <summary>
/// The one-time request that creates the first <see cref="Anvilboard.Domain.Workspace"/> and its
/// first <see cref="Anvilboard.Domain.Role.Administrator"/> <see cref="Anvilboard.Domain.Member"/>.
/// Rejected with <c>VALIDATION_FAILED</c> once any workspace already exists (AC-106).
/// </summary>
public sealed record BootstrapRequest(
    string WorkspaceName,
    string WorkspaceSlug,
    string AdministratorDisplayName,
    string AdministratorUsername,
    string AdministratorPassword,
    string? AdministratorEmail = null);
