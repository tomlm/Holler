using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
/// Decides who may call whom and who may join which room. The default allows everything.
/// A denied call looks to the caller exactly like an offline user, so it reveals nothing.
/// </summary>
public interface IHollerAuthorizer
{
    ValueTask<bool> CanCallAsync(HollerPeerInfo caller, string calleeUserId);

    ValueTask<bool> CanJoinRoomAsync(HollerPeerInfo session, string room);
}

public sealed class AllowAllHollerAuthorizer : IHollerAuthorizer
{
    public ValueTask<bool> CanCallAsync(HollerPeerInfo caller, string calleeUserId) => ValueTask.FromResult(true);

    public ValueTask<bool> CanJoinRoomAsync(HollerPeerInfo session, string room) => ValueTask.FromResult(true);
}

/// <summary>
/// Supplies ICE servers to a user's browser. Replace for custom TURN providers;
/// they are fetched from the server, so secrets never ship in WebAssembly code.
/// </summary>
public interface IIceServerProvider
{
    ValueTask<IReadOnlyList<HollerIceServer>> GetIceServersAsync(string userId);
}

/// <summary>Returns <see cref="HollerOptions.IceServers"/>, plus fresh credentials for <see cref="HollerOptions.Turn"/> if set.</summary>
public sealed class OptionsIceServerProvider(IOptions<HollerOptions> options, TimeProvider time) : IIceServerProvider
{
    public ValueTask<IReadOnlyList<HollerIceServer>> GetIceServersAsync(string userId)
    {
        var o = options.Value;
        if (o.Turn is not { Urls.Length: > 0 } turn)
            return ValueTask.FromResult<IReadOnlyList<HollerIceServer>>(o.IceServers);
        var expires = time.GetUtcNow().Add(turn.CredentialLifetime);
        return ValueTask.FromResult<IReadOnlyList<HollerIceServer>>([.. o.IceServers, CreateTurnServer(turn, userId, expires)]);
    }

    /// <summary>
    /// TURN REST API credentials: username is "expiry-unix-seconds:userId", and the password is
    /// base64(HMAC-SHA1(secret, username)).
    /// </summary>
    public static HollerIceServer CreateTurnServer(HollerTurnOptions turn, string userId, DateTimeOffset expires)
    {
        var username = $"{expires.ToUnixTimeSeconds()}:{userId}";
        var hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(turn.SharedSecret), Encoding.UTF8.GetBytes(username));
        return new HollerIceServer(turn.Urls, username, Convert.ToBase64String(hash));
    }
}
