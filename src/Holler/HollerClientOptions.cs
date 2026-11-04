using Microsoft.AspNetCore.SignalR.Client;

namespace Holler;

public sealed class HollerClientOptions
{
    /// <summary>Hub path relative to the app base URI. Must match the server's <c>HollerOptions.HubPath</c>.</summary>
    public string HubPath { get; set; } = "holler";

    /// <summary>Hook to customize the hub connection, e.g. access tokens or transports.</summary>
    public Action<IHubConnectionBuilder>? ConfigureConnection { get; set; }
}
