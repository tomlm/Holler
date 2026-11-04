using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Holler.Server;

/// <summary>Receives signals for one session (a hub connection or a Blazor Server circuit).</summary>
public interface ISignalSink
{
    /// <summary>Must not block on the receiving client's processing.</summary>
    ValueTask DeliverAsync(HollerSignal signal);
}

public sealed class HollerException(string message) : Exception(message);

/// <summary>
/// The single routing point for Holler signals: tracks sessions, presence, rooms and calls, stamps every
/// signal with its authenticated sender, and only forwards WebRTC negotiation between sessions that share
/// a call or a room. State is in memory, so all sessions must be connected to the same server instance.
/// </summary>
public sealed class HollerSignalRouter(IOptions<HollerOptions> options, ILogger<HollerSignalRouter> logger)
{
    private sealed record Session(string SessionId, string UserId, ISignalSink Sink)
    {
        public HashSet<string> Rooms { get; } = [];
    }

    private sealed class Call(string callId, Session caller, string calleeUserId)
    {
        public string CallId { get; } = callId;
        public Session Caller { get; } = caller;
        public string CalleeUserId { get; } = calleeUserId;
        public string? CalleeSessionId { get; set; }
        public HashSet<string> Busy { get; } = [];
        public bool IsRinging => CalleeSessionId is null;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly Dictionary<string, HashSet<string>> _userSessions = [];
    private readonly Dictionary<string, HashSet<string>> _rooms = [];
    private readonly Dictionary<string, Call> _calls = [];

    private readonly record struct Delivery(Session To, HollerSignal Signal);

    public HollerSessionInfo? GetSession(string sessionId)
    {
        lock (_lock)
            return _sessions.TryGetValue(sessionId, out var s) ? new HollerSessionInfo(s.SessionId, s.UserId) : null;
    }

    public IReadOnlyList<string> GetOnlineUsers()
    {
        lock (_lock)
            return _userSessions.Keys.Order().ToArray();
    }

    public Task RegisterAsync(string sessionId, string userId, ISignalSink sink)
    {
        var deliveries = new List<Delivery>();
        lock (_lock)
        {
            var session = new Session(sessionId, userId, sink);
            _sessions[sessionId] = session;
            if (!_userSessions.TryGetValue(userId, out var set))
            {
                _userSessions[userId] = set = [];
                var online = new HollerSignal { Type = SignalType.PresenceChanged, FromUserId = userId, Payload = "online" };
                deliveries.AddRange(_sessions.Values.Where(s => s.UserId != userId).Select(s => new Delivery(s, online)));
            }
            set.Add(sessionId);
        }
        return DeliverAsync(deliveries);
    }

    public Task UnregisterAsync(string sessionId)
    {
        var deliveries = new List<Delivery>();
        lock (_lock)
        {
            if (!_sessions.Remove(sessionId, out var session))
                return Task.CompletedTask;

            foreach (var room in session.Rooms)
                LeaveRoomLocked(session, room, deliveries);

            var userSessions = _userSessions[session.UserId];
            userSessions.Remove(sessionId);
            foreach (var call in _calls.Values.ToArray())
            {
                var hangup = new HollerSignal { Type = SignalType.Hangup, CallId = call.CallId, FromUserId = session.UserId, FromSessionId = sessionId };
                if (call.Caller == session)
                {
                    _calls.Remove(call.CallId);
                    deliveries.AddRange(CalleeSessions(call).Select(s => new Delivery(s, hangup)));
                }
                else if (call.CalleeSessionId == sessionId)
                {
                    _calls.Remove(call.CallId);
                    deliveries.Add(new Delivery(call.Caller, hangup));
                }
                else if (call.IsRinging && call.CalleeUserId == session.UserId && !CalleeSessions(call).Any())
                {
                    _calls.Remove(call.CallId);
                    deliveries.Add(new Delivery(call.Caller, new HollerSignal { Type = SignalType.Unavailable, CallId = call.CallId, FromUserId = call.CalleeUserId }));
                }
            }

            if (userSessions.Count == 0)
            {
                _userSessions.Remove(session.UserId);
                var offline = new HollerSignal { Type = SignalType.PresenceChanged, FromUserId = session.UserId, Payload = "offline" };
                deliveries.AddRange(_sessions.Values.Select(s => new Delivery(s, offline)));
            }
        }
        return DeliverAsync(deliveries);
    }

    /// <summary>Joins a room and returns the sessions already in it. Existing members get <see cref="SignalType.PeerJoined"/>.</summary>
    public async Task<IReadOnlyList<HollerPeerInfo>> JoinRoomAsync(string sessionId, string room)
    {
        if (string.IsNullOrWhiteSpace(room) || room.Length > 128)
            throw new HollerException("Room names must be 1-128 characters.");
        var deliveries = new List<Delivery>();
        HollerPeerInfo[] existing;
        lock (_lock)
        {
            var session = GetSessionLocked(sessionId);
            if (!_rooms.TryGetValue(room, out var members))
                _rooms[room] = members = [];
            if (members.Contains(sessionId))
                return members.Where(m => m != sessionId).Select(m => ToPeer(_sessions[m])).ToArray();
            if (members.Count >= options.Value.MaxRoomSize)
                throw new HollerException($"Room '{room}' is full ({options.Value.MaxRoomSize} max).");

            existing = members.Select(m => ToPeer(_sessions[m])).ToArray();
            var joined = new HollerSignal { Type = SignalType.PeerJoined, Room = room, FromUserId = session.UserId, FromSessionId = sessionId };
            deliveries.AddRange(members.Select(m => new Delivery(_sessions[m], joined)));
            members.Add(sessionId);
            session.Rooms.Add(room);
        }
        // Existing members learn about the joiner before the joiner gets the list and starts offering.
        await DeliverAsync(deliveries);
        return existing;
    }

    public Task LeaveRoomAsync(string sessionId, string room)
    {
        var deliveries = new List<Delivery>();
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var session) && session.Rooms.Contains(room))
            {
                LeaveRoomLocked(session, room, deliveries);
                session.Rooms.Remove(room);
            }
        }
        return DeliverAsync(deliveries);
    }

    /// <summary>Routes a signal sent by <paramref name="fromSessionId"/>.</summary>
    public Task RouteAsync(string fromSessionId, HollerSignal signal)
    {
        var deliveries = new List<Delivery>();
        lock (_lock)
        {
            var from = GetSessionLocked(fromSessionId);
            if (HollerSignal.IsServerOnly(signal.Type))
            {
                logger.LogWarning("Session {Session} tried to send server-only signal {Type}", fromSessionId, signal.Type);
                return Task.CompletedTask;
            }
            // Never trust the client's claim about who it is.
            var stamped = signal with { FromUserId = from.UserId, FromSessionId = from.SessionId };
            switch (signal.Type)
            {
                case SignalType.Ring:
                    Ring(from, stamped, deliveries);
                    break;
                case SignalType.Accept:
                    Accept(from, stamped, deliveries);
                    break;
                case SignalType.Decline:
                case SignalType.Busy:
                    Refuse(from, stamped, deliveries);
                    break;
                case SignalType.Hangup:
                    Hangup(from, stamped, deliveries);
                    break;
                case SignalType.Description:
                case SignalType.Ice:
                    if (signal.ToSessionId is { } to && _sessions.TryGetValue(to, out var target) && AreConnected(from, target, signal))
                        deliveries.Add(new Delivery(target, stamped));
                    break;
            }
        }
        return DeliverAsync(deliveries);
    }

    private void Ring(Session from, HollerSignal signal, List<Delivery> deliveries)
    {
        if (signal.CallId is not { Length: > 0 and <= 64 } callId || signal.ToUserId is not { } toUser || _calls.ContainsKey(callId))
            return;
        var call = new Call(callId, from, toUser);
        var targets = CalleeSessions(call).ToArray();
        if (targets.Length == 0)
        {
            deliveries.Add(new Delivery(from, new HollerSignal { Type = SignalType.Unavailable, CallId = callId, FromUserId = toUser }));
            return;
        }
        _calls[callId] = call;
        var ring = signal with { ToSessionId = null };
        deliveries.AddRange(targets.Select(s => new Delivery(s, ring)));
    }

    private void Accept(Session from, HollerSignal signal, List<Delivery> deliveries)
    {
        if (signal.CallId is null || !_calls.TryGetValue(signal.CallId, out var call) ||
            !call.IsRinging || call.CalleeUserId != from.UserId || call.Caller == from)
        {
            // Too late (someone else answered, or the caller gave up): tell the sender it's over.
            deliveries.Add(new Delivery(from, new HollerSignal { Type = SignalType.Hangup, CallId = signal.CallId }));
            return;
        }
        // First accept wins.
        var others = CalleeSessions(call).Where(s => s != from).ToArray();
        call.CalleeSessionId = from.SessionId;
        deliveries.Add(new Delivery(call.Caller, signal with { ToSessionId = call.Caller.SessionId }));
        var elsewhere = new HollerSignal { Type = SignalType.AnsweredElsewhere, CallId = call.CallId };
        deliveries.AddRange(others.Select(s => new Delivery(s, elsewhere)));
    }

    private void Refuse(Session from, HollerSignal signal, List<Delivery> deliveries)
    {
        if (signal.CallId is null || !_calls.TryGetValue(signal.CallId, out var call) ||
            !call.IsRinging || call.CalleeUserId != from.UserId || call.Caller == from)
            return;
        var ringing = CalleeSessions(call).ToArray();
        if (signal.Type == SignalType.Busy)
        {
            // Only busy once every ringing session is busy.
            call.Busy.Add(from.SessionId);
            if (ringing.Any(s => !call.Busy.Contains(s.SessionId)))
                return;
        }
        _calls.Remove(call.CallId);
        deliveries.Add(new Delivery(call.Caller, signal with { ToSessionId = call.Caller.SessionId }));
        var elsewhere = new HollerSignal { Type = SignalType.AnsweredElsewhere, CallId = call.CallId };
        deliveries.AddRange(ringing.Where(s => s != from).Select(s => new Delivery(s, elsewhere)));
    }

    private void Hangup(Session from, HollerSignal signal, List<Delivery> deliveries)
    {
        if (signal.CallId is null || !_calls.TryGetValue(signal.CallId, out var call))
            return;
        if (call.Caller == from)
        {
            _calls.Remove(call.CallId);
            deliveries.AddRange(CalleeSessions(call).Select(s => new Delivery(s, signal)));
        }
        else if (call.CalleeSessionId == from.SessionId)
        {
            _calls.Remove(call.CallId);
            deliveries.Add(new Delivery(call.Caller, signal));
        }
        else if (call.IsRinging && call.CalleeUserId == from.UserId)
        {
            Refuse(from, signal with { Type = SignalType.Decline }, deliveries);
        }
    }

    /// <summary>The sessions a call reaches: the answering session once accepted, otherwise all of the callee's sessions except the caller's.</summary>
    private IEnumerable<Session> CalleeSessions(Call call)
    {
        if (call.CalleeSessionId is { } answered)
            return _sessions.TryGetValue(answered, out var s) ? [s] : [];
        return _userSessions.TryGetValue(call.CalleeUserId, out var set)
            ? set.Where(id => id != call.Caller.SessionId).Select(id => _sessions[id]).ToArray()
            : [];
    }

    private bool AreConnected(Session a, Session b, HollerSignal signal)
    {
        if (a == b)
            return false;
        if (signal.CallId is { } callId)
            return _calls.TryGetValue(callId, out var call) && !call.IsRinging &&
                   ((call.Caller == a && call.CalleeSessionId == b.SessionId) ||
                    (call.Caller == b && call.CalleeSessionId == a.SessionId));
        return signal.Room is { } room && a.Rooms.Contains(room) && b.Rooms.Contains(room);
    }

    private void LeaveRoomLocked(Session session, string room, List<Delivery> deliveries)
    {
        if (!_rooms.TryGetValue(room, out var members) || !members.Remove(session.SessionId))
            return;
        if (members.Count == 0)
        {
            _rooms.Remove(room);
            return;
        }
        var left = new HollerSignal { Type = SignalType.PeerLeft, Room = room, FromUserId = session.UserId, FromSessionId = session.SessionId };
        deliveries.AddRange(members.Select(m => new Delivery(_sessions[m], left)));
    }

    private Session GetSessionLocked(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session : throw new HollerException("Unknown Holler session.");

    private static HollerPeerInfo ToPeer(Session s) => new(s.SessionId, s.UserId);

    private async Task DeliverAsync(List<Delivery> deliveries)
    {
        foreach (var (to, signal) in deliveries)
        {
            try
            {
                await to.Sink.DeliverAsync(signal);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deliver {Type} to session {Session}", signal.Type, to.SessionId);
            }
        }
    }
}
