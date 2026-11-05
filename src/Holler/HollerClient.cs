using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Holler;

/// <summary>
/// The Holler client API: place and answer 1:1 calls, join rooms, toggle media and chat.
/// Register with <c>AddHollerClient()</c> (WebAssembly) or <c>AddHoller()</c> (server); it is scoped,
/// so there is one per browser tab / circuit. The Holler components use it for you.
/// </summary>
public sealed class HollerClient : IAsyncDisposable
{
    private const string ModulePath = "./_content/Holler/holler.js";

    private readonly ISignalingChannel _channel;
    private readonly IJSRuntime _js;
    private readonly ILogger<HollerClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    private readonly Dictionary<string, HollerCall> _calls = [];
    private readonly Dictionary<string, HollerPeer> _peers = [];
    private readonly Dictionary<string, HollerRoom> _rooms = [];
    private readonly HashSet<string> _online = [];
    private readonly Dictionary<string, List<HollerChatMessage>> _messages = [];

    // TURN credentials can expire, so re-fetch ICE servers before new connections once they get old.
    private static readonly TimeSpan IceServerRefresh = TimeSpan.FromMinutes(10);
    private DateTimeOffset _iceServersFetched;

    private Task? _started;
    private SynchronizationContext? _context;
    private HollerSessionInfo? _session;
    private IJSObjectReference? _module;
    private IJSObjectReference? _engine;
    private DotNetObjectReference<JsCallbacks>? _callbacks;
    private bool _disposed;

    public HollerClient(ISignalingChannel channel, IJSRuntime js, ILogger<HollerClient> logger)
    {
        _channel = channel;
        _js = js;
        _logger = logger;
    }

    /// <summary>Raised whenever calls, rooms, peers, presence or messages change.</summary>
    public event Action? Changed;

    /// <summary>Raised when another user rings this session.</summary>
    public event Action<HollerCall>? IncomingCall;

    public event Action<HollerChatMessage>? MessageReceived;

    public bool IsStarted => _engine is not null;
    public string? UserId => _session?.UserId;
    public string? SessionId => _session?.SessionId;

    public HollerMediaResult? LocalMedia { get; private set; }
    public bool MicrophoneEnabled { get; private set; } = true;
    public bool CameraEnabled { get; private set; } = true;

    /// <summary>A microphone is running and not muted: what a mute button should show.</summary>
    public bool IsMicrophoneOn => MicrophoneEnabled && LocalMedia is { Audio: true };

    /// <summary>A camera is running and not hidden: what a show/hide video button should show.</summary>
    public bool IsCameraOn => CameraEnabled && LocalMedia is { Video: true };

    // What the app last asked StartMediaAsync for, so a retry asks for the same.
    private (bool Audio, bool Video)? _requestedMedia;

    public IReadOnlyList<HollerCall> Calls { get { lock (_sync) return _calls.Values.ToArray(); } }
    public IReadOnlyList<HollerRoom> Rooms { get { lock (_sync) return _rooms.Values.ToArray(); } }
    public IReadOnlyList<string> OnlineUsers { get { lock (_sync) return _online.Order().ToArray(); } }

    /// <summary>The first call that is ringing this session, if any.</summary>
    public HollerCall? RingingCall
    {
        get { lock (_sync) return _calls.Values.FirstOrDefault(c => c.Direction == HollerCallDirection.Incoming && c.State == HollerCallState.Ringing); }
    }

    /// <summary>The active (not ended) call with a user, if any.</summary>
    public HollerCall? GetCall(string userId)
    {
        lock (_sync) return _calls.Values.FirstOrDefault(c => c.RemoteUserId == userId && c.IsActive);
    }

    public HollerRoom? GetRoom(string name)
    {
        lock (_sync) return _rooms.GetValueOrDefault(name);
    }

    public static string CallConversation(string userId) => $"user:{userId}";
    public static string RoomConversation(string room) => $"room:{room}";

    public IReadOnlyList<HollerChatMessage> GetMessages(string conversation)
    {
        lock (_sync) return _messages.TryGetValue(conversation, out var list) ? list.ToArray() : [];
    }

    /// <summary>Connects to the signaling hub and loads the WebRTC engine. Safe to call repeatedly.</summary>
    public Task StartAsync()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _started ??= StartCoreAsync();
        }
    }

    private async Task StartCoreAsync()
    {
        try
        {
            // Signals arrive on background threads; marshal them back to the caller's context
            // (the Blazor Server circuit's renderer, or the WebAssembly main thread).
            _context = SynchronizationContext.Current;
            _session = await _channel.StartAsync();
            var iceServers = await _channel.GetIceServersAsync();
            _module = await _js.InvokeAsync<IJSObjectReference>("import", ModulePath);
            _callbacks = DotNetObjectReference.Create(new JsCallbacks(this));
            _engine = await _module.InvokeAsync<IJSObjectReference>("createEngine", _callbacks, iceServers);
            _iceServersFetched = DateTimeOffset.UtcNow;

            var online = await _channel.GetOnlineUsersAsync();
            lock (_sync)
            {
                _online.Clear();
                _online.UnionWith(online);
            }
            _channel.SignalReceived += OnSignalReceived;
            _channel.SessionChanged += OnSessionChanged;
            NotifyChanged();
        }
        catch
        {
            lock (_sync) _started = null;
            throw;
        }
    }

    /// <summary>Requests the camera and/or microphone. Falls back to audio only if the camera is unavailable.</summary>
    /// <param name="audio">Request a microphone.</param>
    /// <param name="video">Request a camera.</param>
    /// <param name="videoSettings">
    /// Upper bounds for the video you send. Each viewer is only sent the resolution it actually draws your video
    /// at (and nothing while it isn't shown), so these are ceilings, not what every viewer receives.
    /// </param>
    public async Task<HollerMediaResult> StartMediaAsync(bool audio = true, bool video = true, HollerVideoSettings? videoSettings = null)
    {
        await StartAsync();
        _requestedMedia = (audio, video);
        if (LocalMedia is { Ok: true })
            return LocalMedia;
        var result = await Exclusive(async () =>
            await _engine!.InvokeAsync<HollerMediaResult>("startMedia", audio, video, videoSettings ?? new HollerVideoSettings()));
        LocalMedia = result;
        if (result.Ok)
        {
            await RefreshDeviceIdsAsync();
            await SetMicrophoneAsync(MicrophoneEnabled);
            await SetCameraAsync(CameraEnabled);
        }
        NotifyChanged();
        return result;
    }

    /// <summary>
    /// Asks again for whatever <see cref="StartMediaAsync"/> requested but didn't get, e.g. after the user allowed
    /// a blocked camera in the address bar. Anything already running carries on undisturbed.
    /// </summary>
    public async Task<HollerMediaResult> RetryMediaAsync()
    {
        if (_requestedMedia is not { } requested || _engine is null)
            return LocalMedia ?? new HollerMediaResult(false, false, false, "Media was never requested");
        var result = await Exclusive(async () =>
            await _engine.InvokeAsync<HollerMediaResult>("retryMedia", requested.Audio, requested.Video));
        LocalMedia = result;
        if (result.Ok)
        {
            await RefreshDeviceIdsAsync();
            await SetMicrophoneAsync(MicrophoneEnabled);
            await SetCameraAsync(CameraEnabled);
        }
        NotifyChanged();
        return result;
    }

    private bool MissingRequestedMedia =>
        _requestedMedia is { } requested &&
        ((requested.Audio && LocalMedia is not { Audio: true }) || (requested.Video && LocalMedia is not { Video: true }));

    private Task OnMediaPermissionChangedAsync(string name, string state) => Post(async () =>
    {
        // "prompt" means the user reset a block, so asking now shows the browser's prompt again.
        if (state is "granted" or "prompt" && MissingRequestedMedia)
        {
            try
            {
                await RetryMediaAsync();
            }
            catch (Exception ex) when (ex is not JSDisconnectedException)
            {
                _logger.LogWarning(ex, "Holler failed to restart media after the {Name} permission changed", name);
            }
        }
    });

    public Task ToggleMicrophoneAsync() => SetMicrophoneAsync(!MicrophoneEnabled);

    public Task ToggleCameraAsync() => SetCameraAsync(!CameraEnabled);

    /// <summary>Cameras, with a fallback name ("Camera 1") for any the browser doesn't label.</summary>
    public async Task<IReadOnlyList<HollerMediaDevice>> GetCamerasAsync() => Labelled(await GetDevicesAsync(), "video", "Camera");

    /// <summary>Microphones, with a fallback name ("Microphone 1") for any the browser doesn't label.</summary>
    public async Task<IReadOnlyList<HollerMediaDevice>> GetMicrophonesAsync() => Labelled(await GetDevicesAsync(), "audio", "Microphone");

    private static HollerMediaDevice[] Labelled(IEnumerable<HollerMediaDevice> devices, string kind, string noun) =>
        devices.Where(d => d.Kind == kind)
            .Select((d, i) => string.IsNullOrWhiteSpace(d.Label) ? d with { Label = $"{noun} {i + 1}" } : d)
            .ToArray();

    /// <summary>The camera in use (a <see cref="HollerMediaDevice.DeviceId"/>), if any.</summary>
    public string? CameraDeviceId { get; private set; }

    /// <summary>The microphone in use, if any.</summary>
    public string? MicrophoneDeviceId { get; private set; }

    /// <summary>
    /// Lists cameras and microphones. Labels are empty until the user has granted media permission,
    /// so call this after <see cref="StartMediaAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<HollerMediaDevice>> GetDevicesAsync()
    {
        await StartAsync();
        return await _engine!.InvokeAsync<HollerMediaDevice[]>("listDevices");
    }

    /// <summary>Switches to another camera without interrupting calls. The choice is remembered by the browser.</summary>
    public Task<HollerMediaResult> SelectCameraAsync(string deviceId) => SelectDeviceAsync("video", deviceId);

    /// <summary>Switches to another microphone without interrupting calls. The choice is remembered by the browser.</summary>
    public Task<HollerMediaResult> SelectMicrophoneAsync(string deviceId) => SelectDeviceAsync("audio", deviceId);

    private async Task<HollerMediaResult> SelectDeviceAsync(string kind, string deviceId)
    {
        if (LocalMedia is not { Ok: true } media)
            return new HollerMediaResult(false, false, false, "Media not started");
        var switched = await Exclusive(async () =>
            await _engine!.InvokeAsync<SwitchResult>("switchDevice", kind, deviceId));
        if (!switched.Ok)
            return new HollerMediaResult(false, media.Audio, media.Video, switched.Error);
        // Switching can add a kind we didn't have, e.g. plugging in a camera after starting audio-only.
        LocalMedia = kind == "video" ? media with { Video = true } : media with { Audio = true };
        await RefreshDeviceIdsAsync();
        await SendMediaStateAsync(ConnectedPeers());
        NotifyChanged();
        return LocalMedia;
    }

    private async Task RefreshDeviceIdsAsync()
    {
        CameraDeviceId = await _engine!.InvokeAsync<string?>("currentDevice", "video");
        MicrophoneDeviceId = await _engine!.InvokeAsync<string?>("currentDevice", "audio");
    }

    private sealed record SwitchResult(bool Ok, string? Error);

    /// <summary>Releases the camera and microphone (the browser's recording indicator goes off).</summary>
    public async Task StopMediaAsync()
    {
        if (_engine is null || LocalMedia is null)
            return;
        LocalMedia = null;
        CameraDeviceId = MicrophoneDeviceId = null;
        try
        {
            await _engine.InvokeVoidAsync("stopMedia");
            await SendMediaStateAsync(ConnectedPeers());
        }
        catch (JSDisconnectedException)
        {
        }
        NotifyChanged();
    }

    public async Task SetMicrophoneAsync(bool enabled)
    {
        MicrophoneEnabled = enabled;
        if (_engine is not null)
        {
            await _engine.InvokeVoidAsync("setTrackEnabled", "audio", enabled);
            await SendMediaStateAsync(ConnectedPeers());
        }
        NotifyChanged();
    }

    public async Task SetCameraAsync(bool enabled)
    {
        CameraEnabled = enabled;
        if (_engine is not null)
        {
            await _engine.InvokeVoidAsync("setTrackEnabled", "video", enabled);
            await SendMediaStateAsync(ConnectedPeers());
        }
        NotifyChanged();
    }

    private HollerPeer[] ConnectedPeers()
    {
        lock (_sync) return _peers.Values.Where(p => p.DataChannelOpen).ToArray();
    }

    /// <summary>Tells peers whether we are actually sending audio/video, so they can show a placeholder instead of a black frame.</summary>
    private async Task SendMediaStateAsync(IEnumerable<HollerPeer> peers)
    {
        if (_engine is null)
            return;
        var json = JsonSerializer.Serialize(new DataMessage(null, null,
            Audio: MicrophoneEnabled && LocalMedia is { Audio: true },
            Video: CameraEnabled && LocalMedia is { Video: true }));
        foreach (var peer in peers)
            await _engine.InvokeAsync<bool>("sendData", peer.Key, json);
    }

    /// <summary>Rings every session of <paramref name="userId"/>. Returns the existing call if one is active.</summary>
    public async Task<HollerCall> CallAsync(string userId)
    {
        await StartAsync();
        await RefreshIceServersAsync();
        HollerCall call;
        lock (_sync)
        {
            if (GetCall(userId) is { } existing)
                return existing;
            call = new HollerCall(Guid.NewGuid().ToString("N"), userId, HollerCallDirection.Outgoing);
            _calls[call.CallId] = call;
        }
        NotifyChanged();
        await _channel.SendAsync(new HollerSignal { Type = SignalType.Ring, CallId = call.CallId, ToUserId = userId });
        return call;
    }

    /// <summary>Accepts a ringing call. Any other active call is hung up first.</summary>
    public async Task AcceptAsync(HollerCall call)
    {
        if (call.Direction != HollerCallDirection.Incoming || call.State != HollerCallState.Ringing)
            return;
        foreach (var other in Calls.Where(c => c != call && c.State is HollerCallState.Connecting or HollerCallState.Connected))
            await HangupAsync(other);
        await RefreshIceServersAsync();

        await Exclusive(() =>
        {
            lock (_sync)
            {
                call.State = HollerCallState.Connecting;
                call.Peer = AddPeer(new HollerPeer(call.RemoteSessionId!, call.RemoteUserId, call.CallId, null));
            }
            return Task.CompletedTask;
        });
        NotifyChanged();
        // The caller creates the offer once it sees our Accept.
        await _channel.SendAsync(new HollerSignal { Type = SignalType.Accept, CallId = call.CallId, ToSessionId = call.RemoteSessionId });
    }

    public async Task DeclineAsync(HollerCall call)
    {
        if (call.Direction != HollerCallDirection.Incoming || call.State != HollerCallState.Ringing)
            return;
        await Exclusive(() => EndCallAsync(call, "Declined"));
        await _channel.SendAsync(new HollerSignal { Type = SignalType.Decline, CallId = call.CallId, ToSessionId = call.RemoteSessionId });
    }

    /// <summary>Ends (or cancels) a call.</summary>
    public async Task HangupAsync(HollerCall call)
    {
        if (!call.IsActive)
            return;
        if (call.Direction == HollerCallDirection.Incoming && call.State == HollerCallState.Ringing)
        {
            await DeclineAsync(call);
            return;
        }
        await Exclusive(() => EndCallAsync(call, "Hung up"));
        await _channel.SendAsync(new HollerSignal
        {
            Type = SignalType.Hangup,
            CallId = call.CallId,
            ToUserId = call.RemoteUserId,
            ToSessionId = call.RemoteSessionId,
        });
    }

    /// <summary>Joins a room and connects to every session already in it.</summary>
    public async Task<HollerRoom> JoinRoomAsync(string name)
    {
        await StartAsync();
        await RefreshIceServersAsync();
        HollerRoom room;
        lock (_sync)
        {
            if (_rooms.TryGetValue(name, out var existing))
                return existing;
            // Registered before joining so PeerJoined signals racing the join are not lost.
            room = new HollerRoom(name);
            _rooms[name] = room;
        }

        IReadOnlyList<HollerPeerInfo> members;
        try
        {
            members = await _channel.JoinRoomAsync(name);
        }
        catch
        {
            lock (_sync) _rooms.Remove(name);
            NotifyChanged();
            throw;
        }

        // The joiner offers to every existing member, so there is never offer glare on join.
        await Exclusive(async () =>
        {
            foreach (var member in members)
            {
                HollerPeer peer;
                lock (_sync)
                {
                    if (!_rooms.ContainsKey(name))
                        return;
                    peer = AddPeer(new HollerPeer(member.SessionId, member.UserId, null, name));
                    room.PeerList.Add(peer);
                }
                await CreateJsPeerAsync(peer, initiator: true);
            }
        });
        NotifyChanged();
        return room;
    }

    public async Task LeaveRoomAsync(string name)
    {
        HollerRoom? room;
        lock (_sync)
        {
            if (!_rooms.Remove(name, out room))
                return;
        }
        await Exclusive(async () =>
        {
            foreach (var peer in room.PeerList.ToArray())
                await ClosePeerAsync(peer);
            room.PeerList.Clear();
        });
        NotifyChanged();
        await _channel.LeaveRoomAsync(name);
    }

    /// <summary>Sends a chat message to the other side of a call.</summary>
    public Task SendMessageAsync(HollerCall call, string text) =>
        call.Peer is { } peer
            ? SendDataAsync([peer], null, CallConversation(call.RemoteUserId), text)
            : Task.CompletedTask;

    /// <summary>Sends a chat message to everyone in a room.</summary>
    public Task SendMessageAsync(string room, string text) =>
        GetRoom(room) is { } r
            ? SendDataAsync(r.Peers, room, RoomConversation(room), text)
            : Task.CompletedTask;

    private async Task SendDataAsync(IEnumerable<HollerPeer> peers, string? room, string conversation, string text)
    {
        if (_engine is null || string.IsNullOrWhiteSpace(text))
            return;
        var json = JsonSerializer.Serialize(new DataMessage(room, text));
        foreach (var peer in peers)
            await _engine.InvokeAsync<bool>("sendData", peer.Key, json);
        AddMessage(new HollerChatMessage(conversation, UserId ?? "", text, DateTimeOffset.Now, IsLocal: true));
    }

    private async Task RefreshIceServersAsync()
    {
        if (_engine is null || DateTimeOffset.UtcNow - _iceServersFetched < IceServerRefresh)
            return;
        try
        {
            var iceServers = await _channel.GetIceServersAsync();
            await _engine.InvokeVoidAsync("setIceServers", iceServers);
            _iceServersFetched = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is not JSDisconnectedException)
        {
            _logger.LogWarning(ex, "Holler failed to refresh ICE servers; keeping the previous ones");
        }
    }

    internal async Task AttachAsync(ElementReference element, string streamKey, bool muted)
    {
        await StartAsync();
        await _engine!.InvokeVoidAsync("attach", element, streamKey, muted);
    }

    internal async Task DetachAsync(ElementReference element, string streamKey)
    {
        if (_engine is null)
            return;
        try
        {
            await _engine.InvokeVoidAsync("detach", element, streamKey);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    // ---- incoming signals ----

    private Task OnSignalReceived(HollerSignal signal) => Exclusive(async () =>
    {
        try
        {
            await HandleSignalAsync(signal);
        }
        catch (Exception ex) when (ex is not JSDisconnectedException)
        {
            _logger.LogWarning(ex, "Holler failed to handle {Type} signal", signal.Type);
        }
        NotifyChanged();
    });

    private async Task HandleSignalAsync(HollerSignal s)
    {
        var from = s.FromSessionId ?? "";
        HollerCall? call = null;
        if (s.CallId is not null)
            lock (_sync) call = _calls.GetValueOrDefault(s.CallId);

        switch (s.Type)
        {
            case SignalType.Ring when s.CallId is not null && s.FromUserId is not null:
                bool busy;
                lock (_sync)
                {
                    busy = _calls.Values.Any(c => c.State is HollerCallState.Connecting or HollerCallState.Connected);
                    if (!busy)
                    {
                        call = new HollerCall(s.CallId, s.FromUserId, HollerCallDirection.Incoming) { RemoteSessionId = from };
                        _calls[call.CallId] = call;
                    }
                }
                if (busy)
                    await _channel.SendAsync(new HollerSignal { Type = SignalType.Busy, CallId = s.CallId, ToSessionId = from });
                else
                    IncomingCall?.Invoke(call!);
                break;

            case SignalType.Accept when call is { Direction: HollerCallDirection.Outgoing, State: HollerCallState.Ringing }:
                HollerPeer peer;
                lock (_sync)
                {
                    call.RemoteSessionId = from;
                    call.State = HollerCallState.Connecting;
                    peer = call.Peer = AddPeer(new HollerPeer(from, call.RemoteUserId, call.CallId, null));
                }
                await CreateJsPeerAsync(peer, initiator: true);
                break;

            case SignalType.Decline when call is not null:
                await EndCallAsync(call, "Declined");
                break;
            case SignalType.Busy when call is not null:
                await EndCallAsync(call, "Busy");
                break;
            case SignalType.Unavailable when call is not null:
                await EndCallAsync(call, "Unavailable");
                break;
            case SignalType.NoAnswer when call is not null:
                await EndCallAsync(call, "No answer");
                break;
            case SignalType.AnsweredElsewhere when call is not null:
                await EndCallAsync(call, "Answered elsewhere");
                break;
            case SignalType.Hangup when call is not null:
                await EndCallAsync(call, call.State == HollerCallState.Ringing && call.Direction == HollerCallDirection.Incoming ? "Missed call" : "Remote hung up");
                break;

            case SignalType.Description or SignalType.Ice:
                var key = s.CallId is not null ? $"call:{s.CallId}:{from}" : $"room:{s.Room}:{from}";
                HollerPeer? target;
                lock (_sync) target = _peers.GetValueOrDefault(key);
                if (target is null || _engine is null)
                    return;
                if (!target.Created)
                    await CreateJsPeerAsync(target, initiator: false);
                await _engine.InvokeVoidAsync("signal", key, s.Type == SignalType.Description ? "description" : "ice", s.Payload);
                break;

            case SignalType.PeerJoined when s.Room is not null && s.FromUserId is not null:
                lock (_sync)
                {
                    if (_rooms.TryGetValue(s.Room, out var room) && !_peers.ContainsKey($"room:{s.Room}:{from}"))
                        room.PeerList.Add(AddPeer(new HollerPeer(from, s.FromUserId, null, s.Room)));
                }
                break;

            case SignalType.PeerLeft when s.Room is not null:
                HollerPeer? left = null;
                lock (_sync)
                {
                    if (_rooms.TryGetValue(s.Room, out var room))
                    {
                        left = room.PeerList.FirstOrDefault(p => p.SessionId == from);
                        if (left is not null)
                            room.PeerList.Remove(left);
                    }
                }
                if (left is not null)
                    await ClosePeerAsync(left);
                break;

            case SignalType.PresenceChanged when s.FromUserId is not null:
                lock (_sync)
                {
                    if (s.Payload == "offline")
                        _online.Remove(s.FromUserId);
                    else
                        _online.Add(s.FromUserId);
                }
                break;
        }
    }

    private async Task OnSessionChanged(HollerSessionInfo session) => await Exclusive(async () =>
    {
        // A reconnect gives us a new session; the server already dropped the old one,
        // so tear everything down and rejoin rooms.
        _session = session;
        string[] roomNames;
        lock (_sync) roomNames = _rooms.Keys.ToArray();
        foreach (var call in Calls)
            await EndCallAsync(call, "Connection lost");
        foreach (var peer in _peers.Values.ToArray())
            await ClosePeerAsync(peer);
        lock (_sync) _rooms.Clear();
        NotifyChanged();
        _ = Task.Run(async () =>
        {
            foreach (var name in roomNames)
            {
                try
                {
                    await Post(() => JoinRoomAsync(name));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Holler failed to rejoin room {Room}", name);
                }
            }
        });
    });

    // ---- callbacks from holler.js ----

    private async Task OnLocalSignalAsync(string key, string type, string payload)
    {
        HollerPeer? peer;
        lock (_sync) peer = _peers.GetValueOrDefault(key);
        if (peer is null)
            return;
        await _channel.SendAsync(new HollerSignal
        {
            Type = type == "ice" ? SignalType.Ice : SignalType.Description,
            CallId = peer.CallId,
            Room = peer.Room,
            ToSessionId = peer.SessionId,
            Payload = payload,
        });
    }

    private Task OnPeerStateAsync(string key, string state) => Exclusive(async () =>
    {
        HollerPeer? peer;
        HollerCall? call = null;
        lock (_sync)
        {
            peer = _peers.GetValueOrDefault(key);
            if (peer is null)
                return;
            if (state == "datachannel-open")
                peer.DataChannelOpen = true;
            else
                peer.ConnectionState = state;
            if (peer.CallId is not null)
                call = _calls.GetValueOrDefault(peer.CallId);
            if (call is not null && state == "connected")
                call.State = HollerCallState.Connected;
        }
        if (state == "datachannel-open")
            await SendMediaStateAsync([peer]);
        if (call is not null && state is "failed" or "closed")
        {
            await EndCallAsync(call, "Connection lost");
            await _channel.SendAsync(new HollerSignal { Type = SignalType.Hangup, CallId = call.CallId, ToSessionId = call.RemoteSessionId });
        }
        NotifyChanged();
    });

    private Task OnRemoteTrackAsync(string key, string kind) => Post(() =>
    {
        lock (_sync)
        {
            if (_peers.GetValueOrDefault(key) is { } peer)
            {
                if (kind == "video") peer.HasVideo = true;
                else peer.HasAudio = true;
            }
        }
        NotifyChanged();
        return Task.CompletedTask;
    });

    private Task OnDataAsync(string key, string json) => Post(() =>
    {
        HollerPeer? peer;
        lock (_sync) peer = _peers.GetValueOrDefault(key);
        DataMessage? data = null;
        try
        {
            data = JsonSerializer.Deserialize<DataMessage>(json);
        }
        catch (JsonException)
        {
        }
        if (peer is null || data is null)
            return Task.CompletedTask;
        if (data.Audio is not null || data.Video is not null)
        {
            lock (_sync)
            {
                peer.AudioEnabled = data.Audio ?? peer.AudioEnabled;
                peer.VideoEnabled = data.Video ?? peer.VideoEnabled;
            }
            NotifyChanged();
        }
        if (data.Text is null)
            return Task.CompletedTask;
        var conversation = peer.Room is not null ? RoomConversation(peer.Room) : CallConversation(peer.UserId);
        var message = new HollerChatMessage(conversation, peer.UserId, data.Text, DateTimeOffset.Now, IsLocal: false);
        AddMessage(message);
        MessageReceived?.Invoke(message);
        return Task.CompletedTask;
    });

    // ---- helpers (call with _gate held unless noted) ----

    private HollerPeer AddPeer(HollerPeer peer)
    {
        // caller holds _sync
        _peers[peer.Key] = peer;
        return peer;
    }

    private async Task CreateJsPeerAsync(HollerPeer peer, bool initiator)
    {
        if (peer.Created || _engine is null)
            return;
        peer.Created = true;
        // Perfect negotiation needs exactly one polite side; break the tie on session id.
        var polite = string.CompareOrdinal(SessionId, peer.SessionId) > 0;
        await _engine.InvokeVoidAsync("createPeer", peer.Key, polite, initiator);
    }

    private async Task ClosePeerAsync(HollerPeer peer)
    {
        lock (_sync) _peers.Remove(peer.Key);
        if (peer.Created && _engine is not null)
            await _engine.InvokeVoidAsync("closePeer", peer.Key);
    }

    private async Task EndCallAsync(HollerCall call, string reason)
    {
        lock (_sync)
        {
            if (!_calls.Remove(call.CallId))
                return;
            call.State = HollerCallState.Ended;
            call.EndReason = reason;
        }
        if (call.Peer is { } peer)
            await ClosePeerAsync(peer);
        NotifyChanged();
    }

    private void AddMessage(HollerChatMessage message)
    {
        lock (_sync)
        {
            if (!_messages.TryGetValue(message.Conversation, out var list))
                _messages[message.Conversation] = list = [];
            list.Add(message);
        }
        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();

    /// <summary>Runs <paramref name="work"/> on the client's context, serialized with all other state changes.</summary>
    private Task Exclusive(Func<Task> work) => Post(async () =>
    {
        await _gate.WaitAsync();
        try
        {
            await work();
        }
        finally
        {
            _gate.Release();
        }
    });

    private async Task<T> Exclusive<T>(Func<Task<T>> work)
    {
        T result = default!;
        // Statement body so this binds to the non-generic overload instead of recursing.
        await Exclusive(async () => { result = await work(); });
        return result;
    }

    private Task Post(Func<Task> work)
    {
        var context = _context;
        if (context is null || SynchronizationContext.Current == context)
            return work();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(async _ =>
        {
            try
            {
                await work();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _channel.SignalReceived -= OnSignalReceived;
        _channel.SessionChanged -= OnSessionChanged;
        try
        {
            foreach (var call in Calls)
                await _channel.SendAsync(new HollerSignal { Type = SignalType.Hangup, CallId = call.CallId, ToUserId = call.RemoteUserId, ToSessionId = call.RemoteSessionId });
            if (_engine is not null)
            {
                await _engine.InvokeVoidAsync("dispose");
                await _engine.DisposeAsync();
            }
            if (_module is not null)
                await _module.DisposeAsync();
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or InvalidOperationException or TaskCanceledException)
        {
            // Browser or connection already gone.
        }
        _callbacks?.Dispose();
    }

    /// <summary>Data channel message: chat (<see cref="Text"/>) and/or the sender's media state.</summary>
    private sealed record DataMessage(string? Room, string? Text, bool? Audio = null, bool? Video = null);

    /// <summary>Target for callbacks from holler.js; kept separate so <see cref="HollerClient"/> has no public JSInvokable surface.</summary>
    private sealed class JsCallbacks(HollerClient client)
    {
        [JSInvokable] public Task OnLocalSignal(string key, string type, string payload) => client.OnLocalSignalAsync(key, type, payload);
        [JSInvokable] public Task OnPeerState(string key, string state) => client.OnPeerStateAsync(key, state);
        [JSInvokable] public Task OnRemoteTrack(string key, string kind) => client.OnRemoteTrackAsync(key, kind);
        [JSInvokable] public Task OnData(string key, string json) => client.OnDataAsync(key, json);
        [JSInvokable] public Task OnMediaPermissionChanged(string name, string state) => client.OnMediaPermissionChangedAsync(name, state);
    }
}
