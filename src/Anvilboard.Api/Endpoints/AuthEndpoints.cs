using Anvilboard.Api.Authorization;
using Anvilboard.Application.Authorization;

namespace Anvilboard.Api.Endpoints;

/// <summary>
/// Bootstrap, login, and logout for the web SPA's session cookie flow. The only routes in the
/// host that run before/without an authenticated <see cref="ActorContext"/> — every other
/// endpoint is gated by <see cref="WorkspaceAuthorizationMiddleware"/>.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/bootstrap", async (BootstrapRequestBody body, HttpContext context, IWorkspaceAuthorizationService authService, CancellationToken ct) =>
        {
            try
            {
                var actor = await authService.BootstrapFirstAdministratorAsync(
                    new BootstrapRequest(
                        body.WorkspaceName,
                        body.WorkspaceSlug,
                        body.AdministratorDisplayName,
                        body.AdministratorUsername,
                        body.AdministratorPassword,
                        body.AdministratorEmail),
                    ct);

                var session = await authService.IssueSessionAsync(actor, ct);
                SetSessionCookie(context, session);
                return Results.Ok(new { memberId = actor.MemberId, workspaceId = actor.WorkspaceId, role = actor.Role });
            }
            catch (WorkspaceAuthorizationException ex)
            {
                return Results.Problem(title: ex.ErrorCode, detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).AllowAnonymous();

        group.MapPost("/login", async (LoginRequestBody body, HttpContext context, IWorkspaceAuthorizationService authService, CancellationToken ct) =>
        {
            var authentication = await authService.AuthenticateAsync(
                ChannelCredential.FromUsernamePassword(body.Username, body.Password), ct);
            if (!authentication.IsAuthenticated || authentication.Actor is not { } actor)
            {
                return Results.Problem(
                    title: authentication.ErrorCode ?? "CREDENTIAL_INVALID_OR_EXPIRED",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var session = await authService.IssueSessionAsync(actor, ct);
            SetSessionCookie(context, session);
            return Results.Ok(new { memberId = actor.MemberId, workspaceId = actor.WorkspaceId, role = actor.Role });
        }).AllowAnonymous();

        group.MapPost("/logout", async (HttpContext context, IWorkspaceAuthorizationService authService, CancellationToken ct) =>
        {
            if (context.GetActorContext() is { ApiTokenId: { } tokenId } actor)
            {
                await authService.RevokeCredentialAsync(actor.WorkspaceId, actor.MemberId, tokenId, ct);
            }

            context.Response.Cookies.Delete(WorkspaceAuthorizationMiddleware.SessionCookieName);
            return Results.NoContent();
        });
    }

    private static void SetSessionCookie(HttpContext context, SessionIssuedResult session)
    {
        context.Response.Cookies.Append(WorkspaceAuthorizationMiddleware.SessionCookieName, session.RawToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = session.ExpiresAt,
        });
    }
}

public sealed record BootstrapRequestBody(
    string WorkspaceName,
    string WorkspaceSlug,
    string AdministratorDisplayName,
    string AdministratorUsername,
    string AdministratorPassword,
    string? AdministratorEmail = null);

public sealed record LoginRequestBody(string Username, string Password);
