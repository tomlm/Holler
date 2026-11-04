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
    public bool DataChannelOpen { get; internal set; }

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
public sealed record HollerMediaResult(bool Ok, bool Audio, bool Video, string? Error);
