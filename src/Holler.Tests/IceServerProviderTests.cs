using Holler.Server;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Holler.Tests;

public class IceServerProviderTests
{
    [Test]
    public void Turn_credentials_follow_the_turn_rest_api_scheme()
    {
        var turn = new HollerTurnOptions { Urls = ["turn:turn.example.com:3478"], SharedSecret = "s3cret" };

        var server = OptionsIceServerProvider.CreateTurnServer(turn, "alice", DateTimeOffset.FromUnixTimeSeconds(1700000000));

        Assert.That(server.Username, Is.EqualTo("1700000000:alice"));
        // Independently computed: base64(HMAC-SHA1("s3cret", "1700000000:alice")).
        Assert.That(server.Credential, Is.EqualTo("TtElzSjT0GdnTQ9xdcRNxx96yQs="));
        Assert.That(server.Urls, Is.EqualTo(new[] { "turn:turn.example.com:3478" }));
    }

    [Test]
    public async Task Provider_appends_turn_with_expiry_after_static_servers()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_000_000));
        var options = new HollerOptions
        {
            Turn = new() { Urls = ["turn:t"], SharedSecret = "x", CredentialLifetime = TimeSpan.FromHours(1) },
        };
        var provider = new OptionsIceServerProvider(Options.Create(options), time);

        var servers = await provider.GetIceServersAsync("bob");

        Assert.That(servers, Has.Count.EqualTo(2));
        Assert.That(servers[0].Urls[0], Does.StartWith("stun:"));
        Assert.That(servers[1].Username, Is.EqualTo($"{1_000_000 + 3600}:bob"));
    }

    [Test]
    public async Task Without_turn_only_static_servers_are_returned()
    {
        var provider = new OptionsIceServerProvider(Options.Create(new HollerOptions()), TimeProvider.System);
        var servers = await provider.GetIceServersAsync("bob");
        Assert.That(servers.Single().Username, Is.Null);
    }
}
