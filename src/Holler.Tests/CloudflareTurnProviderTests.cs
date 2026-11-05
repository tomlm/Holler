using System.Net;
using System.Text.Json;
using Holler.Cloudflare;
using Holler.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Holler.Tests;

public class CloudflareTurnProviderTests
{
    // The response documented at developers.cloudflare.com/realtime/turn/generate-credentials, plus a port-53 URL.
    private const string CloudflareResponse = """
        {
          "iceServers": [
            { "urls": ["stun:stun.cloudflare.com:3478"] },
            {
              "urls": [
                "turn:turn.cloudflare.com:3478?transport=udp",
                "turn:turn.cloudflare.com:53?transport=udp",
                "turns:turn.cloudflare.com:443?transport=tcp"
              ],
              "username": "user-abc",
              "credential": "secret-xyz"
            }
          ]
        }
        """;

    private sealed class FakeCloudflare : HttpMessageHandler, IHttpClientFactory
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Status) { Content = new StringContent(CloudflareResponse) };
        }
    }

    private FakeCloudflare _cloudflare = null!;
    private FakeTimeProvider _time = null!;
    private CloudflareTurnOptions _settings = null!;
    private CloudflareTurnProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _cloudflare = new FakeCloudflare();
        _time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        _settings = new CloudflareTurnOptions { KeyId = "key123", ApiToken = "token456", CredentialLifetime = TimeSpan.FromHours(24) };
        _provider = new CloudflareTurnProvider(_cloudflare, Options.Create(_settings), Options.Create(new HollerOptions()), _time,
            NullLogger<CloudflareTurnProvider>.Instance);
    }

    [TearDown]
    public void TearDown() => _cloudflare.Dispose();

    [Test]
    public async Task Requests_credentials_with_the_key_token_and_lifetime()
    {
        await _provider.GetIceServersAsync("alice");

        var (request, body) = _cloudflare.Requests.Single();
        Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
        Assert.That(request.RequestUri!.ToString(), Is.EqualTo("https://rtc.live.cloudflare.com/v1/turn/keys/key123/credentials/generate-ice-servers"));
        Assert.That(request.Headers.Authorization?.ToString(), Is.EqualTo("Bearer token456"));
        Assert.That(JsonDocument.Parse(body).RootElement.GetProperty("ttl").GetInt64(), Is.EqualTo(86400));
    }

    [Test]
    public async Task Returns_static_servers_plus_cloudflare_servers_without_port_53()
    {
        var servers = await _provider.GetIceServersAsync("alice");

        Assert.That(servers[0].Urls[0], Is.EqualTo("stun:stun.l.google.com:19302"), "static HollerOptions.IceServers first");
        var turn = servers.Single(s => s.Username is not null);
        Assert.That(turn.Username, Is.EqualTo("user-abc"));
        Assert.That(turn.Credential, Is.EqualTo("secret-xyz"));
        Assert.That(turn.Urls, Is.EqualTo(new[] { "turn:turn.cloudflare.com:3478?transport=udp", "turns:turn.cloudflare.com:443?transport=tcp" }));
        Assert.That(servers.SelectMany(s => s.Urls), Does.Contain("stun:stun.cloudflare.com:3478"));
    }

    [Test]
    public async Task Caches_per_user_until_half_the_lifetime()
    {
        await _provider.GetIceServersAsync("alice");
        await _provider.GetIceServersAsync("alice");
        Assert.That(_cloudflare.Requests, Has.Count.EqualTo(1), "second call served from cache");

        await _provider.GetIceServersAsync("bob");
        Assert.That(_cloudflare.Requests, Has.Count.EqualTo(2), "each user gets their own credentials");

        _time.Advance(TimeSpan.FromHours(11));
        await _provider.GetIceServersAsync("alice");
        Assert.That(_cloudflare.Requests, Has.Count.EqualTo(2), "still fresh at 11h of 24h");

        _time.Advance(TimeSpan.FromHours(1));
        await _provider.GetIceServersAsync("alice");
        Assert.That(_cloudflare.Requests, Has.Count.EqualTo(3), "refreshed at half the lifetime");
    }

    [Test]
    public async Task Lifetime_is_capped_at_cloudflares_48_hours()
    {
        _settings.CredentialLifetime = TimeSpan.FromDays(7);
        await _provider.GetIceServersAsync("alice");
        Assert.That(JsonDocument.Parse(_cloudflare.Requests.Single().Body).RootElement.GetProperty("ttl").GetInt64(), Is.EqualTo(172800));
    }

    [Test]
    public async Task Falls_back_to_static_servers_when_cloudflare_fails_and_retries_next_time()
    {
        _cloudflare.Status = HttpStatusCode.Unauthorized;
        var servers = await _provider.GetIceServersAsync("alice");
        Assert.That(servers.Single().Urls[0], Does.StartWith("stun:"), "STUN only: direct connections still work");

        _cloudflare.Status = HttpStatusCode.Created;
        servers = await _provider.GetIceServersAsync("alice");
        Assert.That(servers.Any(s => s.Username is not null), Is.True, "failures are not cached");
    }

    [Test]
    public async Task Unconfigured_key_falls_back_without_calling_cloudflare()
    {
        _settings.ApiToken = "";
        var servers = await _provider.GetIceServersAsync("alice");
        Assert.That(_cloudflare.Requests, Is.Empty);
        Assert.That(servers.Single().Username, Is.Null);
    }
}
