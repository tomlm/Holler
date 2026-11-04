# Holler

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
| `HollerVideo` | A single `<video>` for a `HollerPeer`, or your camera with `Local`. Use it to build your own layout. |
| `HollerMediaToggles` | Mute and camera buttons. |

To build your own UI, inject `HollerClient` (or inherit `HollerComponentBase`). It provides `CallAsync`,
`AcceptAsync`, `DeclineAsync`, `HangupAsync`, `JoinRoomAsync`, `LeaveRoomAsync`, `SendMessageAsync`,
`SetMicrophoneAsync`, `SetCameraAsync`, `OnlineUsers`, `Calls` and `Rooms`, plus the `Changed`, `IncomingCall` and
`MessageReceived` events.

## Configuration

```csharp
builder.Services.AddHoller(o =>
{
    o.HubPath = "/holler";                       // must match AddHollerClient(o => o.HubPath = "holler")
    o.IceServers = [new(["stun:stun.l.google.com:19302"])];
    o.Turn = new()                               // needed for users behind strict NATs
    {
        Urls = ["turn:turn.example.com:3478", "turns:turn.example.com:5349"],
        SharedSecret = builder.Configuration["Turn:Secret"]!,   // coturn static-auth-secret
        CredentialLifetime = TimeSpan.FromHours(12),
    };
    o.RingTimeout = TimeSpan.FromSeconds(45);    // then the caller gets "No answer"
    o.MaxRoomSize = 6;
    o.Limits.RingsPerMinute = 10;                // per session; also SignalsPerSecond, MaxPayloadLength, MaxRoomsPerSession
    o.RequireAuthorization = true;               // false: anonymous users get a per-session guest id
});
```

`HollerOptions` binds from configuration as usual, e.g. `builder.Services.Configure<HollerOptions>(builder.Configuration.GetSection("Holler"))`.

**TURN.** `Turn` uses coturn's shared-secret "TURN REST API" (`use-auth-secret`). Each user gets a credential
that expires, derived from the secret. The secret itself never leaves the server. Clients re-fetch credentials before
new connections once they are 10 minutes old.

Extension points (register your own before `AddHoller`):

- `IHollerUserIdProvider` decides the user id people call. The default is the `NameIdentifier` claim, falling back to `Identity.Name`.
- `IHollerAuthorizer` decides who may call whom (`CanCallAsync`) and join which room (`CanJoinRoomAsync`). The
  default allows everything. A denied call looks to the caller like an offline user, so it reveals nothing.
- `IIceServerProvider` replaces the ICE server list entirely, e.g. for a hosted TURN service with its own API.

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
