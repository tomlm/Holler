using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Holler.Server;

/// <summary>SignalR hub used by WebAssembly clients for signaling. Map it with <c>app.MapHollerHub()</c>.</summary>
public sealed class HollerHub(
    HollerSignalRouter router,
    IHollerUserIdProvider userIds,
    IIceServerProvider iceServers,
    IHubContext<HollerHub> hubContext,
    IOptions<HollerOptions> options) : Hub
{
    private const string SessionKey = "holler.session";

    private string SessionId => Context.Items[SessionKey] as string ?? throw new HubException("Not registered with Holler.");

    public override async Task OnConnectedAsync()
    {
        var sessionId = "h" + Guid.NewGuid().ToString("N");
        var userId = Context.User is { } user ? userIds.GetUserId(user) : null;
        if (userId is null)
        {
            if (options.Value.RequireAuthorization)
            {
                Context.Abort();
                return;
            }
            userId = "guest-" + sessionId[1..9];
        }
        Context.Items[SessionKey] = sessionId;
        await router.RegisterAsync(sessionId, userId, new HubClientSink(hubContext, Context.ConnectionId));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items[SessionKey] is string sessionId)
            await router.UnregisterAsync(sessionId);
        await base.OnDisconnectedAsync(exception);
    }

    public HollerSessionInfo Hello() => router.GetSession(SessionId) ?? throw new HubException("Not registered with Holler.");

    public Task Send(HollerSignal signal) => router.RouteAsync(SessionId, signal);

    public async Task<IReadOnlyList<HollerPeerInfo>> JoinRoom(string room)
    {
        try
        {
            return await router.JoinRoomAsync(SessionId, room);
        }
        catch (HollerException ex)
        {
            throw new HubException(ex.Message);
        }
    }

    public Task LeaveRoom(string room) => router.LeaveRoomAsync(SessionId, room);

    public async Task<HollerIceConfiguration> GetIceConfiguration() =>
        new(await iceServers.GetIceServersAsync(Hello().UserId), options.Value.ForceRelay);

    public IReadOnlyList<string> GetOnlineUsers() => router.GetOnlineUsers();

    private sealed class HubClientSink(IHubContext<HollerHub> hub, string connectionId) : ISignalSink
    {
        public ValueTask DeliverAsync(HollerSignal signal) =>
            new(hub.Clients.Client(connectionId).SendAsync("Signal", signal));
    }
}
