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
    private bool _suspended;

    public event Func<HollerSignal, Task>? SignalReceived;

    /// <summary>Raised when the circuit reconnects after its connection dropped (see <see cref="HollerCircuitHandler"/>).</summary>
    public event Func<HollerSessionInfo, Task>? SessionChanged;

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

    /// <summary>
    /// The browser's connection dropped. The circuit can live on for minutes waiting for a reconnect, so leave
    /// the router now; otherwise peers keep seeing a frozen tile for a tab that is already closed.
    /// </summary>
    internal async Task SuspendAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (_session is null || _suspended)
                return;
            _suspended = true;
            await router.UnregisterAsync(_session.SessionId);
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>The browser reconnected: register again under a new session and let the client rebuild its connections.</summary>
    internal async Task ResumeAsync()
    {
        HollerSessionInfo session;
        await _startLock.WaitAsync();
        try
        {
            if (_session is null || !_suspended)
                return;
            _suspended = false;
            session = _session with { SessionId = "s" + Guid.NewGuid().ToString("N") };
            await router.RegisterAsync(session.SessionId, session.UserId, this);
            _session = session;
        }
        finally
        {
            _startLock.Release();
        }
        // Not awaited: the client rejoins rooms through the circuit, which must not wait on this handler.
        if (SessionChanged is { } handler)
            _ = handler(session).ContinueWith(t => logger.LogWarning(t.Exception, "Holler failed to restore the session after reconnect"),
                TaskContinuationOptions.OnlyOnFaulted);
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
