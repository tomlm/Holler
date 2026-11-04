using System.Threading.Channels;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Holler.Server;

/// <summary>
/// Signaling for Blazor Server circuits. The component code already runs on the server, so it talks to
/// the router directly instead of opening a SignalR connection back to itself.
/// </summary>
public sealed class InProcessSignalingChannel(
    HollerSignalRouter router,
    AuthenticationStateProvider authentication,
    IHollerUserIdProvider userIds,
    IIceServerProvider iceServers,
    IOptions<HollerOptions> options,
    ILogger<InProcessSignalingChannel> logger) : ISignalingChannel, ISignalSink
{
    // Deliveries are queued so a sender never waits on (or deadlocks with) the receiving circuit.
    private readonly Channel<HollerSignal> _inbox = Channel.CreateUnbounded<HollerSignal>(new() { SingleReader = true });
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private HollerSessionInfo? _session;

    public event Func<HollerSignal, Task>? SignalReceived;

    // A circuit's session never changes; a new circuit gets a new channel.
    public event Func<HollerSessionInfo, Task>? SessionChanged { add { } remove { } }

    private HollerSessionInfo Session => _session ?? throw new InvalidOperationException("Holler channel not started.");

    public async Task<HollerSessionInfo> StartAsync(CancellationToken cancellationToken = default)
    {
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_session is not null)
                return _session;
            var state = await authentication.GetAuthenticationStateAsync();
            var sessionId = "s" + Guid.NewGuid().ToString("N");
            var userId = userIds.GetUserId(state.User);
            if (userId is null)
            {
                if (options.Value.RequireAuthorization)
                    throw new InvalidOperationException("Holler requires a signed-in user.");
                userId = "guest-" + sessionId[1..9];
            }
            await router.RegisterAsync(sessionId, userId, this);
            _session = new HollerSessionInfo(sessionId, userId);
            _ = PumpAsync();
            return _session;
        }
        finally
        {
            _startLock.Release();
        }
    }

    public Task SendAsync(HollerSignal signal) => router.RouteAsync(Session.SessionId, signal);

    public async Task<IReadOnlyList<HollerPeerInfo>> JoinRoomAsync(string room) =>
        await router.JoinRoomAsync(Session.SessionId, room);

    public Task LeaveRoomAsync(string room) => router.LeaveRoomAsync(Session.SessionId, room);

    public async Task<IReadOnlyList<HollerIceServer>> GetIceServersAsync() =>
        await iceServers.GetIceServersAsync(Session.UserId);

    public Task<IReadOnlyList<string>> GetOnlineUsersAsync() => Task.FromResult(router.GetOnlineUsers());

    ValueTask ISignalSink.DeliverAsync(HollerSignal signal)
    {
        _inbox.Writer.TryWrite(signal);
        return ValueTask.CompletedTask;
    }

    private async Task PumpAsync()
    {
        await foreach (var signal in _inbox.Reader.ReadAllAsync())
        {
            try
            {
                if (SignalReceived is { } handler)
                    await handler(signal);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Holler signal handler failed for {Type}", signal.Type);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _inbox.Writer.TryComplete();
        if (_session is not null)
            await router.UnregisterAsync(_session.SessionId);
    }
}
