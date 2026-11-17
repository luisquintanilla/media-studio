using System.Diagnostics;
using System.Text;
using MediaStudio.Configuration;

namespace MediaStudio.Speech.Kokoro;

/// <summary>Grapheme-to-phoneme conversion producing IPA text.</summary>
public interface IPhonemizer
{
    Task<string> PhonemizeAsync(string text, string language, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs <c>espeak-ng -q --ipa=3 -v &lt;lang&gt; --stdin</c> and returns cleaned IPA. Resolution order: configured path →
/// PATH → the default Windows install location (<see cref="LocalRuntimeDetector.FindEspeakNg"/>).
/// </summary>
public sealed class EspeakPhonemizer : IPhonemizer
{
    public const string InstallHint =
        "espeak-ng is required for local Kokoro narration. Install it (Windows: winget install eSpeak-NG.eSpeak-NG; " +
        "Linux: apt install espeak-ng; macOS: brew install espeak-ng) or choose a cloud voice in Settings.";

    private readonly string? configuredPath;
    private string? resolved;

    public EspeakPhonemizer(string? executablePath = null, TimeSpan? timeout = null)
    {
        configuredPath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath;
        Timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public TimeSpan Timeout { get; }

    /// <summary>The espeak-ng executable, or throws <see cref="InvalidOperationException"/> with install guidance.</summary>
    public string ExecutablePath => resolved ??= Resolve(configuredPath)
        ?? throw new InvalidOperationException(InstallHint);

    public static string? Resolve(string? configuredPath = null) =>
        configuredPath is null ? LocalRuntimeDetector.FindEspeakNg()
        : File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;

    /// <summary>Maps a BCP-47-ish language to an espeak-ng voice; Kokoro supports American and British English.</summary>
    public static string VoiceFor(string? language) => language?.Trim().ToLowerInvariant() switch {
        null or "" or "en" or "en-us" => "en-us",
        "en-gb" or "en-uk" => "en-gb",
        var other => other
    };

    public async Task<string> PhonemizeAsync(string text, string language, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var start = new ProcessStartInfo(ExecutablePath) {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in new[] { "-q", "--ipa=3", "-v", VoiceFor(language), "--stdin" }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException($"{InstallHint} ({error.Message})", error);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            // Read stdout as raw bytes and decode UTF-8 ourselves: the console code page would garble IPA on Windows.
            var output = new MemoryStream();
            var readOut = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
            var readErr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(text.ReplaceLineEndings(" ").AsMemory(), timeout.Token).ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync().ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(readOut, readErr).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"espeak-ng exited with code {process.ExitCode}: {(await readErr).Trim()}");
            return Clean(Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw new TimeoutException($"espeak-ng did not finish within {Timeout.TotalSeconds:0} s.");
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    /// <summary>Removes espeak's tie (U+200D) and separator (<c>_</c>) marks and folds line breaks to single spaces.</summary>
    public static string Clean(string ipa)
    {
        var builder = new StringBuilder(ipa.Length);
        foreach (var c in ipa)
        {
            if (c is '\u200d' or '_') continue;
            var ch = c is '\r' or '\n' or '\t' ? ' ' : c;
            if (ch == ' ' && (builder.Length == 0 || builder[^1] == ' ')) continue;
            builder.Append(ch);
        }
        return builder.ToString().Trim();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}
