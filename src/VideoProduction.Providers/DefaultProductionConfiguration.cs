using MediaStudio.Configuration;
using MediaStudio.Speech.Kokoro;

namespace VideoProduction.Providers;

/// <summary>A production prerequisite that is not ready, with the command that fixes it.</summary>
public sealed record ProductionPrerequisite(string Name, string Problem, string Remedy)
{
    public override string ToString() => $"{Problem} Fix: {Remedy}";
}

/// <summary>
/// The derived default production setup. <see cref="Configuration"/> is null only when no configuration can be derived at all
/// (unsupported narration provider, missing renderer sources or bundled font); <see cref="Missing"/> then explains why.
/// </summary>
public sealed record DefaultProductionResult(string ConfigurationFile, string ToolsFile, string FileProductionConfigurationFile,
    MediaProductionConfiguration? Configuration, ProductionPrerequisite[] Missing, bool Written = false)
{
    public bool Ready => Configuration is not null && Missing.Length == 0;

    /// <summary>One actionable sentence per missing prerequisite, or null when generation is ready.</summary>
    public string? Problem => Ready ? null
        : "Generation needs setup. Your storyboard is saved. " + string.Join(" ", Missing.Select(m => m.ToString()));

    public string Summary
    {
        get
        {
            if (Configuration is not { } configuration) return "No default production configuration could be derived.";
            var speech = configuration.Audio!.Speech;
            var julia = configuration.Decisions?.JuliaModelDirectory is null ? "heuristic" : "Julia (local) then heuristic";
            return $"Narration {speech.Provider} · voice {speech.Voice} · {speech.Language} · speed {speech.Speed}"
                + (speech.Quality is { } quality ? $" · {quality}" : "")
                + $"; {configuration.Audio.SampleRate} Hz. Font {BundledFont.Description}. Tools node/ffmpeg/ffprobe from PATH."
                + $" Illustrations routed by settings at review. Decisions {julia}.";
        }
    }
}

/// <summary>
/// Derives the default <see cref="MediaProductionConfiguration"/> from provider settings v2, the pinned model assets and the
/// tools on PATH, so a fresh <c>configure local</c>/<c>configure azd</c> install can generate without a hand-written
/// <c>--media-config</c>. The files are private (owner-only) and rewritten only when the derived content changes. They never
/// contain secrets: speech connections are resolved from the saved settings by the producer, and API keys stay in the
/// environment or user secrets.
/// </summary>
public static class DefaultProductionConfiguration
{
    public const string FolderName = "default-production";
    public const string ConfigurationFileName = "media-config.json";
    public const string ToolsFileName = "tools.json";
    public const string FileProductionFileName = "production-config.json";
    public const string SourceRootVariable = "VIDEO_PRODUCTION_HOME";
    public const string InstallPrerequisites = @"run scripts\Install-Prerequisites.ps1 (or install it and put it on PATH), then retry.";

    public static string Folder(string stateRoot) => Path.Combine(PrivateFiles.LocalPath(stateRoot), "settings", FolderName);

    /// <summary>Derives the configuration without writing anything.</summary>
    /// <param name="sourceRoot">Repository root holding <c>workers/browser</c>; default: <see cref="FindSourceRoot"/>.</param>
    /// <param name="findExecutable">Resolves a tool on PATH (null when missing); test seam.</param>
    /// <param name="findEspeak">Locates espeak-ng (null when missing); test seam.</param>
    /// <param name="fontDirectory">Directory holding the bundled font and its license; default: <see cref="BundledFont.Directory"/>.</param>
    public static DefaultProductionResult Build(ProviderSettings settings, string stateRoot, ModelAssets? assets = null,
        string? sourceRoot = null, Func<string, string?>? findExecutable = null, Func<string?>? findEspeak = null,
        string? fontDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = PrivateFiles.LocalPath(stateRoot);
        var folder = Folder(root);
        var configurationFile = Path.Combine(folder, ConfigurationFileName);
        var toolsFile = Path.Combine(folder, ToolsFileName);
        var fileProductionFile = Path.Combine(folder, FileProductionFileName);
        assets ??= new ModelAssets(ModelManifest.LoadDefault());
        findExecutable ??= FindOnPath;
        findEspeak ??= LocalRuntimeDetector.FindEspeakNg;
        var missing = new List<ProductionPrerequisite>();

        var speech = Speech(settings, missing);
        if (speech?.Provider == NarrationSpeechClient.Kokoro)
        {
            var assetId = string.Equals(speech.Quality, "fp32", StringComparison.OrdinalIgnoreCase)
                ? KokoroAssetPaths.Fp32AssetId : KokoroAssetPaths.QuantizedAssetId;
            if (assets.GetPath(assetId) is null)
                missing.Add(new("kokoro", $"Kokoro narration assets '{assetId}' are not installed.",
                    $"run 'video-production assets ensure --id {assetId}' (or MediaStudio.exe --configure assets ensure --id {assetId})."));
            if (findEspeak() is null)
                missing.Add(new("espeak-ng", "espeak-ng (Kokoro phonemizer) is not on PATH.", InstallPrerequisites));
        }
        foreach (var tool in new[] { "ffmpeg", "ffprobe", "node" })
            if (findExecutable(tool) is null)
                missing.Add(new(tool, $"{tool} is not on PATH.", InstallPrerequisites));

        var source = sourceRoot ?? FindSourceRoot();
        if (source is null || !File.Exists(Path.Combine(source, "workers", "browser", "worker.mjs")))
        {
            missing.Add(new("renderer", "The video renderer sources (workers/browser) were not found.",
                $"run Media Studio from its repository build, or set {SourceRootVariable} to the repository root."));
            source = null;
        }
        else if (!Directory.Exists(Path.Combine(source, "workers", "browser", "node_modules", "playwright")))
            missing.Add(new("playwright", "The video renderer's npm packages (Playwright) are not installed.",
                @"run scripts\Install-Prerequisites.ps1 (or 'npm ci' and 'npx playwright install chromium' in workers\browser), then retry."));

        var fonts = fontDirectory ?? BundledFont.Directory;
        var font = Path.Combine(fonts, BundledFont.FileName);
        var license = Path.Combine(fonts, BundledFont.LicenseFileName);
        var fontReady = File.Exists(font) && File.Exists(license) && Files.Hash(font) == BundledFont.Sha256;
        if (!fontReady)
            missing.Add(new("font", $"The bundled font is missing or modified in '{fonts}'.", "rebuild so assets/fonts is copied to the output."));

        MediaProductionConfiguration? configuration = null;
        if (speech is not null && source is not null && fontReady)
        {
            var video = new GenerationConfiguration(toolsFile, Path.GetFullPath(font), BundledFont.Sha256,
                Path.GetFullPath(license), Files.Hash(license));
            string? julia = null;
            if (settings.Decisions.Julia != JuliaMode.Off && assets.Manifest.Assets.Any(a => a.Id == Decisions.DecisionClientFactory.JuliaAssetId))
            {
                try { julia = assets.GetPath(Decisions.DecisionClientFactory.JuliaAssetId); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
            configuration = new(1, root, new(KokoroSampleRate, speech, NarrationSpeechClient.IdentityFor(speech, assets.Manifest)),
                video, Decisions: new(julia, null));
        }
        return new(configurationFile, toolsFile, fileProductionFile, configuration, missing.ToArray());
    }

    /// <summary>
    /// Derives the configuration and writes it (with its tools file and a <c>produce --production-config</c> file) under
    /// <c>&lt;stateRoot&gt;/settings/default-production</c> when the derived content differs from what is there.
    /// </summary>
    public static DefaultProductionResult Write(ProviderSettings settings, string stateRoot, ModelAssets? assets = null,
        string? sourceRoot = null, Func<string, string?>? findExecutable = null, Func<string?>? findEspeak = null,
        string? fontDirectory = null)
    {
        var result = Build(settings, stateRoot, assets, sourceRoot, findExecutable, findEspeak, fontDirectory);
        if (result.Configuration is not { } configuration) return result;
        var speech = configuration.Audio!.Speech;
        var tools = new Tools(Path.GetFullPath(sourceRoot ?? FindSourceRoot()!), "node", "ffmpeg", "ffprobe", Speech: speech);
        var folder = Path.GetDirectoryName(result.ConfigurationFile)!;
        Directory.CreateDirectory(folder);
        PrivateFiles.Protect(folder, directory: true);
        var written = WriteIfChanged(result.ToolsFile, tools);
        written |= WriteIfChanged(result.FileProductionConfigurationFile,
            new FileProductionConfiguration(1, configuration.StateRoot, configuration.Video!));
        written |= WriteIfChanged(result.ConfigurationFile, configuration);
        return result with { Written = written };
    }

    /// <summary>
    /// The launch rule: an explicit <c>--media-config</c> always wins (null is returned and nothing is derived); otherwise a
    /// deriver that writes the default under <paramref name="stateRoot"/> from the settings current at each call.
    /// </summary>
    public static Func<DefaultProductionResult>? ForLaunch(string? explicitConfigurationFile, string stateRoot, Func<ProviderSettings> settings,
        ModelAssets? assets = null, string? sourceRoot = null, Func<string, string?>? findExecutable = null, Func<string?>? findEspeak = null,
        string? fontDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return explicitConfigurationFile is not null ? null
            : () => Write(settings(), stateRoot, assets, sourceRoot, findExecutable, findEspeak, fontDirectory);
    }

    /// <summary>The repository root holding the renderer worker: <c>VIDEO_PRODUCTION_HOME</c>, then the app directory and the current directory and their parents.</summary>
    public static string? FindSourceRoot()
    {
        static bool IsRoot(string? directory) => directory is not null
            && File.Exists(Path.Combine(directory, "workers", "browser", "worker.mjs"));
        var configured = Environment.GetEnvironmentVariable(SourceRootVariable);
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) && IsRoot(configured))
            return Path.GetFullPath(configured);
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (IsRoot(directory.FullName)) return directory.FullName;
        return null;
    }

    private const int KokoroSampleRate = 24000;

    private static SpeechConfig? Speech(ProviderSettings settings, List<ProductionPrerequisite> missing)
    {
        var tts = settings.Speech.Tts;
        var speed = (float)tts.Speed;
        switch (tts.Provider)
        {
            case TtsProvider.Kokoro:
                var voice = string.IsNullOrWhiteSpace(tts.Voice) ? "af_heart" : tts.Voice;
                return new(NarrationSpeechClient.Kokoro, voice, KokoroTextToSpeechClient.LanguageForVoice(voice), speed,
                    string.Equals(tts.Quality, "fp32", StringComparison.OrdinalIgnoreCase) ? "fp32" : "quantized");
            case TtsProvider.AzureSpeech when !settings.Privacy.AllowCloud:
                missing.Add(new("speech", "Narration is set to Azure Speech but cloud use is off (privacy.allowCloud).",
                    "run 'video-production configure local' for local Kokoro narration, or 'configure azd --cloud-speech' to opt in to cloud speech."));
                return null;
            case TtsProvider.AzureSpeech when string.IsNullOrWhiteSpace(tts.Voice):
                missing.Add(new("speech", "Azure Speech narration has no voice.", "run 'video-production configure azd --cloud-speech' again."));
                return null;
            case TtsProvider.AzureSpeech:
                var parts = tts.Voice!.Split('-');
                return new(NarrationSpeechClient.AzureSpeech, tts.Voice!, parts.Length >= 2 ? parts[0] + "-" + parts[1] : "en-US", speed);
            default:
                missing.Add(new("speech", $"Narration provider '{tts.Provider}' cannot produce narration.",
                    "switch narration to Kokoro ('video-production configure local') or Azure Speech ('configure azd --cloud-speech')."));
                return null;
        }
    }

    private static string? FindOnPath(string name)
    {
        try { return Files.FindExecutable(name); }
        catch (VideoException) { return null; }
    }

    private static bool WriteIfChanged<T>(string path, T value)
    {
        if (File.Exists(path))
        {
            try
            {
                PrivateFiles.RequirePrivate(path);
                if (File.ReadAllText(path) == Json.Text(value)) return false;
            }
            catch (VideoException) { }
        }
        Json.Write(path, value);
        PrivateFiles.Protect(path);
        return true;
    }
}
