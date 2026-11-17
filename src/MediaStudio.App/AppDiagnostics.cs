using System.IO;
using MediaStudio.Configuration;
using Microsoft.Web.WebView2.Core;
using VideoProduction;

namespace MediaStudio.App;

/// <summary>
/// <c>MediaStudio.exe --diagnostics [name] [args]</c>: troubleshooting checks a user can run and paste into an issue.
/// Window checks use a throwaway state root, never start production and never call a model.
/// </summary>
internal static class AppDiagnostics
{
    private sealed record Entry(string Name, string Usage, string Description, Func<string[], TextWriter, int> Run);

    private static readonly Entry[] Entries =
    [
        new("summary", "summary", "Versions (app, .NET, WebView2, tools), settings summary, local runtimes and model assets.", Summary),
        new("list", "list", "Lists the available diagnostics.", List),
        new("window", "window [<output-dir>]", "Opens the app window on a throwaway state and navigates all four views; saves a screenshot.", Window),
        new("window-capture", "window-capture <output-dir> [--gdi]", "Records a 5-second FFmpeg capture of the app window to check screen recording.", WindowCapture),
        new("local-whisper", "local-whisper <16kHz-mono.wav> [<voice-config.json>]", "Transcribes a WAV with the bundled Whisper.net runtime and the whisper-base model.", LocalWhisperCheck),
    ];

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var name = args.Length == 0 ? "summary" : args[0];
        var entry = Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            error.WriteLine($"Unknown diagnostic '{name}'. Run MediaStudio.exe --diagnostics list.");
            return 2;
        }
        try { return entry.Run(args.Skip(1).ToArray(), output); }
        catch (Exception failure)
        {
            error.WriteLine($"FAIL {entry.Name}: {failure.Message}");
            return 1;
        }
    }

    private static int List(string[] args, TextWriter output)
    {
        output.WriteLine("Usage: MediaStudio.exe --diagnostics <name> [args]");
        foreach (var entry in Entries) output.WriteLine($"  {entry.Usage,-56} {entry.Description}");
        return 0;
    }

    private static int Summary(string[] args, TextWriter output)
    {
        string webView;
        try { webView = CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "not installed"; }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or DllNotFoundException or BadImageFormatException) { webView = "not installed"; }
        return Diagnostics.WriteAsync(output, [new("WebView2", webView)]).GetAwaiter().GetResult();
    }

    private static int Window(string[] args, TextWriter output)
    {
        var evidence = Path.GetFullPath(args.Length > 0 ? args[0]
            : Path.Combine(MediaStudioPaths.Root, "diagnostics", "window-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(evidence);
        return WithThrowawayState(root => MediaApplication.Run(new HybridOptions(root, null), new WindowDiagnostic(null, async (core, _, workspace, _) =>
        {
            await WaitAsync(core, "document.querySelector('#media-create') !== null");
            foreach (var view in new[] { "review", "jobs", "projects", "create" })
            {
                await core.ExecuteScriptAsync($"document.querySelector('#nav-{view}').click()");
                await WaitAsync(core, $"document.querySelector('#media-{view}') !== null");
            }
            Guard.Require(workspace.Projects.List().Length == 0 && workspace.Status is null,
                "diagnostics.window", "Navigation alone must not create projects or start production.");
            await using (var image = File.Create(Path.Combine(evidence, "four-view-native.png")))
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            Json.Write(Path.Combine(evidence, "result.json"), new
            {
                schemaVersion = 1, nativeWpfBlazorWebView = true, fourViewsNavigated = true,
                productionStarted = false, liveInference = false, webView2 = core.Environment.BrowserVersionString
            });
            output.WriteLine("PASS window: four views navigated; no production or inference. Output: " + evidence);
        })));
    }

    private static int WindowCapture(string[] args, TextWriter output)
    {
        if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--gdi"))
            throw new ArgumentException("Usage: --diagnostics window-capture <output-dir> [--gdi]");
        var evidence = Path.GetFullPath(args[0]);
        var mode = args.Length == 2 ? WindowCaptureMode.Gdi : WindowCaptureMode.Graphics;
        const string label = "Media Studio window capture check - no inference or production";
        return WithThrowawayState(root => MediaApplication.Run(new HybridOptions(root, null), new WindowDiagnostic(label, async (core, window, workspace, banner) =>
        {
            await WaitAsync(core, "document.querySelector('#media-create') !== null");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var repaint = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            repaint.Tick += (_, _) => banner.Text = $"{label} - {clock.Elapsed.TotalSeconds:F1}s";
            repaint.Start();
            try
            {
                await Task.Delay(500);
                await using var capture = await OwnedWindowCapture.Start(window, "ffmpeg", evidence, 5, mode);
                await Task.Delay(2000);
                await core.ExecuteScriptAsync("document.querySelector('#nav-review').click()");
                await WaitAsync(core, "document.querySelector('#media-review') !== null");
                await Task.Delay(1500);
                Guard.Require(workspace.Status is null && workspace.Projects.List().Length == 0,
                    "diagnostics.capture", "The capture check must not start production or inference.");
            }
            finally { repaint.Stop(); }
            output.WriteLine("PASS window-capture: recorded the app window. Output: " + evidence);
        })));
    }

    private static int LocalWhisperCheck(string[] args, TextWriter output)
    {
        if (args.Length is < 1 or > 2) throw new ArgumentException("Usage: --diagnostics local-whisper <16kHz-mono.wav> [<voice-config.json>]");
        var wave = Path.GetFullPath(args[0]);
        var whisper = args.Length == 2
            ? new LocalWhisper(Path.GetFullPath(args[1]))
            : new LocalWhisper(new NativeWhisperConfiguration(1,
                Path.Combine(new ModelAssets(ModelManifest.LoadDefault()).RequirePath("whisper-base"), "ggml-base.bin"),
                AppContext.BaseDirectory));
        whisper.VerifyAssets();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var text = whisper.RecognizeAsync(wave, timeout.Token).GetAwaiter().GetResult();
        output.WriteLine("PASS local-whisper: " + text.Trim());
        return 0;
    }

    private static int WithThrowawayState(Func<string, int> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "MediaStudio-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { return run(root); }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task WaitAsync(CoreWebView2 core, string expression)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await core.ExecuteScriptAsync(expression) != "true")
            await Task.Delay(75, timeout.Token);
    }
}
