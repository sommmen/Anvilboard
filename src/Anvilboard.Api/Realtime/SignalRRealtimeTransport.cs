using Anvilboard.Application.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Anvilboard.Api.Realtime;

/// <summary>
/// Sends already-coalesced changes to the one workspace group they belong to. Addressing the group
/// (never <c>Clients.All</c>) is the delivery-side half of workspace isolation: even a mis-scoped
/// envelope could only ever reach members of its own workspace (AC-RT-002).
/// </summary>
public sealed class SignalRRealtimeTransport(IHubContext<WorkspaceRealtimeHub> hubContext) : IRealtimeTransport
{
    public Task SendAsync(RealtimeChange change, CancellationToken ct = default) =>
        hubContext.Clients
            .Group(WorkspaceRealtimeHub.GroupNameFor(change.WorkspaceId))
            .SendAsync(WorkspaceRealtimeHub.ChangeMethodName, RealtimeChangeEnvelope.From(change), ct);
}
