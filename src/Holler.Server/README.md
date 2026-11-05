![Logo](https://raw.githubusercontent.com/tomlm/Holler/refs/heads/main/icon.png)

# Holler.Server

The server side of [Holler](https://www.nuget.org/packages/Holler), peer-to-peer WebRTC video for Blazor:

- the **SignalR hub** WebAssembly clients signal through,
- the **router** that tracks sessions, presence, rooms and calls, stamps every signal with its authenticated sender,
  and only forwards WebRTC negotiation between sessions that share a call or room,
- an **in-process channel** so Interactive Server components signal without a SignalR round trip back to the server,
- `HollerClient` registration for Server-rendered components, and ICE (STUN/TURN) server configuration.

It references `Holler`, so a Blazor Server app only needs this package.

## Set up

```csharp
builder.Services.AddAuthentication(/* your scheme */);
builder.Services.AddAuthorization();
builder.Services.AddHoller();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapHollerHub();     // only needed for WebAssembly / Auto clients
```

Users are identified by the `NameIdentifier` claim (falling back to `Identity.Name`); that's the id other people
call. Each tab or device is a separate session of the same user.

## Configure

```csharp
builder.Services.AddHoller(o =>
{
    o.HubPath = "/holler";                         // must match the client's AddHollerClient HubPath
    o.IceServers = [new(["stun:stun.l.google.com:19302"])];
    o.RequireAuthorization = true;                 // false: anonymous users get a per-session guest id
    o.MaxRoomSize = 6;                             // rooms are a full mesh
    o.RingTimeout = TimeSpan.FromSeconds(45);      // unanswered calls end with "No answer"
    o.Limits.RingsPerMinute = 10;                  // also SignalsPerSecond, MaxPayloadLength, MaxRoomsPerSession
    o.ForceRelay = false;                          // true: all media through TURN
});
```

`HollerOptions` also binds from configuration:

```csharp
builder.Services.Configure<HollerOptions>(builder.Configuration.GetSection("Holler"));
```

In environment variables or Azure App Service settings, use `__` for the section separator, e.g.
`Holler__RingTimeout=00:00:30`.

## TURN

Players behind strict NATs or firewalls need a TURN relay. Options:

- **Cloudflare Realtime TURN**: add [`Holler.Cloudflare`](https://www.nuget.org/packages/Holler.Cloudflare) and call
  `AddHollerCloudflareTurn()`.
- **Your own coturn** with a shared secret (`use-auth-secret`):

  ```csharp
  builder.Services.AddHoller(o => o.Turn = new()
  {
      Urls = ["turn:turn.example.com:3478?transport=udp", "turns:turn.example.com:443?transport=tcp"],
      SharedSecret = builder.Configuration["Turn:Secret"]!,
  });
  ```

- **Any other service**: derive from `TurnCredentialProvider`, implement `FetchCredentialsAsync`, and register it as
  `IIceServerProvider`. The base class caches per-user credentials, adds your STUN servers, drops URLs browsers can't
  use, and falls back to STUN only if the service is down.

Set `ForceRelay = true` to prove TURN works: video then only connects through the relay.

## Extension points

Register your own before `AddHoller()`:

| Service | Purpose | Default |
|---|---|---|
| `IHollerUserIdProvider` | Maps a `ClaimsPrincipal` to the user id people call. | `NameIdentifier`, then `Identity.Name` |
| `IHollerAuthorizer` | Who may call whom (`CanCallAsync`) and join which room (`CanJoinRoomAsync`). Denied calls look like an offline user. | Allow everything |
| `IIceServerProvider` | STUN/TURN servers per user. | `HollerOptions.IceServers` (+ `Turn`) |

## Deployment notes

Routing state is in memory: every user must reach the same server instance (one instance, or sticky sessions with
everyone on the same node). A restart ends active calls until clients reconnect.

Full documentation: https://github.com/tomlm/Holler
