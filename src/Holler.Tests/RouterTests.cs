using Holler.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Holler.Tests;

public class RouterTests
{
    private sealed class FakeSink : ISignalSink
    {
        public List<HollerSignal> Received { get; } = [];
        public ValueTask DeliverAsync(HollerSignal signal)
        {
            Received.Add(signal);
            return ValueTask.CompletedTask;
        }
        public IEnumerable<HollerSignal> Of(SignalType type) => Received.Where(s => s.Type == type);
    }

    private sealed class FakeAuthorizer : IHollerAuthorizer
    {
        public HashSet<(string Caller, string Callee)> DeniedCalls { get; } = [];
        public HashSet<string> DeniedRooms { get; } = [];
        public ValueTask<bool> CanCallAsync(HollerPeerInfo caller, string calleeUserId) =>
            ValueTask.FromResult(!DeniedCalls.Contains((caller.UserId, calleeUserId)));
        public ValueTask<bool> CanJoinRoomAsync(HollerPeerInfo session, string room) =>
            ValueTask.FromResult(!DeniedRooms.Contains(room));
    }

    private HollerSignalRouter _router = null!;
    private HollerOptions _options = null!;
    private FakeTimeProvider _time = null!;
    private FakeAuthorizer _authorizer = null!;
    private readonly Dictionary<string, FakeSink> _sinks = [];

    [SetUp]
    public void Setup()
    {
        _sinks.Clear();
        _options = new HollerOptions { MaxRoomSize = 3 };
        _time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        _authorizer = new FakeAuthorizer();
        _router = new HollerSignalRouter(Options.Create(_options), _authorizer, _time, NullLogger<HollerSignalRouter>.Instance);
    }

    private async Task<FakeSink> Connect(string sessionId, string userId)
    {
        var sink = new FakeSink();
        _sinks[sessionId] = sink;
        await _router.RegisterAsync(sessionId, userId, sink);
        return sink;
    }

    private Task Send(string from, SignalType type, string? callId = null, string? toUser = null, string? toSession = null, string? room = null) =>
        _router.RouteAsync(from, new HollerSignal { Type = type, CallId = callId, ToUserId = toUser, ToSessionId = toSession, Room = room, Payload = "x" });

    [Test]
    public async Task Ring_reaches_every_session_of_the_user_but_not_others()
    {
        await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");
        var b2 = await Connect("b2", "bob");
        var c1 = await Connect("c1", "carol");

        await Send("a1", SignalType.Ring, "call1", toUser: "bob");

        Assert.That(b1.Of(SignalType.Ring).Count(), Is.EqualTo(1));
        Assert.That(b2.Of(SignalType.Ring).Count(), Is.EqualTo(1));
        Assert.That(c1.Of(SignalType.Ring), Is.Empty);
    }

    [Test]
    public async Task Sender_identity_is_stamped_by_the_server()
    {
        await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");

        await _router.RouteAsync("a1", new HollerSignal { Type = SignalType.Ring, CallId = "call1", ToUserId = "bob", FromUserId = "mallory", FromSessionId = "m1" });

        var ring = b1.Of(SignalType.Ring).Single();
        Assert.That(ring.FromUserId, Is.EqualTo("alice"));
        Assert.That(ring.FromSessionId, Is.EqualTo("a1"));
    }

    [Test]
    public async Task Clients_cannot_send_server_only_signals()
    {
        await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");
        b1.Received.Clear();

        await Send("a1", SignalType.PeerLeft, toSession: "b1", room: "r");
        await Send("a1", SignalType.AnsweredElsewhere, "call1", toSession: "b1");

        Assert.That(b1.Received, Is.Empty);
    }

    [Test]
    public async Task Calling_an_offline_user_returns_unavailable()
    {
        var a1 = await Connect("a1", "alice");
        await Send("a1", SignalType.Ring, "call1", toUser: "nobody");
        Assert.That(a1.Of(SignalType.Unavailable).Single().CallId, Is.EqualTo("call1"));
    }

    [Test]
    public async Task First_accept_wins_and_other_sessions_are_told()
    {
        var a1 = await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");
        var b2 = await Connect("b2", "bob");
        await Send("a1", SignalType.Ring, "call1", toUser: "bob");

        await Send("b2", SignalType.Accept, "call1", toSession: "a1");
        await Send("b1", SignalType.Accept, "call1", toSession: "a1");

        var accept = a1.Of(SignalType.Accept).Single();
        Assert.That(accept.FromSessionId, Is.EqualTo("b2"));
        Assert.That(b1.Of(SignalType.AnsweredElsewhere).Count(), Is.EqualTo(1));
        Assert.That(b1.Of(SignalType.Hangup).Count(), Is.EqualTo(1), "late accept is told the call is gone");
        Assert.That(b2.Of(SignalType.AnsweredElsewhere), Is.Empty);
    }

    [Test]
    public async Task Negotiation_only_flows_between_connected_call_sessions()
    {
        await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");
        var b2 = await Connect("b2", "bob");
        await Send("a1", SignalType.Ring, "call1", toUser: "bob");

        await Send("a1", SignalType.Description, "call1", toSession: "b1");
        Assert.That(b1.Of(SignalType.Description), Is.Empty, "not before accept");

        await Send("b1", SignalType.Accept, "call1", toSession: "a1");
        await Send("a1", SignalType.Description, "call1", toSession: "b1");
        await Send("a1", SignalType.Ice, "call1", toSession: "b2");

        Assert.That(b1.Of(SignalType.Description).Count(), Is.EqualTo(1));
        Assert.That(b2.Of(SignalType.Ice), Is.Empty, "other session isn't in the call");
    }

    [Test]
    public async Task Strangers_cannot_send_negotiation_to_a_session()
    {
        await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");
        await Send("a1", SignalType.Description, toSession: "b1", room: "secret");
        Assert.That(b1.Of(SignalType.Description), Is.Empty);
    }

    [Test]
    public async Task Decline_reaches_caller_and_stops_other_sessions_ringing()
    {
        var a1 = await Connect("a1", "alice");
        await Connect("b1", "bob");
        var b2 = await Connect("b2", "bob");
        await Send("a1", SignalType.Ring, "call1", toUser: "bob");

        await Send("b1", SignalType.Decline, "call1", toSession: "a1");

        Assert.That(a1.Of(SignalType.Decline).Count(), Is.EqualTo(1));
        Assert.That(b2.Of(SignalType.AnsweredElsewhere).Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task Busy_only_when_every_ringing_session_is_busy()
    {
        var a1 = await Connect("a1", "alice");
        await Connect("b1", "bob");
        await Connect("b2", "bob");
        await Send("a1", SignalType.Ring, "call1", toUser: "bob");

        await Send("b1", SignalType.Busy, "call1");
        Assert.That(a1.Of(SignalType.Busy), Is.Empty);
        await Send("b2", SignalType.Busy, "call1");
        Assert.That(a1.Of(SignalType.Busy).Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task Caller_cancelling_stops_all_ringing_sessions()
    {
        await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");
        var b2 = await Connect("b2", "bob");
        await Send("a1", SignalType.Ring, "call1", toUser: "bob");

        await Send("a1", SignalType.Hangup, "call1", toUser: "bob");

        Assert.That(b1.Of(SignalType.Hangup).Count(), Is.EqualTo(1));
        Assert.That(b2.Of(SignalType.Hangup).Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task Disconnect_hangs_up_active_call()
    {
        var a1 = await Connect("a1", "alice");
        await Connect("b1", "bob");
        await Send("a1", SignalType.Ring, "call1", toUser: "bob");
        await Send("b1", SignalType.Accept, "call1", toSession: "a1");

        await _router.UnregisterAsync("b1");

        Assert.That(a1.Of(SignalType.Hangup).Single().CallId, Is.EqualTo("call1"));
    }

    [Test]
    public async Task Room_join_returns_existing_members_and_notifies_them()
    {
        var a1 = await Connect("a1", "alice");
        await Connect("b1", "bob");

        var first = await _router.JoinRoomAsync("a1", "standup");
        var second = await _router.JoinRoomAsync("b1", "standup");

        Assert.That(first, Is.Empty);
        Assert.That(second.Select(p => p.SessionId), Is.EqualTo(new[] { "a1" }));
        var joined = a1.Of(SignalType.PeerJoined).Single();
        Assert.That((joined.FromSessionId, joined.FromUserId, joined.Room), Is.EqualTo(("b1", "bob", "standup")));
    }

    [Test]
    public async Task Room_members_can_negotiate()
    {
        await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");
        await _router.JoinRoomAsync("a1", "standup");
        await _router.JoinRoomAsync("b1", "standup");

        await Send("a1", SignalType.Description, toSession: "b1", room: "standup");

        Assert.That(b1.Of(SignalType.Description).Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task Room_size_is_enforced()
    {
        for (var i = 0; i < 4; i++)
            await Connect($"s{i}", $"user{i}");
        for (var i = 0; i < 3; i++)
            await _router.JoinRoomAsync($"s{i}", "full");

        Assert.ThrowsAsync<HollerException>(() => _router.JoinRoomAsync("s3", "full"));
    }

    [Test]
    public async Task Leaving_or_disconnecting_notifies_room()
    {
        await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");
        await Connect("c1", "carol");
        await _router.JoinRoomAsync("a1", "r");
        await _router.JoinRoomAsync("b1", "r");
        await _router.JoinRoomAsync("c1", "r");

        await _router.LeaveRoomAsync("a1", "r");
        await _router.UnregisterAsync("c1");

        Assert.That(b1.Of(SignalType.PeerLeft).Select(s => s.FromSessionId), Is.EqualTo(new[] { "a1", "c1" }));
    }

    [Test]
    public async Task Presence_tracks_users_not_sessions()
    {
        var a1 = await Connect("a1", "alice");
        await Connect("b1", "bob");
        await Connect("b2", "bob");
        Assert.That(_router.GetOnlineUsers(), Is.EqualTo(new[] { "alice", "bob" }));
        Assert.That(a1.Of(SignalType.PresenceChanged).Count(), Is.EqualTo(1), "second bob session isn't a new presence");

        await _router.UnregisterAsync("b1");
        Assert.That(a1.Of(SignalType.PresenceChanged).Count(s => s.Payload == "offline"), Is.Zero);
        await _router.UnregisterAsync("b2");
        Assert.That(a1.Of(SignalType.PresenceChanged).Count(s => s.Payload == "offline"), Is.EqualTo(1));
        Assert.That(_router.GetOnlineUsers(), Is.EqualTo(new[] { "alice" }));
    }

    [Test]
    public async Task Ringing_callee_going_offline_tells_caller_unavailable()
    {
        var a1 = await Connect("a1", "alice");
        await Connect("b1", "bob");
        await Send("a1", SignalType.Ring, "call1", toUser: "bob");

        await _router.UnregisterAsync("b1");

        Assert.That(a1.Of(SignalType.Unavailable).Single().CallId, Is.EqualTo("call1"));
    }

    [Test]
    public async Task Unanswered_call_times_out_with_no_answer()
    {
        var a1 = await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");
        var b2 = await Connect("b2", "bob");
        await Send("a1", SignalType.Ring, "call1", toUser: "bob");

        _time.Advance(_options.RingTimeout - TimeSpan.FromSeconds(1));
        Assert.That(a1.Of(SignalType.NoAnswer), Is.Empty);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.That(a1.Of(SignalType.NoAnswer).Single().CallId, Is.EqualTo("call1"));
        Assert.That(b1.Of(SignalType.Hangup).Count(), Is.EqualTo(1), "stops ringing");
        Assert.That(b2.Of(SignalType.Hangup).Count(), Is.EqualTo(1), "stops ringing");

        await Send("b1", SignalType.Accept, "call1", toSession: "a1");
        Assert.That(a1.Of(SignalType.Accept), Is.Empty, "a timed-out call can no longer be accepted");
    }

    [Test]
    public async Task Answered_call_does_not_time_out()
    {
        var a1 = await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");
        await Send("a1", SignalType.Ring, "call1", toUser: "bob");
        await Send("b1", SignalType.Accept, "call1", toSession: "a1");

        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.That(a1.Of(SignalType.NoAnswer), Is.Empty);
        Assert.That(b1.Of(SignalType.Hangup), Is.Empty);
        await Send("a1", SignalType.Description, "call1", toSession: "b1");
        Assert.That(b1.Of(SignalType.Description).Count(), Is.EqualTo(1), "call still active");
    }

    [Test]
    public async Task Zero_ring_timeout_rings_forever()
    {
        _options.RingTimeout = TimeSpan.Zero;
        var a1 = await Connect("a1", "alice");
        await Connect("b1", "bob");
        await Send("a1", SignalType.Ring, "call1", toUser: "bob");

        _time.Advance(TimeSpan.FromHours(1));

        Assert.That(a1.Of(SignalType.NoAnswer), Is.Empty);
    }

    [Test]
    public async Task Denied_call_looks_like_an_offline_user()
    {
        _authorizer.DeniedCalls.Add(("alice", "bob"));
        var a1 = await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");
        b1.Received.Clear();

        await Send("a1", SignalType.Ring, "call1", toUser: "bob");

        Assert.That(a1.Of(SignalType.Unavailable).Single().CallId, Is.EqualTo("call1"));
        Assert.That(b1.Received, Is.Empty);
    }

    [Test]
    public async Task Denied_room_join_throws()
    {
        _authorizer.DeniedRooms.Add("vip");
        await Connect("a1", "alice");
        Assert.ThrowsAsync<HollerException>(() => _router.JoinRoomAsync("a1", "vip"));
        Assert.That(await _router.JoinRoomAsync("a1", "lobby"), Is.Empty);
    }

    [Test]
    public async Task Rings_are_rate_limited_per_session()
    {
        _options.Limits.RingsPerMinute = 2;
        var a1 = await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");
        b1.Received.Clear();

        for (var i = 0; i < 3; i++)
            await Send("a1", SignalType.Ring, $"call{i}", toUser: "bob");

        Assert.That(b1.Of(SignalType.Ring).Count(), Is.EqualTo(2));
        Assert.That(a1.Of(SignalType.Unavailable).Single().CallId, Is.EqualTo("call2"));

        _time.Advance(TimeSpan.FromMinutes(1));
        await Send("a1", SignalType.Ring, "call3", toUser: "bob");
        Assert.That(b1.Of(SignalType.Ring).Count(), Is.EqualTo(3), "window resets");
    }

    [Test]
    public async Task Signals_are_rate_limited_per_session()
    {
        _options.Limits.SignalsPerSecond = 5;
        await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");
        await _router.JoinRoomAsync("a1", "r");
        await _router.JoinRoomAsync("b1", "r");

        for (var i = 0; i < 8; i++)
            await Send("a1", SignalType.Ice, toSession: "b1", room: "r");
        Assert.That(b1.Of(SignalType.Ice).Count(), Is.EqualTo(5));

        _time.Advance(TimeSpan.FromSeconds(1));
        await Send("a1", SignalType.Ice, toSession: "b1", room: "r");
        Assert.That(b1.Of(SignalType.Ice).Count(), Is.EqualTo(6), "window resets");
    }

    [Test]
    public async Task Oversized_payloads_are_dropped()
    {
        _options.Limits.MaxPayloadLength = 10;
        await Connect("a1", "alice");
        var b1 = await Connect("b1", "bob");
        await _router.JoinRoomAsync("a1", "r");
        await _router.JoinRoomAsync("b1", "r");

        await _router.RouteAsync("a1", new HollerSignal { Type = SignalType.Description, ToSessionId = "b1", Room = "r", Payload = new string('x', 11) });
        await _router.RouteAsync("a1", new HollerSignal { Type = SignalType.Description, ToSessionId = "b1", Room = "r", Payload = new string('x', 10) });

        Assert.That(b1.Of(SignalType.Description).Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task Rooms_per_session_are_limited()
    {
        _options.Limits.MaxRoomsPerSession = 2;
        await Connect("a1", "alice");
        await _router.JoinRoomAsync("a1", "r1");
        await _router.JoinRoomAsync("a1", "r2");
        Assert.ThrowsAsync<HollerException>(() => _router.JoinRoomAsync("a1", "r3"));
        Assert.That(await _router.JoinRoomAsync("a1", "r1"), Is.Empty, "rejoining a room you are in is fine");
    }
}
