namespace Anvilboard.Application.Realtime;

/// <summary>
/// Delivers an already-coalesced <see cref="RealtimeChange"/> to connected clients. This is the only
/// interface a concrete transport (SignalR today) has to implement, and it is invoked exclusively by
/// <see cref="RealtimeDispatcher"/> on a background loop — never on a request/mutation thread.
/// </summary>
public interface IRealtimeTransport
{
    Task SendAsync(RealtimeChange change, CancellationToken ct = default);
}

/// <summary>Transport used when realtime is registered but no concrete transport was supplied.</summary>
public sealed class NullRealtimeTransport : IRealtimeTransport
{
    public Task SendAsync(RealtimeChange change, CancellationToken ct = default) => Task.CompletedTask;
}
