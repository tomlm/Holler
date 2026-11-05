using Microsoft.Playwright;

namespace Holler.E2E;

/// <summary>
/// Starts the Holler sample on a free port and a Chromium with fake camera/microphone devices,
/// shared by every test in the assembly.
/// </summary>
[SetUpFixture]
public sealed class SampleAppFixture
{
    public static string BaseUrl { get; private set; } = "";
    public static IBrowser Browser { get; private set; } = null!;

    /// <summary>
    /// Fake devices without the auto-accepting fake UI, so camera/mic permission really applies (tests set it with
    /// the DevTools protocol to imitate a user's Block and Allow).
    /// </summary>
    public static IBrowser BrowserWithRealPermissions { get; private set; } = null!;

    /// <summary>Short enough to test "no answer", long enough for the other tests to answer.</summary>
    public static TimeSpan RingTimeout { get; } = TimeSpan.FromSeconds(8);

    private static SampleApp? _app;
    private static IPlaywright? _playwright;

    /// <summary>For tests that need a browser launched with different settings.</summary>
    public static IPlaywright Playwright => _playwright!;
    private static string? _skipReason;

    /// <summary>Call first in each test: skips when the Playwright browser isn't installed.</summary>
    public static void RequireBrowser()
    {
        if (_skipReason is not null)
            Assert.Ignore(_skipReason);
    }

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        try
        {
            Browser = await _playwright.Chromium.LaunchAsync(new()
            {
                Args =
                [
                    "--use-fake-ui-for-media-stream",
                    "--use-fake-device-for-media-stream",
                    "--autoplay-policy=no-user-gesture-required",
                ],
            });
            BrowserWithRealPermissions = await _playwright.Chromium.LaunchAsync(new()
            {
                // Full Chromium (new headless), not the headless shell: only it applies camera/mic permissions.
                Channel = "chromium",
                Args = ["--use-fake-device-for-media-stream", "--autoplay-policy=no-user-gesture-required"],
            });
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist"))
        {
            _skipReason = "Playwright Chromium is not installed. Run: pwsh src/Holler.E2E/bin/Debug/net10.0/playwright.ps1 install chromium";
            return;
        }

        _app = await SampleApp.StartAsync(new Dictionary<string, string> { ["Holler__RingTimeout"] = RingTimeout.ToString() });
        BaseUrl = _app.BaseUrl;
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (Browser is not null)
            await Browser.CloseAsync();
        if (BrowserWithRealPermissions is not null)
            await BrowserWithRealPermissions.CloseAsync();
        _playwright?.Dispose();
        if (_app is not null)
            await _app.DisposeAsync();
    }
}
