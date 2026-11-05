using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Holler.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Holler.Cloudflare;

public sealed class CloudflareTurnOptions
{
    /// <summary>The TURN key's id ("Turn Token ID" in the dashboard).</summary>
    public string KeyId { get; set; } = "";

    /// <summary>The TURN key's API token. A long-term secret: keep it in user secrets, app settings or Key Vault.</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>
    /// How long issued credentials last (Cloudflare allows up to 48 hours). A relayed connection drops once its
    /// credentials expire, so make this longer than a session; Holler re-fetches well before expiry for new ones.
    /// </summary>
    public TimeSpan CredentialLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>The Cloudflare Realtime TURN API.</summary>
    public Uri Endpoint { get; set; } = new("https://rtc.live.cloudflare.com/v1/turn/keys/");
}

/// <summary>
/// Issues per-user TURN credentials from Cloudflare Realtime TURN
/// (<c>POST /v1/turn/keys/{keyId}/credentials/generate-ice-servers</c>).
/// </summary>
public sealed class CloudflareTurnProvider(
    IHttpClientFactory httpClients,
    IOptions<CloudflareTurnOptions> cloudflare,
    IOptions<HollerOptions> options,
    TimeProvider time,
    ILogger<CloudflareTurnProvider> logger) : TurnCredentialProvider(options, time, logger)
{
    public const string HttpClientName = "Holler.Cloudflare";

    private static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(48);

    protected override async Task<TurnCredentials> FetchCredentialsAsync(string userId, CancellationToken cancellationToken)
    {
        var settings = cloudflare.Value;
        if (string.IsNullOrWhiteSpace(settings.KeyId) || string.IsNullOrWhiteSpace(settings.ApiToken))
            throw new InvalidOperationException("Cloudflare TURN is not configured: set Holler:CloudflareTurn:KeyId and ApiToken.");

        var lifetime = settings.CredentialLifetime > MaxLifetime ? MaxLifetime : settings.CredentialLifetime;
        var issuedAt = Time.GetUtcNow();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(settings.Endpoint, $"{Uri.EscapeDataString(settings.KeyId)}/credentials/generate-ice-servers"))
        {
            Content = JsonContent.Create(new { ttl = (long)lifetime.TotalSeconds }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiToken);

        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Cloudflare TURN returned {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return new TurnCredentials(ParseIceServers(json.RootElement), issuedAt + lifetime);
    }

    // { "iceServers": [ { "urls": [..] }, { "urls": [..], "username": "..", "credential": ".." } ] }
    // Tolerates "iceServers" as a single object and "urls" as a single string, as in the TURN REST draft.
    private static List<HollerIceServer> ParseIceServers(JsonElement root)
    {
        var servers = new List<HollerIceServer>();
        if (!root.TryGetProperty("iceServers", out var iceServers))
            return servers;
        IEnumerable<JsonElement> entries = iceServers.ValueKind == JsonValueKind.Array ? iceServers.EnumerateArray() : [iceServers];
        foreach (var entry in entries)
        {
            if (!entry.TryGetProperty("urls", out var urls))
                continue;
            string[] list = urls.ValueKind == JsonValueKind.Array
                ? urls.EnumerateArray().Select(url => url.GetString()).OfType<string>().ToArray()
                : urls.GetString() is { } single ? [single] : [];
            if (list.Length == 0)
                continue;
            servers.Add(new HollerIceServer(list,
                entry.TryGetProperty("username", out var username) ? username.GetString() : null,
                entry.TryGetProperty("credential", out var credential) ? credential.GetString() : null));
        }
        return servers;
    }
}
