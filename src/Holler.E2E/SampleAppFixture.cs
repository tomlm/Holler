using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

    private static Process? _app;
    private static IPlaywright? _playwright;
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
        _playwright = await Playwright.CreateAsync();
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

        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        var project = Path.Combine(FindRepoRoot(), "src", "samples", "Holler.Sample", "Holler.Sample");
        var start = new ProcessStartInfo("dotnet", ["run", "--project", project, "-c", BuildConfiguration, "--no-build", "--no-launch-profile", "--urls", BaseUrl])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["Holler__RingTimeout"] = RingTimeout.ToString();
        _app = Process.Start(start) ?? throw new InvalidOperationException("Could not start the sample app.");
        _app.OutputDataReceived += (_, e) => { if (e.Data is not null) TestContext.Progress.WriteLine($"[app] {e.Data}"); };
        _app.ErrorDataReceived += (_, e) => { if (e.Data is not null) TestContext.Progress.WriteLine($"[app] {e.Data}"); };
        _app.BeginOutputReadLine();
        _app.BeginErrorReadLine();

        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            if (_app.HasExited)
                throw new InvalidOperationException($"Sample app exited with code {_app.ExitCode}.");
            try
            {
                if ((await http.GetAsync(BaseUrl)).IsSuccessStatusCode)
                    break;
            }
            catch (HttpRequestException)
            {
            }
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Sample app did not respond at {BaseUrl}.");
            await Task.Delay(500);
        }
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        if (Browser is not null)
            await Browser.CloseAsync();
        if (BrowserWithRealPermissions is not null)
            await BrowserWithRealPermissions.CloseAsync();
        _playwright?.Dispose();
        if (_app is { HasExited: false })
        {
            _app.Kill(entireProcessTree: true);
            await _app.WaitForExitAsync();
        }
        _app?.Dispose();
    }

    // The sample was built alongside these tests, in the same configuration.
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Holler.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not find the repo root (src/Holler.slnx).");
    }
}
