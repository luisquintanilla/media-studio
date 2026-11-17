using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MediaStudio.Configuration;

/// <summary>
/// <c>video-production diagnostics</c> and <c>MediaStudio.exe --diagnostics</c>: versions, a settings summary, local runtime
/// detection and model asset state. Never prints keys or tokens; cloud endpoint hosts are masked and per-user
/// directories are shown as environment variables, so the output is safe to paste into an issue.
/// </summary>
public static class Diagnostics
{
    /// <param name="versions">Extra host versions to print, e.g. the app version and the WebView2 runtime.</param>
    /// <param name="store">Settings store; defaults to <see cref="MediaStudioPaths.SettingsFile"/>.</param>
    /// <param name="assets">Model assets; defaults to the bundled manifest and <see cref="MediaStudioPaths.ModelsRoot"/>.</param>
    /// <param name="detector">Runtime detector; defaults to a real one probing localhost.</param>
    /// <param name="toolVersion">Returns the first output line of a tool's version command, or null when it is not installed.</param>
    public static async Task<int> WriteAsync(TextWriter output, IEnumerable<KeyValuePair<string, string>>? versions = null,
        ProviderSettingsStore? store = null, ModelAssets? assets = null, LocalRuntimeDetector? detector = null,
        Func<string, string, string?>? toolVersion = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        toolVersion ??= DefaultToolVersion;
        output.WriteLine("Media Studio diagnostics (no keys or tokens are printed)");

        output.WriteLine();
        output.WriteLine("Versions");
        var entry = Assembly.GetEntryAssembly()?.GetName();
        Row(output, "host", entry is null ? "unknown" : $"{entry.Name} {Version(Assembly.GetEntryAssembly()!)}");
        foreach (var (name, value) in versions ?? []) Row(output, name, value);
        Row(output, ".NET", RuntimeInformation.FrameworkDescription);
        Row(output, "OS", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        foreach (var (tool, arguments) in new[] { ("ollama", "--version"), ("ffmpeg", "-version"), ("node", "--version"), ("espeak-ng", "--version") })
            Row(output, tool, toolVersion(tool, arguments) ?? "not found");

        output.WriteLine();
        output.WriteLine("Settings");
        store ??= new ProviderSettingsStore();
        Row(output, "file", Friendly(store.FilePath) + (store.Exists ? "" : " (missing — run `configure local` or the setup wizard)"));
        ProviderSettings? settings = null;
        if (store.Exists)
        {
            try { settings = store.Load(); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or System.Text.Json.JsonException or ArgumentException)
            {
                Row(output, "error", exception.Message);
            }
        }
        if (settings is not null) WriteSettings(output, settings);

        output.WriteLine();
        output.WriteLine("Local runtimes");
        ModelManifest? manifest = assets?.Manifest;
        if (manifest is null)
        {
            try { manifest = ModelManifest.LoadDefault(); }
            catch (Exception exception) when (exception is FileNotFoundException or InvalidDataException or System.Text.Json.JsonException)
            {
                Row(output, "manifest", exception.Message);
            }
        }
        var report = await (detector ?? new LocalRuntimeDetector()).DetectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var hardware = report.Hardware;
        Row(output, "hardware", string.Create(CultureInfo.InvariantCulture,
            $"{hardware.RamGb:0.#} GB RAM, {(hardware.VramGb is { } vram ? $"{vram:0.#} GB VRAM ({hardware.GpuName ?? "GPU"})" : "no discrete GPU detected")} → tier {report.RecommendedTier}"));
        Row(output, "Ollama", report.Ollama.Available
            ? $"running at {report.Ollama.Endpoint}, {report.Ollama.Models.Length} model(s): {string.Join(", ", report.Ollama.Models)}"
            : $"not running ({report.Ollama.Detail ?? "no response"}) at {report.Ollama.Endpoint}");
        if (manifest is not null && report.Ollama.Available)
        {
            var missing = report.MissingOllamaTags(manifest.GetTier(report.RecommendedTier));
            Row(output, $"tier {report.RecommendedTier} tags", missing.Length == 0 ? "all pulled" : "missing " + string.Join(", ", missing));
        }
        Row(output, "Llama.app", report.LlamaApp is { } llama ? $"running ({llama.Models.Length} models)" : "not running");
        Row(output, "Foundry Local", report.FoundryLocalInstalled ? "installed" : "not installed");
        Row(output, "espeak-ng", report.EspeakNgPath is { } espeak ? Friendly(espeak) : "not found (needed for local Kokoro narration)");

        output.WriteLine();
        output.WriteLine("Model assets");
        if (manifest is not null)
        {
            assets ??= new ModelAssets(manifest);
            Row(output, "root", Friendly(assets.Root));
            foreach (var status in assets.List())
                Row(output, status.Asset.Id, $"{status.State}{(status.Asset.Optional ? " (optional)" : "")}, {ConfigureCommands.FormatBytes(status.Asset.TotalBytes)}");
        }
        return 0;
    }

    private static void WriteSettings(TextWriter output, ProviderSettings settings)
    {
        Row(output, "schema", settings.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        Row(output, "allowCloud", settings.Privacy.AllowCloud ? "true" : "false");
        foreach (var connection in settings.Connections)
        {
            var key = connection.Auth == AuthMode.ApiKey
                ? (CredentialFactory.HasApiKey(connection.Id) ? ", api key: set" : ", api key: NOT set")
                : "";
            Row(output, "connection " + connection.Id,
                $"{Lower(connection.Kind)} {MaskEndpoint(connection.Endpoint)} auth={Lower(connection.Auth)}{key} ({(connection.IsCloud ? "cloud" : "local")})");
        }
        Row(output, "chat tiers", settings.Chat.Tiers.Length == 0 ? "(none)"
            : string.Join(" → ", settings.Chat.Tiers.Select(t => $"{t.Name}={t.Connection}/{t.Model}")));
        Row(output, "routing", $"decider={settings.Chat.Routing.Decider}, authoringFloor={settings.Chat.Routing.AuthoringFloor}, failover={settings.Chat.Routing.Failover}");
        Row(output, "agents", $"authoring={Lower(settings.Agents.Authoring.Runtime)}, illustration={Lower(settings.Agents.Illustration.Runtime)}");
        Row(output, "vision", Role(settings.Vision));
        Row(output, "embeddings", Role(settings.Embeddings));
        Row(output, "documents", string.Join(", ", settings.Documents.Candidates));
        var speech = settings.Speech;
        Row(output, "tts", $"{Lower(speech.Tts.Provider)} {speech.Tts.Voice}".TrimEnd());
        Row(output, "stt", $"{Lower(speech.Stt.Provider)} {speech.Stt.Model}".TrimEnd());
        Row(output, "live", Lower(speech.Live.Provider));
        Row(output, "images", $"{Lower(settings.Images.Mode)}, grounding={settings.Images.Grounding}, critique={settings.Images.Critique.Enabled}");
        Row(output, "decisions", $"julia={Lower(settings.Decisions.Julia)}");
    }

    /// <summary>Loopback endpoints are shown in full; other hosts keep only their last two labels.</summary>
    public static string MaskEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return "(no endpoint)";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return "(invalid endpoint)";
        if (uri.IsLoopback) return uri.GetLeftPart(UriPartial.Path);
        var labels = uri.Host.Split('.');
        var host = labels.Length <= 2 ? "***" : "***." + string.Join('.', labels[^2..]);
        return $"{uri.Scheme}://{host}/";
    }

    /// <summary>Replaces per-user directories with %LOCALAPPDATA% / %USERPROFILE% so pasted output carries no user name.</summary>
    public static string Friendly(string path)
    {
        foreach (var (folder, token) in new[]
                 {
                     (Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%"),
                     (Environment.SpecialFolder.UserProfile, "%USERPROFILE%")
                 })
        {
            var root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return token + path[root.Length..];
        }
        return path;
    }

    private static string Role(RoleModel? role) => role is null ? "(not set)" : $"{role.Connection}/{role.Model}";
    private static string Lower<T>(T value) where T : struct, Enum => System.Text.Json.JsonSerializer.Serialize(value).Trim('"');
    private static void Row(TextWriter output, string name, string value) => output.WriteLine($"  {name,-16} {value}");

    private static string Version(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";

    // ollama prefixes its lines with "Warning:" when its server is down ("Warning: client version is 0.x").
    private static string? DefaultToolVersion(string tool, string arguments) =>
        LocalRuntimeDetector.RunTool(tool, arguments, TimeSpan.FromSeconds(5))?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { } lines
            ? lines.FirstOrDefault(line => !line.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase))
                ?? lines.Where(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"\d+\.\d+"))
                    .Select(line => line["Warning:".Length..].Trim()).FirstOrDefault()
            : null;
}
