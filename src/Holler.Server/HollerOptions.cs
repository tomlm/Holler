namespace Holler.Server;

public sealed class HollerOptions
{
    /// <summary>Where the signaling hub is mapped. WebAssembly clients must use the same path.</summary>
    public string HubPath { get; set; } = "/holler";

    /// <summary>Static STUN/TURN servers handed to browsers. For TURN with expiring credentials, use <see cref="Turn"/> instead.</summary>
    public List<HollerIceServer> IceServers { get; set; } = [new(["stun:stun.l.google.com:19302"])];

    /// <summary>
    /// A TURN server using the shared-secret "TURN REST API" scheme (coturn <c>use-auth-secret</c>).
    /// Each user gets short-lived credentials, so the secret never leaves the server.
    /// </summary>
    public HollerTurnOptions? Turn { get; set; }

    /// <summary>
    /// Require an authenticated user (the default). When false, anonymous users get a per-session guest id
    /// and can only be reached by that id.
    /// </summary>
    public bool RequireAuthorization { get; set; } = true;

    /// <summary>Maximum sessions per room. Rooms are a full mesh, so cost grows with the square of this.</summary>
    public int MaxRoomSize { get; set; } = 6;

    /// <summary>How long a call rings before the caller gets "no answer". <see cref="TimeSpan.Zero"/> rings forever.</summary>
    public TimeSpan RingTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Per-session abuse limits. Signals over a limit are dropped.</summary>
    public HollerLimits Limits { get; set; } = new();
}

public sealed class HollerTurnOptions
{
    /// <summary>TURN URLs, e.g. <c>turn:turn.example.com:3478</c> and <c>turns:turn.example.com:5349</c>.</summary>
    public string[] Urls { get; set; } = [];

    /// <summary>The coturn <c>static-auth-secret</c>.</summary>
    public string SharedSecret { get; set; } = "";

    /// <summary>How long issued credentials stay valid. Clients refresh them before starting new connections.</summary>
    public TimeSpan CredentialLifetime { get; set; } = TimeSpan.FromHours(12);
}

public sealed class HollerLimits
{
    /// <summary>Calls one session may start per minute.</summary>
    public int RingsPerMinute { get; set; } = 10;

    /// <summary>Signals one session may send per second. ICE gathering sends bursts, so keep this generous.</summary>
    public int SignalsPerSecond { get; set; } = 100;

    /// <summary>Maximum signal payload length (SDP is usually a few KB).</summary>
    public int MaxPayloadLength { get; set; } = 64 * 1024;

    /// <summary>Rooms one session may be in at once.</summary>
    public int MaxRoomsPerSession { get; set; } = 8;
}
