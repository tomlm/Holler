using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Holler.Server;

/// <summary>Short-lived TURN credentials from a provider, and when they stop working.</summary>
public sealed record TurnCredentials(IReadOnlyList<HollerIceServer> IceServers, DateTimeOffset ExpiresAt);

/// <summary>
/// Base for TURN services that issue short-lived credentials over an API (Cloudflare, Twilio, Metered, ...).
/// A provider implements only <see cref="FetchCredentialsAsync"/>; this class caches each user's credentials until
/// half their lifetime has passed, adds the static <see cref="HollerOptions.IceServers"/>, drops URLs browsers
/// can't use, and falls back to the static servers (direct connections only) if the service can't be reached.
/// </summary>
public abstract partial class TurnCredentialProvider(
    IOptions<HollerOptions> options,
    TimeProvider time,
    ILogger logger) : IIceServerProvider
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset RefreshAfter, IReadOnlyList<HollerIceServer> Servers)> _cache = new();

    /// <summary>The clock, for computing credential expiry.</summary>
    protected TimeProvider Time { get; } = time;

    /// <summary>Gets fresh credentials for <paramref name="userId"/> from the TURN service.</summary>
    protected abstract Task<TurnCredentials> FetchCredentialsAsync(string userId, CancellationToken cancellationToken);

    public async ValueTask<IReadOnlyList<HollerIceServer>> GetIceServersAsync(string userId)
    {
        var now = Time.GetUtcNow();
        if (_cache.TryGetValue(userId, out var cached) && now < cached.RefreshAfter)
            return cached.Servers;

        var fallback = options.Value.IceServers;
        TurnCredentials credentials;
        try
        {
            credentials = await FetchCredentialsAsync(userId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Players on open networks still connect directly; only those who need a relay go without video.
            logger.LogWarning(ex, "Could not get TURN credentials from {Provider}; using STUN only", GetType().Name);
            return fallback;
        }

        var servers = fallback.Concat(credentials.IceServers.Select(UsableInBrowsers).OfType<HollerIceServer>()).ToArray();
        var lifetime = credentials.ExpiresAt - now;
        _cache[userId] = (now + lifetime / 2, servers);
        PruneExpired(now);
        return servers;
    }

    // Browsers block port 53, so such URLs only time out (Cloudflare documents this for its alternate port).
    private static HollerIceServer? UsableInBrowsers(HollerIceServer server)
    {
        var urls = server.Urls.Where(url => !Port53().IsMatch(url)).ToArray();
        return urls.Length == 0 ? null : server with { Urls = urls };
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var (userId, entry) in _cache)
        {
            if (now >= entry.RefreshAfter)
                _cache.TryRemove(userId, out _);
        }
    }

    [GeneratedRegex(@":53(\?|$)")]
    private static partial Regex Port53();
}
