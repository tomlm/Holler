using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace Holler.Server;

/// <summary>Maps an authenticated principal to the user id other people call. Replace to use your own identity scheme.</summary>
public interface IHollerUserIdProvider
{
    /// <summary>Returns the user id, or null if the principal is anonymous.</summary>
    string? GetUserId(ClaimsPrincipal user);
}

/// <summary>Uses the <see cref="ClaimTypes.NameIdentifier"/> claim, falling back to the identity name.</summary>
public sealed class ClaimsHollerUserIdProvider : IHollerUserIdProvider
{
    public string? GetUserId(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
            ? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity.Name
            : null;
}

/// <summary>
/// Supplies ICE servers to a user's browser. Replace to issue short-lived TURN credentials;
/// they are fetched from the server, so secrets never ship in WebAssembly code.
/// </summary>
public interface IIceServerProvider
{
    ValueTask<IReadOnlyList<HollerIceServer>> GetIceServersAsync(string userId);
}

/// <summary>Returns <see cref="HollerOptions.IceServers"/>.</summary>
public sealed class OptionsIceServerProvider(IOptions<HollerOptions> options) : IIceServerProvider
{
    public ValueTask<IReadOnlyList<HollerIceServer>> GetIceServersAsync(string userId) =>
        ValueTask.FromResult<IReadOnlyList<HollerIceServer>>(options.Value.IceServers);
}
