using System.Threading.Channels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace Holler;

/// <summary>Signaling over a SignalR <see cref="HubConnection"/> to the Holler hub.</summary>
public sealed class HubSignalingChannel : ISignalingChannel
{
    private readonly HubConnection _connection;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly Channel<HollerSignal> _inbox = Channel.CreateUnbounded<HollerSignal>(new() { SingleReader = true });
    private HollerSessionInfo? _session;
    private Task? _pump;

    public HubSignalingChannel(NavigationManager navigation, HollerClientOptions options)
    {
        var builder = new HubConnectionBuilder()
            .WithUrl(navigation.ToAbsoluteUri(options.HubPath))
            .WithAutomaticReconnect();
        options.ConfigureConnection?.Invoke(builder);
        _connection = builder.Build();

        // Queue rather than await handlers so the receive loop never blocks (handlers may invoke the hub).
        _connection.On<HollerSignal>("Signal", signal => { _inbox.Writer.TryWrite(signal); });
        _connection.Reconnected += async _ =>
        {
            _session = await _connection.InvokeAsync<HollerSessionInfo>("Hello");
            if (SessionChanged is { } handler)
                await handler(_session);
        };
    }

    public event Func<HollerSignal, Task>? SignalReceived;
    public event Func<HollerSessionInfo, Task>? SessionChanged;

    public async Task<HollerSessionInfo> StartAsync(CancellationToken cancellationToken = default)
    {
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_session is not null && _connection.State == HubConnectionState.Connected)
                return _session;
            if (_connection.State == HubConnectionState.Disconnected)
                await _connection.StartAsync(cancellationToken);
            _pump ??= PumpAsync();
            _session = await _connection.InvokeAsync<HollerSessionInfo>("Hello", cancellationToken);
            return _session;
        }
        finally
        {
            _startLock.Release();
        }
    }

    public Task SendAsync(HollerSignal signal) => _connection.InvokeAsync("Send", signal);

    public Task<IReadOnlyList<HollerPeerInfo>> JoinRoomAsync(string room) =>
        _connection.InvokeAsync<IReadOnlyList<HollerPeerInfo>>("JoinRoom", room);

    public Task LeaveRoomAsync(string room) => _connection.InvokeAsync("LeaveRoom", room);

    public Task<IReadOnlyList<HollerIceServer>> GetIceServersAsync() =>
        _connection.InvokeAsync<IReadOnlyList<HollerIceServer>>("GetIceServers");

    public Task<IReadOnlyList<string>> GetOnlineUsersAsync() =>
        _connection.InvokeAsync<IReadOnlyList<string>>("GetOnlineUsers");

    private async Task PumpAsync()
    {
        await foreach (var signal in _inbox.Reader.ReadAllAsync())
        {
            try
            {
                if (SignalReceived is { } handler)
                    await handler(signal);
            }
            catch
            {
                // Handlers log their own failures; keep pumping.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _inbox.Writer.TryComplete();
        await _connection.DisposeAsync();
    }
}
