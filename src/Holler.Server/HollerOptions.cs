namespace Holler.Server;

public sealed class HollerOptions
{
    /// <summary>Where the signaling hub is mapped. WebAssembly clients must use the same path.</summary>
    public string HubPath { get; set; } = "/holler";

    /// <summary>STUN/TURN servers handed to browsers. Add a TURN server for users behind strict NATs.</summary>
    public List<HollerIceServer> IceServers { get; set; } = [new(["stun:stun.l.google.com:19302"])];

    /// <summary>
    /// Require an authenticated user (the default). When false, anonymous users get a per-session guest id
    /// and can only be reached by that id.
    /// </summary>
    public bool RequireAuthorization { get; set; } = true;

    /// <summary>Maximum sessions per room. Rooms are a full mesh, so cost grows with the square of this.</summary>
    public int MaxRoomSize { get; set; } = 6;
}
