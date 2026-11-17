using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using VideoProduction;

namespace MediaStudio.App;

public enum WindowCaptureMode { Graphics, Gdi }

public sealed class OwnedWindowCapture : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> output;
    private readonly Task<string> error;
    private readonly TaskCompletionSource firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string directory;
    private readonly string providerMode;
    private bool finished;
    public string RecordingPath { get; }
    public long WindowHandle { get; }

    private OwnedWindowCapture(Window window, string ffmpeg, string directory, int maximumSeconds, WindowCaptureMode mode, string providerMode)
    {
        Guard.Require(maximumSeconds is >= 1 and <= 600 && !Directory.Exists(directory),
            "capture.bounds", "Use a new owned-window evidence directory and a duration <=600 seconds.");
        this.directory = Path.GetFullPath(directory);
        this.providerMode = providerMode;
        Directory.CreateDirectory(this.directory);
        WindowHandle = new WindowInteropHelper(window).Handle.ToInt64();
        Guard.Require(WindowHandle != 0, "capture.window", "The actual shown WPF window needs its exact HWND.");
        RecordingPath = Path.Combine(this.directory, "native-window.mp4");
        ffmpeg = Files.FindExecutable(ffmpeg);
        process = new() {
            StartInfo = new(ffmpeg) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        var hwnd = unchecked((ulong)WindowHandle).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var input = mode == WindowCaptureMode.Graphics
            ? new[] { "-f", "lavfi", "-i", $"gfxcapture=hwnd={hwnd}:max_framerate=15:capture_cursor=0:display_border=0" }
            : ["-f", "gdigrab", "-framerate", "15", "-draw_mouse", "0", "-i", $"hwnd={hwnd}"];
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "info", "-n", "-progress", "pipe:1", "-stats_period", "0.5",
        }.Concat(input).Concat(new[]
        {
            "-t", maximumSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-vf", (mode == WindowCaptureMode.Graphics ? "hwdownload,format=bgra," : "")
                + "scale=1280:-2:out_color_matrix=bt709:out_range=tv,format=yuv420p,colorspace=iall=bt709:itrc=iec61966-2-1:all=bt709:irange=tv:range=tv",
            "-an", "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-threads", "1",
            "-crf", "20", "-color_range", "tv", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
            "-movflags", "+faststart", RecordingPath
        })) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "SystemDrive", "ComSpec", "PATH", "PATHEXT", "TEMP", "TMP",
            "USERPROFILE", "LOCALAPPDATA", "APPDATA", "ProgramData" })
            if (Environment.GetEnvironmentVariable(name) is { } value) process.StartInfo.Environment[name] = value;
        if (!process.Start()) throw new IOException("Could not start exact-window graphics capture.");
        output = Drain(process.StandardOutput, observeFrames: true);
        error = Drain(process.StandardError, observeFrames: false);
        Json.Write(Path.Combine(this.directory, "capture-source.json"), new
        {
            schemaVersion = 1, windowPid = Environment.ProcessId, WindowHandle, capturePid = process.Id,
            sessionId = Process.GetCurrentProcess().SessionId, Environment.UserInteractive, window.IsVisible,
            method = $"FFmpeg {(mode == WindowCaptureMode.Graphics ? "gfxcapture" : "gdigrab")} exact HWND; no monitor/title matching, desktop, pointer, microphone or system-audio capture",
            ffmpeg, ffmpegSha256 = Files.Hash(ffmpeg), maximumSeconds,
            startedUtc = DateTimeOffset.UtcNow, permission = "User-requested actual native WPF window capture"
        });
    }
    public static async Task<OwnedWindowCapture> Start(Window window, string ffmpeg, string directory, int maximumSeconds,
        WindowCaptureMode mode = WindowCaptureMode.Graphics,
        string providerMode = "Actual native UI only; no authoring model, synthesis, production or server lookup")
    {
        var capture = new OwnedWindowCapture(window, ffmpeg, directory, maximumSeconds, mode, providerMode);
        try
        {
            await capture.firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Guard.Require(!capture.process.HasExited, "capture.exited", "Window capture exited before the native interaction.");
            return capture;
        }
        catch (Exception startup)
        {
            try { await capture.DisposeAsync(); }
            catch (Exception shutdown) { throw new AggregateException("Native capture startup and cleanup both failed.", startup, shutdown); }
            throw;
        }
    }
    private async Task<string> Drain(StreamReader reader, bool observeFrames)
    {
        var text = new StringBuilder();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (text.Length + line.Length > 1_000_000)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw new IOException("Owned capture diagnostics exceeded the explicit 1MB bound.");
            }
            text.AppendLine(line);
            if (observeFrames && line.StartsWith("frame=", StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(6), out var frame) && frame > 0) firstFrame.TrySetResult();
        }
        if (observeFrames && !firstFrame.Task.IsCompleted)
            firstFrame.TrySetException(new IOException("Exact-window capture produced no frame; inspect retained FFmpeg diagnostics."));
        return text.ToString();
    }
    public async ValueTask DisposeAsync()
    {
        if (finished) return;
        finished = true;
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("q");
                await process.StandardInput.FlushAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                catch (TimeoutException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    throw new IOException("Owned-window capture failed to finalize within its bound.");
                }
            }
            Guard.Require(process.ExitCode == 0 && File.Exists(RecordingPath) && new FileInfo(RecordingPath).Length > 0,
                "capture.failed", "Exact-window capture failed; no verified recording is claimed.");
            Json.Write(Path.Combine(directory, "capture-result.json"), new
            {
                schemaVersion = 1, path = RecordingPath, sha256 = Files.Hash(RecordingPath), WindowHandle,
                exitCode = process.ExitCode, finalizedUtc = DateTimeOffset.UtcNow,
                providerMode,
                interpretation = "Actual native window capture; codec/decoded-frame inspection is separate and still required."
            });
        }
        finally
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "capture-progress.txt"), await output);
                await File.WriteAllTextAsync(Path.Combine(directory, "capture-stderr.txt"), await error);
            }
            finally { process.Dispose(); }
        }
    }
}
