namespace Holler;

/// <summary>
/// Transport for signaling messages. WebAssembly clients use <see cref="HubSignalingChannel"/>;
/// Blazor Server circuits use an in-process channel from Holler.Server.
/// </summary>
public interface ISignalingChannel : IAsyncDisposable
{
    /// <summary>Connects (idempotent) and returns this client's session identity.</summary>
    Task<HollerSessionInfo> StartAsync(CancellationToken cancellationToken = default);

    Task SendAsync(HollerSignal signal);

    /// <summary>Joins a room and returns the sessions already in it.</summary>
    Task<IReadOnlyList<HollerPeerInfo>> JoinRoomAsync(string room);

    Task LeaveRoomAsync(string room);

    Task<IReadOnlyList<HollerIceServer>> GetIceServersAsync();

    Task<IReadOnlyList<string>> GetOnlineUsersAsync();

    /// <summary>Raised for every signal delivered to this session.</summary>
    event Func<HollerSignal, Task>? SignalReceived;

    /// <summary>Raised when the connection was re-established under a new session.</summary>
    event Func<HollerSessionInfo, Task>? SessionChanged;
}
