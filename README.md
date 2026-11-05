[![Build and Test](https://github.com/tomlm/Holler/actions/workflows/BuildAndRunTests.yml/badge.svg)](https://github.com/tomlm/Holler/actions/workflows/BuildAndRunTests.yml)[![NuGet](https://img.shields.io/nuget/v/Holler.svg)](https://www.nuget.org/packages/Holler)

![Logo](https://raw.githubusercontent.com/tomlm/Holler/refs/heads/main/icon.png)

# Holler

![Screenshot](https://raw.githubusercontent.com/tomlm/Holler/refs/heads/main/screenshot.gif)

Dead-simple peer-to-peer WebRTC video for Blazor.

- **Media is peer-to-peer.** Audio, video and chat go browser-to-browser over `RTCPeerConnection` and data channels.
- **SignalR is only for signaling**: ringing, accept/decline, SDP offers/answers and ICE candidates.
- **Every render mode works**: Interactive Server, WebAssembly and Auto, and calls cross between them.
  Server circuits signal in-process; WebAssembly clients connect to the Holler hub.
- **1:1 calls** to a *user* (every tab or device they have open rings; the first to answer wins), plus
  **small rooms** (full mesh, 6 by default) and **text chat** over the data channel.

## Quick start

**Server** `Program.cs` (Holler needs to know who the user is, so set up authentication as usual):

```csharp
builder.Services.AddHoller();          // hub + router + HollerClient for Server render mode
...
app.UseAuthentication();
app.UseAuthorization();
app.MapHollerHub();                    // /holler
```

**WebAssembly client** `Program.cs` (only if you use WebAssembly or Auto):

```csharp
builder.Services.AddHollerClient();
```

**Components** (`@using Holler`, in any interactive render mode):

```razor
<HollerIncomingCall OnAccepted="call => _with = call.RemoteUserId" />  @* rings this tab *@

<HollerVideoCall UserId="bob" />       @* video call with bob: call, accept, mute, camera, hang up *@
<HollerChat UserId="bob" />            @* chat over the call's data channel *@

<HollerVideoRoom Name="standup" />     @* everyone in "standup", in a grid *@
<HollerChat Room="standup" />
```

The stylesheet loads automatically through a Blazor JS initializer. To restyle, override the `--holler-*` CSS
custom properties or the `.holler-*` classes.

## Components

| Component | Purpose |
|---|---|
| `HollerVideoCall` | 1:1 call UI for `UserId`: remote video, local preview, call/accept/decline/hang up, mute and camera toggles. `AutoStart` rings as soon as it's shown. |
| `HollerIncomingCall` | Accept/decline prompt for incoming calls. Put it in an interactive layout or page; it also keeps the tab connected so it can be rung. |
| `HollerVideoRoom` | Joins room `Name` (`AutoJoin`) and shows every member in a grid. |
| `HollerChat` | Data-channel chat for a call (`UserId`) or a room (`Room`). |
| `HollerVideo` | A single `<video>` for a `HollerPeer`, or your camera with `Local`. Use it to build your own layout. `Muted` silences it (see `HollerAudio`). |
| `HollerAudio` | Plays a peer's audio from a hidden element, so it keeps playing while their video isn't on screen. |
| `HollerMediaControls` | Mute and camera buttons, each with a device picker, plus guidance and **Try again** when the browser has blocked the camera or microphone. |

To build your own UI, inject `HollerClient` (or inherit `HollerComponentBase`, which starts the client after the
first interactive render and re-renders on every change). It provides `CallAsync`, `AcceptAsync`, `DeclineAsync`,
`HangupAsync`, `JoinRoomAsync`, `LeaveRoomAsync`, `SendMessageAsync`, `OnlineUsers`, `Calls` and `Rooms`, the camera
and microphone API below, and the `Changed`, `IncomingCall` and `MessageReceived` events.

## Camera and microphone

Media is opt-in: nothing touches the camera or microphone until you call `StartMediaAsync` (the call and room
components do it for you).

| Member | Purpose |
|---|---|
| `StartMediaAsync(audio, video, videoSettings)` | Asks for the camera and/or microphone. Falls back to audio only if there's no camera or it's blocked. Returns a `HollerMediaResult` (`Ok`, `Audio`, `Video`, `Error`, `PermissionDenied`), also kept in `LocalMedia`. |
| `StopMediaAsync()` | Releases both, so the browser's recording indicator goes off (e.g. when leaving the page that uses them). |
| `IsMicrophoneOn`, `IsCameraOn` | What a mute / show-video button should show: the device is running *and* not muted/hidden. |
| `ToggleMicrophoneAsync()`, `ToggleCameraAsync()` (or `SetMicrophoneAsync(bool)`, `SetCameraAsync(bool)`) | Mute and hide video. Peers are told, so they can show a placeholder instead of a black frame (`HollerPeer.VideoEnabled` / `AudioEnabled`, `IsVideoVisible`); once they stop drawing your video, you stop sending it to them. |
| `GetCamerasAsync()`, `GetMicrophonesAsync()` | Devices for a picker, with fallback names ("Camera 1") where the browser gives none. Call after media has started: browsers hide device names until permission is granted. |
| `SelectCameraAsync(id)`, `SelectMicrophoneAsync(id)`, `CameraDeviceId`, `MicrophoneDeviceId` | Switch devices mid-call. The new track replaces the old one on every connection, so nothing reconnects. The browser remembers the choice for next time (falling back to the default if that device is gone). |
| `RetryMediaAsync()` | Asks again for whatever `StartMediaAsync` requested but didn't get, adding it to calls already in progress. |

### Video size follows what viewers actually draw

`HollerVideoSettings(MaxWidth, MaxHeight, MaxFrameRate, MaxBitrateKbps)` are **ceilings**, not what everyone
receives. Each viewer measures how big it draws your video (in device pixels) and tells you over the data channel;
you then send that viewer a copy scaled down to that size, with the bitrate scaled to match, and **nothing at all**
while it isn't showing your video. A 56 px avatar costs a viewer roughly 100 kbps instead of the camera's full
resolution, and hiding a drawer stops that video entirely.

```csharp
await holler.StartMediaAsync(videoSettings: new(MaxWidth: 640, MaxHeight: 480, MaxFrameRate: 24, MaxBitrateKbps: 800));
```

The encoder keeps resolution and drops frames when bandwidth is short, so small thumbnails are sharp within a
couple of seconds instead of starting blurry.

### When the user blocks the camera or microphone

Browsers don't ask twice: after the user clicks **Block** (Chrome/Edge), every request fails immediately until
they allow it again from the camera icon in the address bar. (A dismissed prompt, or Firefox without "Remember
this decision", does ask again.) Holler makes recovery painless:

- `LocalMedia.PermissionDenied` is set, so you can explain what to do. `HollerMediaControls` shows the
  instructions and a **Try again** button.
- Holler watches the browser's permission state and restarts media **by itself** as soon as the user switches it
  to Allow, with no reload.
- `RetryMediaAsync()` retries on demand (e.g. from your own button).

Camera access needs a secure origin: HTTPS, or `http://localhost` during development. To test from a second
machine, use HTTPS on your LAN address or a tunnel. With Visual Studio dev tunnels, run the **http** launch
profile: tunnels to the HTTPS endpoint don't carry Blazor's WebSocket.

### Picture-in-picture and autoplay

Holler's videos opt out of the browser's picture-in-picture button. If the browser blocks audio until the user
interacts with the page (common when joining on page load), Holler starts playback on the first click or key press.

## Configuration

```csharp
builder.Services.AddHoller(o =>
{
    o.HubPath = "/holler";                       // must match AddHollerClient(o => o.HubPath = "holler")
    o.IceServers = [new(["stun:stun.l.google.com:19302"])];
    o.ForceRelay = false;                        // true: all media through TURN (see TURN below)
    o.RingTimeout = TimeSpan.FromSeconds(45);    // then the caller gets "No answer"
    o.MaxRoomSize = 6;
    o.Limits.RingsPerMinute = 10;                // per session; also SignalsPerSecond, MaxPayloadLength, MaxRoomsPerSession
    o.RequireAuthorization = true;               // false: anonymous users get a per-session guest id
});
```

`HollerOptions` binds from configuration as usual, e.g. `builder.Services.Configure<HollerOptions>(builder.Configuration.GetSection("Holler"))`.

## TURN (for players behind strict NATs and firewalls)

Most players connect directly. Those on corporate networks, some mobile carriers, or carrier-grade NAT need a
**TURN relay**, or they get no video at all. Holler gets ICE servers from an `IIceServerProvider`; pick one:

**Cloudflare Realtime TURN** (package `Holler.Cloudflare`). Hosted, no servers to run; the first 1,000 GB a month
is free, then $0.05/GB (check Cloudflare's current pricing). Only relayed players count.

```csharp
builder.Services.AddHoller();
builder.Services.AddHollerCloudflareTurn();      // binds Holler:CloudflareTurn:KeyId / ApiToken / CredentialLifetime
```

Create a TURN key in the Cloudflare dashboard (Realtime, then TURN Server) and keep its token secret, e.g.
`dotnet user-secrets set "Holler:CloudflareTurn:ApiToken" "..."` locally and app settings or Key Vault in production
(`Holler__CloudflareTurn__ApiToken`).

**Your own coturn** with its shared secret (`use-auth-secret`), no extra package:

```csharp
builder.Services.AddHoller(o => o.Turn = new()
{
    Urls = ["turn:turn.example.com:3478?transport=udp", "turns:turn.example.com:443?transport=tcp"],
    SharedSecret = builder.Configuration["Turn:Secret"]!,   // coturn static-auth-secret
});
```

**Any other TURN service** (Twilio, Metered, ...): derive from `TurnCredentialProvider` and implement one method
that fetches credentials for a user. The base class caches each user's credentials until half their lifetime, adds
your static STUN servers, drops URLs browsers can't use, and falls back to STUN only (direct connections) if the
service is unreachable. Register it as the `IIceServerProvider`:

```csharp
public sealed class MyTurnProvider(IOptions<HollerOptions> options, TimeProvider time, ILogger<MyTurnProvider> logger)
    : TurnCredentialProvider(options, time, logger)
{
    protected override async Task<TurnCredentials> FetchCredentialsAsync(string userId, CancellationToken ct)
    {
        // Call your provider's API...
        return new TurnCredentials([new HollerIceServer(["turn:turn.example.com:3478"], "username", "password")],
            ExpiresAt: Time.GetUtcNow().AddHours(12));
    }
}

builder.Services.AddSingleton<IIceServerProvider, MyTurnProvider>();
```

Credentials are per user and short-lived, so secrets never reach the browser. Clients re-fetch them before new calls
and room joins once they are 10 minutes old. A connection that is already relaying keeps the credentials it started
with, so make their lifetime longer than a session (Cloudflare allows up to 48 hours; the default is 24).

**Prove it works:** set `ForceRelay` (or `Holler__ForceRelay=true`) and every connection must go through TURN. If
video still connects, your TURN setup works. It also hides players' IP addresses from each other.

## Extension points

Register your own before `AddHoller`:

- `IHollerUserIdProvider` decides the user id people call. The default is the `NameIdentifier` claim, falling back to `Identity.Name`.
- `IHollerAuthorizer` decides who may call whom (`CanCallAsync`) and join which room (`CanJoinRoomAsync`). The
  default allows everything. A denied call looks to the caller like an offline user, so it reveals nothing.
- `IIceServerProvider` supplies ICE servers per user (see TURN above).

## How it works

```
 Browser A ── RTCPeerConnection (audio/video/data, P2P) ── Browser B
     │                                                      │
 HollerClient ── signals ──► HollerSignalRouter ◄── signals ── HollerClient
 (WASM: SignalR hub,         (server, in memory)            (Server: in-process)
  Server: in-process)
```

- **Calls:** the caller rings every session of the callee. The first `Accept` wins and the other sessions
  get `AnsweredElsewhere`. The caller then sends the offer.
- **Rooms:** a joiner gets the current member list and offers to each member, so offers never collide on join.
  Later renegotiation uses WebRTC "perfect negotiation".
- **Trust:** the router stamps every signal with the authenticated sender. It only forwards SDP and ICE between
  sessions that share an accepted call or a room, and rejects server-only signal types sent by clients.
  Per-session rate and size limits drop floods, and an unanswered call stops ringing after `RingTimeout`.
- **Scale-out:** routing state is in memory, so all users must reach the same server instance (use sticky
  sessions or a single instance). A distributed router is future work.

## Repo layout

- `src/Holler`: components, `HollerClient`, the WebRTC engine (`wwwroot/holler.js`) and hub signaling for WebAssembly
- `src/Holler.Server`: the hub, the router and the in-process channel for Server circuits
- `src/Holler.Cloudflare`: Cloudflare Realtime TURN provider (`AddHollerCloudflareTurn`)
- `src/Holler.Tests`: router tests
- `src/Holler.E2E`: Playwright tests that run the sample in headless Chromium with fake camera/mic devices
- `src/samples/Holler.Sample`: a demo with name-only sign-in, presence, 1:1 calls, rooms and chat in every render mode

Run the sample with `dotnet run --project src/samples/Holler.Sample/Holler.Sample`. Sign in with two different
names in two browsers (or a normal and a private window) and call each other.

## Tests

```
dotnet build src/Holler.slnx
dotnet test src/Holler.Tests
pwsh src/Holler.E2E/bin/Debug/net10.0/playwright.ps1 install chromium   # once
dotnet test src/Holler.E2E
```

The E2E fixture starts the sample on a free port. It drives real WebRTC calls and rooms between several
signed-in browser contexts, across Server, WebAssembly and Auto. If Chromium isn't installed, the tests are skipped.

The relay test (`RelayTests`) proves TURN end to end with relay-only mode. It needs a Cloudflare TURN key in the
test project's user secrets, and is skipped without one:

```
dotnet user-secrets set "Holler:CloudflareTurn:KeyId" "<turn token id>" --project src/Holler.E2E
dotnet user-secrets set "Holler:CloudflareTurn:ApiToken" "<api token>" --project src/Holler.E2E
```
