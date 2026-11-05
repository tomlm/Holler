![Logo](https://raw.githubusercontent.com/tomlm/Holler/refs/heads/main/icon.png)

# Holler

Peer-to-peer WebRTC video, audio and chat for Blazor. Media flows browser-to-browser; SignalR only carries the
signaling (ringing, offers/answers, ICE candidates).

This package is the **client side**: the Blazor components, `HollerClient`, and the browser WebRTC engine. You
always also need [`Holler.Server`](https://www.nuget.org/packages/Holler.Server) in your server project.

| Your app | Install |
|---|---|
| Blazor Server only (Interactive Server) | `Holler.Server` only (it brings in this package) |
| Blazor WebAssembly or Auto | `Holler.Server` in the server project, **and** `Holler` in the `.Client` project |

## Set up

Server project (see `Holler.Server` for options):

```csharp
builder.Services.AddHoller();
// ...
app.MapHollerHub();
```

WebAssembly client project (`Program.cs`), only if you use WebAssembly or Auto:

```csharp
builder.Services.AddHollerClient();   // or AddHollerClient(o => o.HubPath = "holler")
```

`HubPath` (default `holler`) must match the server's `HollerOptions.HubPath` (default `/holler`). Use
`ConfigureConnection` to customize the SignalR connection (access tokens, transports).

The stylesheet loads automatically through a Blazor JS initializer; no `<link>` or `<script>` tags are needed.

## Use it

Add `@using Holler` and use the components in any interactive render mode:

```razor
<HollerIncomingCall OnAccepted="call => _with = call.RemoteUserId" />   @* lets this tab be rung *@

<HollerVideoCall UserId="bob" />      @* 1:1 call with a user *@
<HollerChat UserId="bob" />

<HollerVideoRoom Name="standup" />    @* everyone in a room, full mesh *@
<HollerChat Room="standup" />
```

| Component | Purpose |
|---|---|
| `HollerVideoCall` | 1:1 call UI: remote video, self view, call/accept/decline/hang up, media controls. |
| `HollerIncomingCall` | Accept/decline prompt; keeps the tab connected so it can be rung. |
| `HollerVideoRoom` | Joins a room and shows every member in a grid. |
| `HollerChat` | Data-channel chat for a call or a room. |
| `HollerVideo` | One `<video>` for a peer (or your camera with `Local`), for your own layouts. |
| `HollerAudio` | Plays a peer's audio from a hidden element, independent of their video. |
| `HollerMediaControls` | Mute and camera buttons with device pickers, and guidance when the camera is blocked. |

For your own UI, inject `HollerClient` (or inherit `HollerComponentBase`):

- Calls and rooms: `CallAsync`, `AcceptAsync`, `DeclineAsync`, `HangupAsync`, `JoinRoomAsync`, `LeaveRoomAsync`,
  `SendMessageAsync`, plus `Calls`, `Rooms`, `OnlineUsers`.
- Camera and microphone: `StartMediaAsync`, `StopMediaAsync`, `IsMicrophoneOn`/`IsCameraOn`,
  `ToggleMicrophoneAsync`/`ToggleCameraAsync`, `GetCamerasAsync`/`GetMicrophonesAsync`,
  `SelectCameraAsync`/`SelectMicrophoneAsync`, `RetryMediaAsync`, and `MediaIssue` (why the camera or microphone
  isn't running, in words for the user: blocked, in use by another app, or missing).
- Diagnostics: `HollerPeer.Route` (direct or relayed through TURN), and logging to `ILogger` (category
  `Holler.HollerClient`): media results, connection states and routes per player.
- Events: `Changed`, `IncomingCall`, `MessageReceived`.

### Video bandwidth

`StartMediaAsync(videoSettings: new(MaxWidth: 640, MaxHeight: 480, MaxFrameRate: 24, MaxBitrateKbps: 800))` sets
**ceilings**. Each viewer is sent only the resolution it actually draws your video at, and nothing while it isn't
showing it, so small thumbnails cost little.

## Requirements

- An interactive render mode (Server, WebAssembly or Auto). Components start after the first interactive render.
- A secure origin for camera access: HTTPS, or `http://localhost` in development.
- Signed-in users by default (configurable on the server).

Full documentation: https://github.com/tomlm/Holler
