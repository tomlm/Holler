![Logo](https://raw.githubusercontent.com/tomlm/Holler/refs/heads/main/icon.png)

# Holler.Cloudflare

[Cloudflare Realtime TURN](https://developers.cloudflare.com/realtime/turn/) for
[Holler](https://www.nuget.org/packages/Holler.Server), so players behind strict NATs and firewalls (corporate
networks, many mobile carriers, carrier-grade NAT) still get video.

Most players connect directly; TURN only relays the connections that can't. Each user gets short-lived TURN
credentials from Cloudflare's API, issued on your server, so your Cloudflare key never reaches the browser.

## Set up

```csharp
builder.Services.AddHoller();
builder.Services.AddHollerCloudflareTurn();
```

Settings bind from the `Holler:CloudflareTurn` configuration section:

| Setting | Purpose |
|---|---|
| `KeyId` | The TURN key's id ("Turn Token ID" in the dashboard). |
| `ApiToken` | The TURN key's API token. **Secret.** |
| `CredentialLifetime` | How long issued credentials last. Default `24:00:00`, max 48 hours. |

or in code (applied after configuration binding):

```csharp
builder.Services.AddHollerCloudflareTurn(o => o.CredentialLifetime = TimeSpan.FromHours(12));
```

To leave TURN off where no key is configured (e.g. other developers' machines):

```csharp
if (!string.IsNullOrWhiteSpace(builder.Configuration["Holler:CloudflareTurn:KeyId"]))
    builder.Services.AddHollerCloudflareTurn();
```

## Get a key

In the [Cloudflare dashboard](https://dash.cloudflare.com/?to=/:account/calls), open **Realtime**, then
**TURN Server**, and create a key. Copy the **Turn Token ID** and **API Token** (copy the token straight away).

Store them as secrets, never in source:

- Locally: `dotnet user-secrets set "Holler:CloudflareTurn:ApiToken" "<token>"` (and `KeyId`).
- Azure App Service: app settings `Holler__CloudflareTurn__KeyId` and `Holler__CloudflareTurn__ApiToken` (the token
  can be a Key Vault reference). Use `__`, which works on Windows and Linux.

## Behavior

- Credentials are cached per user until half their lifetime, then re-issued for new connections.
- Cloudflare's STUN and TURN servers are added after your `HollerOptions.IceServers`; port 53 URLs, which browsers
  block, are dropped.
- If Cloudflare can't be reached, or no key is configured, Holler logs a warning and falls back to STUN only: players
  who can connect directly still do.
- A connection that is already relaying keeps the credentials it started with, so keep `CredentialLifetime` longer
  than a session.

## Check that it works

Set `Holler:ForceRelay` to `true` (or `HollerOptions.ForceRelay`) and connect two players. Media can then only flow
through TURN, so if video connects, Cloudflare is relaying it. In Chrome or Edge, `chrome://webrtc-internals` shows
`relay` candidates and the selected candidate pair. Turn `ForceRelay` off again afterwards.

## Cost

Cloudflare bills TURN by data relayed from its edge to clients, with a monthly free allowance (1,000 GB at the time
of writing, then $0.05/GB); check Cloudflare's current pricing. Only relayed players count, and Holler sends each
viewer just the video size it draws, which keeps thumbnails cheap.

Full documentation: https://github.com/tomlm/Holler
