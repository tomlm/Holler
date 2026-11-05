using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;

namespace Holler.E2E;

/// <summary>
/// Relay-only mode (HollerOptions.ForceRelay): every connection must go through a TURN server.
/// The Cloudflare test needs a real TURN key in this project's user secrets:
/// <code>
/// dotnet user-secrets set "Holler:CloudflareTurn:KeyId" "..." --project src/Holler.E2E
/// dotnet user-secrets set "Holler:CloudflareTurn:ApiToken" "..." --project src/Holler.E2E
/// </code>
/// </summary>
[Category("E2E")]
public class RelayTests
{
    private const string RemoteVideos = "[...document.querySelectorAll('.holler-tile:not(.holler-tile-local) video')]";

    private readonly List<IBrowserContext> _contexts = [];

    [SetUp]
    public void SetUp() => SampleAppFixture.RequireBrowser();

    [TearDown]
    public async Task TearDown()
    {
        foreach (var context in _contexts)
            await context.CloseAsync();
        _contexts.Clear();
    }

    [Test]
    public async Task Relay_only_without_a_turn_server_never_connects()
    {
        // Proves the relay-only policy reaches the browser: with nothing to relay through, there is no route.
        await using var app = await SampleApp.StartAsync(new Dictionary<string, string> { ["Holler__ForceRelay"] = "true" });
        var (a, b) = await JoinSameRoomAsync(app);

        await Task.Delay(TimeSpan.FromSeconds(12));
        foreach (var page in new[] { a, b })
            Assert.That(await page.EvaluateAsync<bool>($"() => {RemoteVideos}.some(v => v.videoWidth > 0)"), Is.False);
    }

    [Test]
    public async Task Relay_only_through_cloudflare_turn_connects()
    {
        var secrets = new ConfigurationBuilder().AddUserSecrets<RelayTests>().Build();
        var keyId = secrets["Holler:CloudflareTurn:KeyId"];
        var apiToken = secrets["Holler:CloudflareTurn:ApiToken"];
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(apiToken))
            Assert.Ignore("No Cloudflare TURN key: set Holler:CloudflareTurn:KeyId and ApiToken in the Holler.E2E user secrets.");

        await using var app = await SampleApp.StartAsync(new Dictionary<string, string>
        {
            ["Holler__ForceRelay"] = "true",
            ["Holler__CloudflareTurn__KeyId"] = keyId,
            ["Holler__CloudflareTurn__ApiToken"] = apiToken,
        });
        var (a, b) = await JoinSameRoomAsync(app);

        // Relay-only means any connection that works went through Cloudflare's TURN servers.
        foreach (var page in new[] { a, b })
            await page.WaitForFunctionAsync($"() => {RemoteVideos}.some(v => v.videoWidth > 0)", null, new() { Timeout = 30_000 });
    }

    private async Task<(IPage A, IPage B)> JoinSameRoomAsync(SampleApp app)
    {
        var room = "relay-" + Guid.NewGuid().ToString("N")[..8];
        var a = await SignInAsync(app, "ray");
        var b = await SignInAsync(app, "sol");
        await a.GotoAsync($"{app.BaseUrl}room/server?room={room}");
        await b.GotoAsync($"{app.BaseUrl}room/wasm?room={room}");
        // Both are in the room (each sees the other's tile) before we judge the media path.
        foreach (var page in new[] { a, b })
            await page.Locator(".holler-tile:not(.holler-tile-local)").WaitForAsync(new() { Timeout = 30_000 });
        return (a, b);
    }

    private async Task<IPage> SignInAsync(SampleApp app, string name)
    {
        name = $"{name}-{Guid.NewGuid().ToString("N")[..6]}";
        var context = await SampleAppFixture.Browser.NewContextAsync(new() { Permissions = ["camera", "microphone"] });
        _contexts.Add(context);
        var page = await context.NewPageAsync();
        await page.GotoAsync(app.BaseUrl);
        await page.GetByPlaceholder("Pick a name").FillAsync(name);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.GetByText($"Signed in as {name}").WaitForAsync();
        return page;
    }
}
