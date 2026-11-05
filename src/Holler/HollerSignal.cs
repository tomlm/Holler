namespace Holler;

/// <summary>The kinds of signaling messages exchanged through the Holler hub.</summary>
public enum SignalType
{
    /// <summary>Caller asks a user to start a call. Delivered to every session of <see cref="HollerSignal.ToUserId"/>.</summary>
    Ring,
    /// <summary>Callee accepts a ringing call.</summary>
    Accept,
    /// <summary>Callee declines a ringing call.</summary>
    Decline,
    /// <summary>Callee session is already in a call.</summary>
    Busy,
    /// <summary>Either side ends a call (or the caller cancels a ringing call).</summary>
    Hangup,
    /// <summary>WebRTC session description (offer or answer), JSON in <see cref="HollerSignal.Payload"/>.</summary>
    Description,
    /// <summary>WebRTC ICE candidate, JSON in <see cref="HollerSignal.Payload"/>.</summary>
    Ice,
    /// <summary>Server: a session joined a room you are in.</summary>
    PeerJoined,
    /// <summary>Server: a session left a room you are in.</summary>
    PeerLeft,
    /// <summary>Server: another session of yours answered (or declined) the call.</summary>
    AnsweredElsewhere,
    /// <summary>Server: the called user has no reachable sessions.</summary>
    Unavailable,
    /// <summary>Server: a user came online or went offline. <see cref="HollerSignal.Payload"/> is "online" or "offline".</summary>
    PresenceChanged,
    /// <summary>Server: the call rang until the ring timeout without being answered.</summary>
    NoAnswer,
}

/// <summary>
/// A signaling message. The server always overwrites <see cref="FromUserId"/> and
/// <see cref="FromSessionId"/> with the authenticated sender, so they can be trusted on receipt.
/// </summary>
public sealed record HollerSignal
{
    public SignalType Type { get; init; }
    public string? CallId { get; init; }
    public string? FromUserId { get; init; }
    public string? FromSessionId { get; init; }
    public string? ToUserId { get; init; }
    public string? ToSessionId { get; init; }
    public string? Room { get; init; }
    public string? Payload { get; init; }

    /// <summary>Signal types that only the server may originate.</summary>
    public static bool IsServerOnly(SignalType type) => type is
        SignalType.PeerJoined or SignalType.PeerLeft or SignalType.AnsweredElsewhere or
        SignalType.Unavailable or SignalType.PresenceChanged or SignalType.NoAnswer;
}

/// <summary>Identity of this client's signaling session.</summary>
public sealed record HollerSessionInfo(string SessionId, string UserId);

/// <summary>A session (one tab or device) belonging to a user.</summary>
public sealed record HollerPeerInfo(string SessionId, string UserId);

/// <summary>A STUN or TURN server, in the shape of <c>RTCIceServer</c>.</summary>
public sealed record HollerIceServer(string[] Urls, string? Username = null, string? Credential = null);

/// <summary>How browsers should find a route to each other.</summary>
/// <param name="IceServers">STUN/TURN servers for this user.</param>
/// <param name="RelayOnly">
/// Only use TURN relays (<c>iceTransportPolicy: "relay"</c>). For proving a TURN setup works, or for hiding
/// players' IP addresses from each other; connections fail if no TURN server is reachable.
/// </param>
public sealed record HollerIceConfiguration(IReadOnlyList<HollerIceServer> IceServers, bool RelayOnly = false);
