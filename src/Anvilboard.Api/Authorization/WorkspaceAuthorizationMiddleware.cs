using Anvilboard.Application.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Primitives;

namespace Anvilboard.Api.Authorization;

/// <summary>
/// The single REST-side enforcement point for authentication and authorization (§11.2): every
/// request is authenticated here, and — when the matched endpoint declares a
/// <see cref="RequiresPermissionAttribute"/> — authorized here too, before any endpoint delegate
/// runs. No endpoint is allowed to perform its own ad hoc check.
/// </summary>
public sealed class WorkspaceAuthorizationMiddleware(RequestDelegate next)
{
    /// <summary>Name of the HTTP-only secure cookie carrying a hashed session credential for the
    /// web SPA (§ Included: "HTTP-only secure session cookie for the SPA").</summary>
    public const string SessionCookieName = "anvilboard_session";

    /// <summary><see cref="HttpContext.Items"/> key under which the resolved
    /// <see cref="ActorContext"/> is attached for downstream endpoint delegates to read.</summary>
    public const string ActorContextItemsKey = "Anvilboard.ActorContext";

    public async Task InvokeAsync(HttpContext context, IWorkspaceAuthorizationService authService)
    {
        var endpoint = context.GetEndpoint();

        // The bootstrap endpoint (and any other route explicitly opted out via `.AllowAnonymous()`)
        // has no credential to authenticate yet — it is the only path allowed to run
        // unauthenticated (§ Core Responsibilities #4: bootstrap flow).
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await next(context);
            return;
        }

        var credential = ExtractCredential(context.Request);
        var authentication = await authService.AuthenticateAsync(credential, context.RequestAborted);
        if (!authentication.IsAuthenticated || authentication.Actor is not { } actor)
        {
            await WriteDenialAsync(context, StatusCodes.Status401Unauthorized, authentication.ErrorCode ?? "AUTHENTICATION_REQUIRED");
            return;
        }

        var requiredPermission = endpoint?.Metadata.GetMetadata<RequiresPermissionAttribute>();
        if (requiredPermission is not null)
        {
            // Holding any one of the declared permissions is sufficient. Pre-checking the actor's
            // already-resolved EffectivePermissions() (no DB call) picks *which* permission to
            // pass to AuthorizeAsync, so exactly one authorization-decision event is emitted per
            // request (AC-104) even when a route declares more than one acceptable permission.
            var effectivePermissions = actor.EffectivePermissions();
            var permissionToCheck = requiredPermission.Permissions.FirstOrDefault(effectivePermissions.Contains, requiredPermission.Permissions[0]);

            var authorization = await authService.AuthorizeAsync(actor, actor.WorkspaceId, permissionToCheck, context.RequestAborted);
            if (!authorization.IsAuthorized)
            {
                await WriteDenialAsync(context, StatusCodes.Status403Forbidden, authorization.ErrorCode ?? "WORKSPACE_ACCESS_DENIED");
                return;
            }
        }

        context.Items[ActorContextItemsKey] = actor;
        await next(context);
    }

    private const string BearerPrefix = "Bearer ";

    private static ChannelCredential ExtractCredential(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Authorization", out StringValues authorizationHeader)
            && authorizationHeader.ToString() is { Length: > 0 } headerValue
            && headerValue.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            && headerValue[BearerPrefix.Length..] is { Length: > 0 } bearerToken)
        {
            return ChannelCredential.FromApiToken(bearerToken);
        }

        if (request.Cookies.TryGetValue(SessionCookieName, out var sessionToken) && !string.IsNullOrEmpty(sessionToken))
        {
            return ChannelCredential.FromApiToken(sessionToken);
        }

        return new ChannelCredential();
    }

    private static Task WriteDenialAsync(HttpContext context, int statusCode, string errorCode)
    {
        // No `data` field on denial, matching AC-002/AC-103 across every channel: only the stable
        // error code (and title) are ever disclosed.
        context.Response.StatusCode = statusCode;
        return Results.Problem(title: errorCode, statusCode: statusCode).ExecuteAsync(context);
    }
}

/// <summary>Endpoint-side accessor for the <see cref="ActorContext"/> this middleware attaches to
/// <see cref="HttpContext.Items"/> after successful authentication.</summary>
public static class HttpContextActorContextExtensions
{
    /// <summary>
    /// Returns the current request's authenticated <see cref="ActorContext"/>. Only valid to call
    /// from a route that runs after <see cref="WorkspaceAuthorizationMiddleware"/> has authenticated
    /// the request (i.e. not <c>[AllowAnonymous]</c>) — throws otherwise, since that would indicate
    /// a route wiring defect rather than a recoverable condition.
    /// </summary>
    public static ActorContext GetActorContext(this HttpContext context) =>
        context.Items[WorkspaceAuthorizationMiddleware.ActorContextItemsKey] as ActorContext
        ?? throw new InvalidOperationException(
            "No ActorContext on HttpContext.Items — this route must run after WorkspaceAuthorizationMiddleware and must not be [AllowAnonymous].");
}
