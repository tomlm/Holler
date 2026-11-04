using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Holler.E2E;

/// <summary>Drives the sample with real WebRTC between several signed-in browser contexts.</summary>
[Category("E2E")]
public class SampleTests
{
    private readonly List<IBrowserContext> _contexts = [];

    private static string Url(string path) => SampleAppFixture.BaseUrl + path;

    [SetUp]
    public void SetUp() => SampleAppFixture.RequireBrowser();

    [TearDown]
    public async Task TearDown()
    {
        foreach (var context in _contexts)
            await context.CloseAsync();
        _contexts.Clear();
    }

    /// <summary>A fresh browser context signed in as <paramref name="name"/> (unique per test run).</summary>
    private async Task<(string Name, IPage Page)> SignInAsync(string name)
    {
        name = $"{name}-{Guid.NewGuid().ToString("N")[..6]}";
        var context = await SampleAppFixture.Browser.NewContextAsync(new() { Permissions = ["camera", "microphone"] });
        _contexts.Add(context);
        var page = await context.NewPageAsync();
        await page.GotoAsync(Url(""));
        await page.GetByPlaceholder("Pick a name").FillAsync(name);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await page.GetByText($"Signed in as {name}").WaitForAsync();
        return (name, page);
    }

    private static Task WaitForStatusAsync(IPage page, string text) =>
        page.Locator(".holler-status", new() { HasText = text }).WaitForAsync(new() { Timeout = 20_000 });

    private static Task WaitForRemoteVideoAsync(IPage page) =>
        page.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('.holler-stage > video.holler-video')].some(v => v.videoWidth > 0 && !v.paused)",
            null, new() { Timeout = 20_000 });

    private static async Task SendChatAsync(IPage page, string text)
    {
        await page.GetByLabel("Message").FillAsync(text);
        await page.GetByRole(AriaRole.Button, new() { Name = "Send" }).ClickAsync();
    }

    private static Task WaitForChatAsync(IPage page, string text) =>
        page.Locator(".holler-chat-text", new() { HasText = text }).WaitForAsync(new() { Timeout = 10_000 });

    private static async Task RingAsync(IPage caller, string calleeName)
    {
        await caller.GetByRole(AriaRole.Button, new() { Name = calleeName, Exact = true }).ClickAsync(new() { Timeout = 30_000 });
        await caller.GetByRole(AriaRole.Button, new() { Name = $"Call {calleeName}" }).ClickAsync();
    }

    private static ILocator IncomingButton(IPage page, string name) =>
        page.GetByRole(AriaRole.Alertdialog).GetByRole(AriaRole.Button, new() { Name = name });

    [TestCase("server", "server")]
    [TestCase("wasm", "wasm")]
    [TestCase("server", "wasm")]
    [TestCase("wasm", "server")]
    public async Task Call_connects_video_and_chat_across_render_modes(string callerMode, string calleeMode)
    {
        var a = await SignInAsync("alice");
        var b = await SignInAsync("bob");
        await a.Page.GotoAsync(Url($"call/{callerMode}"));
        await b.Page.GotoAsync(Url($"call/{calleeMode}"));

        await RingAsync(a.Page, b.Name);
        await IncomingButton(b.Page, "Accept").ClickAsync(new() { Timeout = 15_000 });

        await WaitForStatusAsync(a.Page, "Connected");
        await WaitForStatusAsync(b.Page, "Connected");
        await WaitForRemoteVideoAsync(a.Page);
        await WaitForRemoteVideoAsync(b.Page);

        await SendChatAsync(a.Page, "hello from a");
        await WaitForChatAsync(b.Page, "hello from a");
        await SendChatAsync(b.Page, "hi back");
        await WaitForChatAsync(a.Page, "hi back");

        await a.Page.GetByRole(AriaRole.Button, new() { Name = "Mute", Exact = true }).ClickAsync();
        await a.Page.GetByRole(AriaRole.Button, new() { Name = "Unmute", Exact = true }).WaitForAsync();

        await a.Page.GetByRole(AriaRole.Button, new() { Name = "Hang up" }).ClickAsync();
        await WaitForStatusAsync(b.Page, "Remote hung up");
    }

    [Test]
    public async Task Declined_call_tells_the_caller()
    {
        var a = await SignInAsync("dana");
        var b = await SignInAsync("eli");
        await a.Page.GotoAsync(Url("call/server"));
        await b.Page.GotoAsync(Url("call/wasm"));

        await RingAsync(a.Page, b.Name);
        await IncomingButton(b.Page, "Decline").ClickAsync(new() { Timeout = 15_000 });

        await WaitForStatusAsync(a.Page, "Declined");
    }

    [Test]
    public async Task Unanswered_call_times_out()
    {
        var a = await SignInAsync("hal");
        var b = await SignInAsync("ida");
        await a.Page.GotoAsync(Url("call/wasm"));
        await b.Page.GotoAsync(Url("call/server"));

        await RingAsync(a.Page, b.Name);
        await IncomingButton(b.Page, "Accept").WaitForAsync(new() { Timeout = 15_000 });

        var wait = SampleAppFixture.RingTimeout + TimeSpan.FromSeconds(10);
        await a.Page.Locator(".holler-status", new() { HasText = "No answer" }).WaitForAsync(new() { Timeout = (float)wait.TotalMilliseconds });
        await IncomingButton(b.Page, "Accept").WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10_000 });
    }

    [Test]
    public async Task Answering_on_one_device_stops_the_other_ringing()
    {
        var a = await SignInAsync("fay");
        var b1 = await SignInAsync("gus");
        // Second tab for the same user, sharing the sign-in cookie.
        var b2 = await b1.Page.Context.NewPageAsync();
        await a.Page.GotoAsync(Url("call/server"));
        await b1.Page.GotoAsync(Url("call/server"));
        await b2.GotoAsync(Url("call/wasm"));
        await b2.GetByText(Regex.Escape(a.Name)).First.WaitForAsync(new() { Timeout = 30_000 });

        await RingAsync(a.Page, b1.Name);
        await IncomingButton(b1.Page, "Accept").WaitForAsync(new() { Timeout = 15_000 });
        await IncomingButton(b2, "Accept").ClickAsync(new() { Timeout = 15_000 });

        await WaitForStatusAsync(a.Page, "Connected");
        await IncomingButton(b1.Page, "Accept").WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 10_000 });
    }

    [Test]
    public async Task Room_meshes_every_render_mode_with_chat_and_leave()
    {
        var users = new List<IPage>();
        foreach (var mode in new[] { "server", "wasm", "auto" })
        {
            var u = await SignInAsync($"room-{mode}");
            await u.Page.GotoAsync(Url($"room/{mode}"));
            users.Add(u.Page);
        }

        foreach (var page in users)
        {
            await page.WaitForFunctionAsync(
                "() => { const v = [...document.querySelectorAll('.holler-tile:not(.holler-tile-local) video')]; return v.length === 2 && v.every(x => x.videoWidth > 0); }",
                null, new() { Timeout = 30_000 });
        }

        await SendChatAsync(users[2], "room hello");
        await WaitForChatAsync(users[0], "room hello");
        await WaitForChatAsync(users[1], "room hello");

        await users[0].GetByRole(AriaRole.Button, new() { Name = "Leave" }).ClickAsync();
        foreach (var page in users.Skip(1))
        {
            await page.WaitForFunctionAsync(
                "() => document.querySelectorAll('.holler-tile:not(.holler-tile-local)').length === 1",
                null, new() { Timeout = 10_000 });
        }
    }
}
