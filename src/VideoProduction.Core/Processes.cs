using System.Diagnostics;
using System.Text;

namespace VideoProduction;

public static class Processes
{
    public static async Task<ProcessResult> Run(string executable, IEnumerable<string> argv, string cwd,
        CancellationToken cancellationToken, int timeoutSeconds = 120, int maxOutputBytes = 4_000_000,
        bool requireSuccess = true, string? standardInput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) {
            WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.RedirectStandardInput = standardInput is not null;
        if (start.RedirectStandardInput) start.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var argument in argv) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA",
            "APPDATA", "HOME", "LANG", "LC_ALL", "DOTNET_ROOT", "PLAYWRIGHT_BROWSERS_PATH" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null) start.Environment[name] = value;
        }
        start.Environment["NO_COLOR"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var process = new Process { StartInfo = start };
        var watch = Stopwatch.StartNew();
        var events = new List<ProcessEvent>();
        var totalBytes = 0;
        process.Start();
        using var registration = linked.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Process exited between state check and kill. */ }
        });
        async Task<string> Drain(StreamReader reader, string stream)
        {
            var result = new StringBuilder();
            var buffer = new char[2048];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), linked.Token)) > 0)
            {
                var text = new string(buffer, 0, count);
                if (Interlocked.Add(ref totalBytes, Encoding.UTF8.GetByteCount(text)) > maxOutputBytes)
                {
                    linked.Cancel();
                    throw new VideoException("process.output-limit", "Child output exceeded approved bound.");
                }
                result.Append(text);
                lock (events) events.Add(new ProcessEvent(watch.ElapsedTicks, stream, text));
            }
            return result.ToString();
        }
        var stdout = Drain(process.StandardOutput, "stdout");
        var stderr = Drain(process.StandardError, "stderr");
        var exited = process.WaitForExitStatusAsync(linked.Token);
        async Task Input()
        {
            if (standardInput is null) return;
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), linked.Token);
            process.StandardInput.Close();
        }
        try
        {
            await Task.WhenAll(stdout, stderr, Input(), exited);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new VideoException("process.timeout", $"Process exceeded {timeoutSeconds} seconds.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitStatusAsync(CancellationToken.None);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var status = await exited;
        // Retain the existing wire exit-code mapping for POSIX signal termination.
        var exitCode = status.Signal is null ? status.ExitCode : process.ExitCode;
        var output = new ProcessResult(exitCode, await stdout, await stderr,
            events.OrderBy(x => x.ElapsedTicks).ToArray(), Stopwatch.Frequency);
        if (requireSuccess && (output.ExitCode != 0 || status.Signal is not null || status.Canceled))
            throw new VideoException("process.failed", $"Process exited {output.ExitCode}{(status.Signal is { } signal ? $" (signal {signal})" : "")}: {output.Stderr[^Math.Min(3000, output.Stderr.Length)..]}");
        return output;
    }
}
