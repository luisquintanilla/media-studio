using System.Diagnostics;
using System.Text.Json;

namespace MediaStudio.Configuration;

public sealed record HardwareProfile(long TotalRamBytes, long? GpuVramBytes, string? GpuName = null)
{
    public double RamGb => TotalRamBytes / (1024d * 1024 * 1024);
    public double? VramGb => GpuVramBytes / (1024d * 1024 * 1024);

    /// <summary>Best-effort probe: RAM from the GC, VRAM from nvidia-smi then (Windows) the display adapter registry key.</summary>
    public static HardwareProfile Detect()
    {
        var ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var (vram, name) = ProbeNvidiaSmi();
        if (vram is null && OperatingSystem.IsWindows()) (vram, name) = ProbeWindowsRegistry();
        return new(ram, vram, name);
    }

    private static (long?, string?) ProbeNvidiaSmi()
    {
        var output = LocalRuntimeDetector.RunTool("nvidia-smi", "--query-gpu=memory.total,name --format=csv,noheader,nounits", TimeSpan.FromSeconds(3));
        if (output is null) return (null, null);
        long? best = null;
        string? bestName = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',', 2, StringSplitOptions.TrimEntries);
            if (long.TryParse(parts[0], out var mib) && (best is null || mib * 1024 * 1024 > best))
            {
                best = mib * 1024 * 1024;
                bestName = parts.Length > 1 ? parts[1] : null;
            }
        }
        return (best, bestName);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (long?, string?) ProbeWindowsRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (key is null) return (null, null);
            long? best = null;
            string? bestName = null;
            foreach (var sub in key.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var adapter = key.OpenSubKey(sub);
                if (adapter?.GetValue("HardwareInformation.qwMemorySize") is long bytes && (best is null || bytes > best))
                {
                    best = bytes;
                    bestName = adapter.GetValue("DriverDesc") as string;
                }
            }
            return (best, bestName);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (null, null);
        }
    }
}

/// <summary>Maps hardware to the §2.2 tier table. XL is never auto-selected without opt-in.</summary>
public static class TierSelector
{
    private const double Gb = 1024d * 1024 * 1024;

    public static HardwareTier Select(HardwareProfile hardware, bool allowXl = false)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        var ram = hardware.TotalRamBytes / Gb;
        var vram = (hardware.GpuVramBytes ?? 0) / Gb;
        // Reported totals are slightly below the marketing size (e.g. 15.8 GB for 16 GB), so allow ~5 % slack.
        if (allowXl && vram >= 24 * 0.95) return HardwareTier.XL;
        if (ram >= 32 * 0.95 && vram >= 12 * 0.95) return HardwareTier.L;
        if (ram >= 16 * 0.95 || vram >= 8 * 0.95) return HardwareTier.M;
        return HardwareTier.S;
    }
}

public sealed record RuntimeProbe(string Name, Uri Endpoint, bool Available, string[] Models, string? Detail = null)
{
    public bool HasModel(string tag) => Models.Any(m => TagMatches(m, tag));

    /// <summary>Ollama reports "embeddinggemma:latest" for a pull of "embeddinggemma".</summary>
    public static bool TagMatches(string installed, string wanted) =>
        string.Equals(installed, wanted, StringComparison.OrdinalIgnoreCase) ||
        (!wanted.Contains(':') && string.Equals(installed, wanted + ":latest", StringComparison.OrdinalIgnoreCase));
}

public sealed record LocalRuntimeReport(
    RuntimeProbe Ollama,
    RuntimeProbe? LlamaApp,
    bool FoundryLocalInstalled,
    string? EspeakNgPath,
    HardwareProfile Hardware,
    HardwareTier RecommendedTier)
{
    public string[] MissingOllamaTags(OllamaTier tier) => tier.Tags.Where(t => !Ollama.HasModel(t)).ToArray();
}

/// <summary>Detects local inference runtimes (Ollama, Llama.app, Foundry Local), espeak-ng and hardware. Never throws for an absent runtime.</summary>
public sealed class LocalRuntimeDetector(HttpClient? httpClient = null)
{
    public static readonly Uri OllamaEndpoint = new("http://localhost:11434/v1/");
    public static readonly Uri LlamaAppEndpoint = new("http://localhost:9931/v1/");
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1.5);
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    public async Task<LocalRuntimeReport> DetectAsync(bool allowXl = false, CancellationToken cancellationToken = default)
    {
        var ollama = ProbeAsync("Ollama", OllamaEndpoint, cancellationToken);
        var llama = ProbeAsync("Llama.app", LlamaAppEndpoint, cancellationToken);
        var hardware = await Task.Run(HardwareProfile.Detect, cancellationToken).ConfigureAwait(false);
        var foundryLocal = await Task.Run(() => RunTool("foundry", "--version", TimeSpan.FromSeconds(3)) is not null, cancellationToken).ConfigureAwait(false);
        var llamaResult = await llama.ConfigureAwait(false);
        return new(await ollama.ConfigureAwait(false), llamaResult.Available ? llamaResult : null, foundryLocal, FindEspeakNg(), hardware,
            TierSelector.Select(hardware, allowXl));
    }

    /// <summary>GET <c>{endpoint}models</c> on an OpenAI-compatible server and lists model ids.</summary>
    public async Task<RuntimeProbe> ProbeAsync(string name, Uri endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var baseUri = endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var response = await _http.GetAsync(new Uri(baseUri, "models"), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(name, baseUri, false, [], $"HTTP {(int)response.StatusCode}");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            var models = document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray()
                    .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
                    .OfType<string>().ToArray()
                : [];
            return new(name, baseUri, true, models);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(name, baseUri, false, [], "not responding");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return new(name, baseUri, false, [], "not running");
        }
    }

    public static string? FindEspeakNg()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "espeak-ng.exe" } : ["espeak-ng"];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var installed = Path.Combine(programFiles, "eSpeak NG", "espeak-ng.exe");
            if (File.Exists(installed)) return installed;
        }
        return null;
    }

    internal static string? RunTool(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return null;
        }
    }
}
