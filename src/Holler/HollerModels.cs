namespace Holler;

public enum HollerCallDirection { Outgoing, Incoming }

public enum HollerCallState { Ringing, Connecting, Connected, Ended }

/// <summary>A 1:1 call with another user.</summary>
public sealed class HollerCall
{
    internal HollerCall(string callId, string remoteUserId, HollerCallDirection direction)
    {
        CallId = callId;
        RemoteUserId = remoteUserId;
        Direction = direction;
    }

    public string CallId { get; }
    public string RemoteUserId { get; }
    public HollerCallDirection Direction { get; }

    /// <summary>The remote session (tab or device). Known once the call is accepted, or immediately for incoming calls.</summary>
    public string? RemoteSessionId { get; internal set; }
    public HollerCallState State { get; internal set; } = HollerCallState.Ringing;
    public string? EndReason { get; internal set; }
    public HollerPeer? Peer { get; internal set; }

    public bool IsActive => State is not HollerCallState.Ended;
}

/// <summary>A remote session that this client has (or is setting up) a peer connection with.</summary>
public sealed class HollerPeer
{
    internal HollerPeer(string sessionId, string userId, string? callId, string? room)
    {
        SessionId = sessionId;
        UserId = userId;
        CallId = callId;
        Room = room;
        Key = callId is not null ? $"call:{callId}:{sessionId}" : $"room:{room}:{sessionId}";
    }

    /// <summary>Unique key of this peer connection; also the stream key used by <c>HollerVideo</c>.</summary>
    public string Key { get; }
    public string SessionId { get; }
    public string UserId { get; }
    public string? CallId { get; }
    public string? Room { get; }

    /// <summary>The <c>RTCPeerConnection.connectionState</c>: new, connecting, connected, disconnected, failed or closed.</summary>
    public string ConnectionState { get; internal set; } = "new";
    public bool HasAudio { get; internal set; }
    public bool HasVideo { get; internal set; }

    /// <summary>The peer's microphone is on (they haven't muted, and they have one).</summary>
    public bool AudioEnabled { get; internal set; } = true;

    /// <summary>The peer's camera is on. False while they have hidden their video.</summary>
    public bool VideoEnabled { get; internal set; } = true;

    /// <summary>There is live video to show: a video track has arrived and the peer hasn't turned the camera off.</summary>
    public bool IsVideoVisible => HasVideo && VideoEnabled;
    public bool DataChannelOpen { get; internal set; }

    /// <summary>
    /// How the connection reaches this peer once connected: "direct" (local network or through the routers) or
    /// "relay" (through a TURN server), with the transport, e.g. "relay (udp)". Null until connected.
    /// </summary>
    public string? Route { get; internal set; }

    /// <summary>The connection goes through a TURN relay.</summary>
    public bool IsRelayed => Route?.StartsWith("relay", StringComparison.Ordinal) == true;

    internal bool Created { get; set; }
}

/// <summary>A room this client has joined. Every member is connected to every other (full mesh).</summary>
public sealed class HollerRoom
{
    internal HollerRoom(string name) => Name = name;

    public string Name { get; }
    internal List<HollerPeer> PeerList { get; } = [];
    public IReadOnlyList<HollerPeer> Peers => PeerList.ToArray();
}

/// <summary>A chat message sent over a peer data channel.</summary>
public sealed record HollerChatMessage(string Conversation, string FromUserId, string Text, DateTimeOffset At, bool IsLocal);

/// <summary>Result of requesting the local camera and microphone.</summary>
/// <param name="Ok">Some media is running (possibly only audio).</param>
/// <param name="Audio">A microphone is running.</param>
/// <param name="Video">A camera is running.</param>
/// <param name="Error">Why something requested isn't running, if anything.</param>
/// <param name="PermissionDenied">
/// The user (or browser policy) blocked the camera or microphone. Browsers won't ask again on their own: the
/// user has to allow it from the address bar. Holler retries automatically when the browser reports the change,
/// and <c>HollerClient.RetryMediaAsync</c> retries on demand.
/// </param>
public sealed record HollerMediaResult(bool Ok, bool Audio, bool Video, string? Error, bool PermissionDenied = false);

/// <summary>A camera (<see cref="Kind"/> "video") or microphone ("audio").</summary>
public sealed record HollerMediaDevice(string DeviceId, string Kind, string Label);

/// <summary>
/// Upper bounds for outgoing video. The camera captures at most this, and each viewer gets a copy scaled down
/// to the size it renders the video at, with the bitrate scaled to match.
/// </summary>
public sealed record HollerVideoSettings(int MaxWidth = 1280, int MaxHeight = 720, int MaxFrameRate = 30, int MaxBitrateKbps = 1500);
