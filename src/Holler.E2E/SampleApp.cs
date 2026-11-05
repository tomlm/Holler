using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Holler.E2E;

/// <summary>A running copy of the Holler sample on a free port, with optional configuration overrides.</summary>
public sealed class SampleApp : IAsyncDisposable
{
    private readonly Process _process;

    private SampleApp(Process process, string baseUrl)
    {
        _process = process;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    // The sample was built alongside these tests, in the same configuration.
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    /// <param name="settings">Configuration overrides, as environment variables (e.g. <c>Holler__ForceRelay</c>).</param>
    public static async Task<SampleApp> StartAsync(IReadOnlyDictionary<string, string>? settings = null)
    {
        var baseUrl = $"http://127.0.0.1:{GetFreePort()}/";
        var project = Path.Combine(FindRepoRoot(), "src", "samples", "Holler.Sample", "Holler.Sample");
        var start = new ProcessStartInfo("dotnet", ["run", "--project", project, "-c", BuildConfiguration, "--no-build", "--no-launch-profile", "--urls", baseUrl])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            start.Environment[key] = value;

        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the sample app.");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) TestContext.Progress.WriteLine($"[app] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) TestContext.Progress.WriteLine($"[app] {e.Data}"); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var app = new SampleApp(process, baseUrl);

        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"Sample app exited with code {process.ExitCode}.");
            try
            {
                if ((await http.GetAsync(baseUrl)).IsSuccessStatusCode)
                    return app;
            }
            catch (HttpRequestException)
            {
            }
            if (DateTime.UtcNow > deadline)
            {
                await app.DisposeAsync();
                throw new TimeoutException($"Sample app did not respond at {baseUrl}.");
            }
            await Task.Delay(500);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process.Dispose();
    }

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
