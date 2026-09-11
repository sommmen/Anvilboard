using Anvilboard.Api.Authorization;
using Anvilboard.Application.Realtime;
using Anvilboard.Domain;
using Microsoft.AspNetCore.SignalR;

namespace Anvilboard.Api.Realtime;

/// <summary>
/// The SignalR endpoint browsers connect to for live board/dashboard updates.
/// </summary>
/// <remarks>
/// Connections are authenticated and authorized by <see cref="WorkspaceAuthorizationMiddleware"/>
/// during the negotiate/connect request, exactly like every REST route, so an unauthorized caller
/// is refused before a connection exists rather than after one appears to have been established.
/// Group membership is then derived exclusively from that authenticated actor's own workspace — the
/// hub exposes no client-callable subscribe method at all, which is what makes cross-workspace
/// subscription impossible rather than merely rejected (AC-RT-002).
/// </remarks>
public sealed class WorkspaceRealtimeHub(RealtimeMetrics metrics) : Hub
{
    /// <summary>The SignalR method name clients handle to receive change envelopes.</summary>
    public const string ChangeMethodName = "change";

    public const string HubPath = "/hubs/workspace";

    /// <summary>
    /// The single denial code a rejected connection ever sees. Deliberately identical for an absent,
    /// malformed, expired, and unauthorized credential, so a caller cannot distinguish "no such
    /// workspace" from "not allowed" — the same non-disclosure rule REST denials follow.
    /// </summary>
    public const string AccessDeniedErrorCode = "WORKSPACE_ACCESS_DENIED";

    public static string GroupNameFor(WorkspaceId workspaceId) => $"workspace:{workspaceId}";

    public override async Task OnConnectedAsync()
    {
        // The negotiate/connect request already ran through WorkspaceAuthorizationMiddleware, which
        // refuses an unauthenticated or unauthorized caller before SignalR ever sees it. Reaching
        // here therefore means the actor is authorized; all that is left is to derive the group.
        var actor = Context.GetHttpContext()?.GetActorContext()
            ?? throw new HubException(AccessDeniedErrorCode);

        await Groups.AddToGroupAsync(
            Context.ConnectionId, GroupNameFor(actor.WorkspaceId), Context.ConnectionAborted);

        // Counted only after the connection is authorized and joined, so the gauge reflects
        // deliverable connections rather than handshake attempts.
        metrics.ConnectionOpened();
        Context.Items[nameof(WorkspaceId)] = actor.WorkspaceId;

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.Remove(nameof(WorkspaceId)))
        {
            metrics.ConnectionClosed();
        }

        // SignalR removes the connection from its groups automatically on disconnect.
        return base.OnDisconnectedAsync(exception);
    }
}
