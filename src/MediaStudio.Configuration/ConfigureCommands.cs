using System.Globalization;

namespace MediaStudio.Configuration;

/// <summary>
/// Shared implementation of <c>video-production configure …</c> / <c>assets …</c> and <c>MediaStudio.exe --configure …</c>.
/// Writes only the settings file; never writes keys.
/// </summary>
public static class ConfigureCommands
{
    public const string Usage = """
        configure local [--tier S|M|L|XL] [--allow-xl] [--fast <tag>] [--balanced <tag>] [--vision <tag>]
                                                         Write local-first settings from detected hardware/runtimes;
                                                         the tag options replace the tier's Ollama models.
        configure azd [--env <name>] [--from-file <path>] [--tenant <id>] [--cloud-speech]
                                                         Import `azd env get-values` (Foundry endpoint, deployments) and allow cloud.
        configure test [--cloud-only]                    Probe every role and print a ✅/❌ table with fixes.
        configure show                                   Print the settings path and contents.
        configure production [--write] [--state-root <dir>]
                                                         Derive the default production configuration (narration, font, tools,
                                                         Decisions) from these settings, list missing prerequisites, and with
                                                         --write save it under <state-root>\settings\default-production.
        configure copilot [--model <id>] [--tier] [--runtime] [--illustrations] [--byok <connection>] [--allow-cloud] [--off]
                                                         Set the GitHub Copilot options (replaces earlier ones): --tier adds the
                                                         copilot chat tier, --runtime/--illustrations use the Copilot agent runtime,
                                                         --byok runs it on your own connection without sign-in, --off removes it.
                                                         The subscription backend needs cloud: pass --allow-cloud to consent.
        assets list                                      List pinned model assets and their state.
        assets verify [--id <asset>]                     Re-hash installed assets.
        assets ensure [--id <asset>] [--tier S|M|L|XL] [--fp32]
                                                         Download + verify Kokoro, Whisper and Julia for the tier.
        diagnostics                                      Versions, settings summary (no secrets), runtimes and asset state.
        Common: --settings <path> (default %LOCALAPPDATA%\MediaStudio\settings\model-providers.json),
                --models <dir> (default %LOCALAPPDATA%\MediaStudio\models), --manifest <path>.
        """;

    public static bool Handles(string[] args) => args.Length > 0 && args[0] is "configure" or "assets" or "diagnostics";

    /// <param name="checks">Extra <c>configure test</c> rows (the app passes the Copilot check).</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default,
        IEnumerable<IConfigurationCheck>? checks = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            if (args.ElementAtOrDefault(0) == "diagnostics")
            {
                var diagnosticsOptions = Options.Parse(args.Skip(1), false);
                var manifest = diagnosticsOptions.Manifest();
                return await Diagnostics.WriteAsync(output, store: diagnosticsOptions.Store(), assets: diagnosticsOptions.Assets(manifest),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            var options = Options.Parse(args.Skip(2), args.ElementAtOrDefault(1) == "copilot");
            return (args.ElementAtOrDefault(0), args.ElementAtOrDefault(1)) switch {
                ("configure", "local") => await LocalAsync(options, output, cancellationToken).ConfigureAwait(false),
                ("configure", "azd") => await AzdAsync(options, output, cancellationToken).ConfigureAwait(false),
                ("configure", "test") => await TestAsync(options, output, checks, cancellationToken).ConfigureAwait(false),
                ("configure", "copilot") => Copilot(options, output),
                ("configure", "show") => Show(options, output),
                ("assets", "list") => AssetsList(options, output),
                ("assets", "verify") => await AssetsVerifyAsync(options, output, cancellationToken).ConfigureAwait(false),
                ("assets", "ensure") => await AssetsEnsureAsync(options, output, cancellationToken).ConfigureAwait(false),
                _ => UsageError(output)
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
                                              or ArgumentException or KeyNotFoundException or HttpRequestException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync("error: " + exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static int UsageError(TextWriter output)
    {
        output.WriteLine("usage:");
        output.WriteLine(Usage);
        return 2;
    }

    private static async Task<int> LocalAsync(Options options, TextWriter output, CancellationToken cancellationToken)
    {
        var manifest = options.Manifest();
        var report = await new LocalRuntimeDetector().DetectAsync(options.Has("--allow-xl"), cancellationToken).ConfigureAwait(false);
        var tier = options.Tier() ?? report.RecommendedTier;
        WriteDetection(output, report, tier);
        var store = options.Store();
        var local = WithModelOverrides(ProviderSettingsDefaults.Local(tier, manifest), options.Value("--fast"), options.Value("--balanced"), options.Value("--vision"));
        var existing = store.Exists ? store.Load() : null;
        var settings = existing is null ? local : MergeLocal(existing, local);
        store.Save(settings);
        output.WriteLine($"Saved local-first settings (tier {tier}) to {Diagnostics.Friendly(store.FilePath)}");
        var wanted = local.Chat.Tiers.Select(t => t.Model).Append(local.Vision?.Model).Append(local.Embeddings?.Model).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in wanted.Where(tag => !report.Ollama.HasModel(tag)))
            output.WriteLine($"  next: ollama pull {tag}");
        var assets = options.Assets(manifest);
        foreach (var id in manifest.RequiredAssetsFor(tier).Where(id => assets.GetPath(id) is null))
            output.WriteLine($"  next: video-production assets ensure --id {id}   ({FormatBytes(manifest.GetAsset(id).TotalBytes)})");
        output.WriteLine("Then run: video-production configure test   (and configure production to check video generation)");
        return 0;
    }

    /// <summary>Replaces the fast/balanced chat models and the vision model of local-first settings (null keeps the tier default).</summary>
    public static ProviderSettings WithModelOverrides(ProviderSettings local, string? fast, string? balanced, string? vision)
    {
        ArgumentNullException.ThrowIfNull(local);
        string Pick(ChatTier tier) => tier.Name == ChatTierNames.Fast ? fast ?? tier.Model : tier.Name == ChatTierNames.Balanced ? balanced ?? tier.Model : tier.Model;
        return local with {
            Chat = local.Chat with { Tiers = local.Chat.Tiers.Select(t => t with { Model = Pick(t) }).ToArray() },
            Vision = vision is null || local.Vision is null ? local.Vision : local.Vision with { Model = vision }
        };
    }

    /// <summary>Replaces the local parts of <paramref name="existing"/> with <paramref name="local"/>, keeping cloud connections and tiers.</summary>
    public static ProviderSettings MergeLocal(ProviderSettings existing, ProviderSettings local)
    {
        var localIds = local.Connections.Select(c => c.Id).ToHashSet();
        var connections = local.Connections.Concat(existing.Connections.Where(c => !localIds.Contains(c.Id))).ToArray();
        var localTierNames = local.Chat.Tiers.Select(t => t.Name).ToHashSet();
        var tiers = local.Chat.Tiers.Concat(existing.Chat.Tiers.Where(t => !localTierNames.Contains(t.Name) && t.Connection is not null && !localIds.Contains(t.Connection))).ToArray();
        bool IsLocal(RoleModel? role) => role is null || localIds.Contains(role.Connection);
        return existing with {
            Connections = connections,
            Chat = existing.Chat with { Tiers = tiers },
            Vision = IsLocal(existing.Vision) ? local.Vision : existing.Vision,
            Embeddings = IsLocal(existing.Embeddings) ? local.Embeddings : existing.Embeddings,
            Speech = existing.Speech.Stt.Provider == SttProvider.Whisper
                ? existing.Speech with { Stt = existing.Speech.Stt with { Model = local.Speech.Stt.Model } }
                : existing.Speech
        };
    }

    private static async Task<int> AzdAsync(Options options, TextWriter output, CancellationToken cancellationToken)
    {
        var raw = options.Value("--from-file") is { } file
            ? await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)
            : await AzdEnvironmentImporter.RunAzdAsync(options.Value("--env"), cancellationToken: cancellationToken).ConfigureAwait(false);
        var outputs = AzdEnvironmentImporter.Parse(raw);
        var store = options.Store();
        var baseline = store.Exists ? store.Load() : ProviderSettingsDefaults.Local(options.Tier() ?? TierSelector.Select(HardwareProfile.Detect()), options.Manifest());
        var settings = AzdEnvironmentImporter.Apply(baseline, outputs, options.Value("--tenant"), options.Has("--cloud-speech"));
        store.Save(settings);
        output.WriteLine($"Imported Foundry account {new Uri(outputs.FoundryEndpoint).Host} ({outputs.Location ?? "region unknown"}) into {Diagnostics.Friendly(store.FilePath)}");
        output.WriteLine($"  deployments: {(outputs.Deployments.Count == 0 ? "(defaults: role names)" : string.Join(", ", outputs.Deployments.Select(d => $"{d.Key}={d.Value}")))}");
        output.WriteLine($"  tiers: {string.Join(" → ", settings.Chat.Tiers.Select(t => $"{t.Name}({t.Model})"))}");
        output.WriteLine($"  documents: {string.Join(", ", settings.Documents.Candidates)}");
        output.WriteLine("  privacy.allowCloud = true (local tiers are still tried first). Auth: Entra via DefaultAzureCredential; no keys were written.");
        output.WriteLine("Then run: video-production configure test   (and configure production to check video generation)");
        return 0;
    }

    private static async Task<int> TestAsync(Options options, TextWriter output, IEnumerable<IConfigurationCheck>? extensions,
        CancellationToken cancellationToken)
    {
        var store = options.Store();
        var settings = store.Load();
        var tester = new ConfigurationTester(assets: options.Assets(options.Manifest()), extensions: extensions);
        var checks = await tester.TestAsync(settings, options.Has("--cloud-only"), cancellationToken).ConfigureAwait(false);
        output.WriteLine($"Settings: {Diagnostics.Friendly(store.FilePath)} (cloud {(settings.Privacy.AllowCloud ? "allowed" : "off")})");
        var width = checks.Count == 0 ? 10 : checks.Max(c => c.Role.Length);
        foreach (var check in checks)
        {
            output.WriteLine($"{check.Symbol} {check.Role.PadRight(width)}  {check.Detail}");
            if (check.Status == RoleCheckStatus.Failed && check.Remediation is { } fix) output.WriteLine($"   {new string(' ', width)}  fix: {fix}");
        }
        var failed = checks.Count(c => c.Status == RoleCheckStatus.Failed);
        output.WriteLine(failed == 0 ? "All checked roles are ready." : $"{failed} role(s) need attention. See {ConfigurationTester.TroubleshootingDoc}.");
        return failed == 0 ? 0 : 1;
    }

    private static int Copilot(Options options, TextWriter output)
    {
        var store = options.Store();
        var settings = store.Load();
        if (options.Has("--off"))
        {
            store.Save(ProviderSettingsDefaults.WithoutCopilot(settings));
            output.WriteLine($"Removed GitHub Copilot from {Diagnostics.Friendly(store.FilePath)}. Sign out of the app's Copilot login from Settings → Setup.");
            return 0;
        }
        var byok = options.Value("--byok");
        if (byok is not null && settings.FindConnection(byok) is null)
            throw new ArgumentException($"--byok: no connection '{byok}'. Known: {string.Join(", ", settings.Connections.Select(c => c.Id))}.");
        if (options.Has("--allow-cloud")) settings = settings with { Privacy = settings.Privacy with { AllowCloud = true } };
        settings = ProviderSettingsDefaults.WithCopilot(settings, options.Value("--model"), options.Has("--tier"),
            options.Has("--runtime"), options.Has("--illustrations"), byok);
        store.Save(settings);
        var copilot = settings.Agents.Copilot;
        output.WriteLine($"Saved GitHub Copilot settings to {Diagnostics.Friendly(store.FilePath)}");
        output.WriteLine($"  backend: {(copilot.Backend == CopilotBackend.Byok ? "byok on '" + copilot.ByokConnection + "' (no GitHub sign-in)" : "your Copilot subscription (uses premium requests; prompts leave this PC)")}");
        output.WriteLine($"  model: {copilot.Model ?? "(Copilot default)"} · tier: {(options.Has("--tier") ? "on" : "off")} · authoring: {settings.Agents.Authoring.Runtime.ToString().ToLowerInvariant()} · illustrations: {settings.Agents.Illustration.Runtime.ToString().ToLowerInvariant()}");
        if (copilot.Backend == CopilotBackend.Subscription && ProviderSettingsDefaults.UsesCopilot(settings))
            output.WriteLine("  sign in: Media Studio → Settings → Setup → GitHub Copilot, `gh auth login`, or set COPILOT_GITHUB_TOKEN.");
        output.WriteLine("Then run: MediaStudio.exe --configure test");
        return 0;
    }

    private static int Show(Options options, TextWriter output)
    {
        var store = options.Store();
        output.WriteLine(Diagnostics.Friendly(store.FilePath));
        output.WriteLine(store.Exists ? ProviderSettingsJson.Serialize(store.Load()) : "(not configured — run `video-production configure local`)");
        return 0;
    }

    private static int AssetsList(Options options, TextWriter output)
    {
        var assets = options.Assets(options.Manifest());
        output.WriteLine($"Models root: {Diagnostics.Friendly(assets.Root)}");
        foreach (var status in assets.List())
            output.WriteLine($"{status.State,-9} {status.Asset.Id,-28} {FormatBytes(status.Asset.TotalBytes),9}  {status.Asset.License,-11} {status.Asset.DisplayName}{(status.Asset.Optional ? " (optional)" : "")}");
        return 0;
    }

    private static async Task<int> AssetsVerifyAsync(Options options, TextWriter output, CancellationToken cancellationToken)
    {
        var assets = options.Assets(options.Manifest());
        var ids = options.Value("--id") is { } id ? [id] : assets.List().Where(s => s.State != ModelAssetState.Missing).Select(s => s.Asset.Id).ToArray();
        var failed = 0;
        foreach (var assetId in ids)
        {
            var status = await assets.VerifyAsync(assetId, cancellationToken).ConfigureAwait(false);
            if (status.State != ModelAssetState.Verified) failed++;
            output.WriteLine($"{(status.State == ModelAssetState.Verified ? "✅" : "❌")} {assetId}: {status.State}{(status.Detail is null ? "" : " — " + status.Detail)}");
        }
        if (ids.Length == 0) output.WriteLine("No assets installed. Run: video-production assets ensure");
        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> AssetsEnsureAsync(Options options, TextWriter output, CancellationToken cancellationToken)
    {
        var manifest = options.Manifest();
        var assets = options.Assets(manifest);
        string[] ids;
        if (options.Value("--id") is { } id) ids = [id];
        else
        {
            var tier = options.Tier() ?? TierFromSettings(options, manifest) ?? TierSelector.Select(HardwareProfile.Detect());
            ids = manifest.RequiredAssetsFor(tier, options.Has("--fp32"));
            output.WriteLine($"Tier {tier}: {string.Join(", ", ids)}");
        }
        foreach (var assetId in ids)
        {
            var asset = manifest.GetAsset(assetId);
            output.WriteLine($"{assetId} ({FormatBytes(asset.TotalBytes)}, {asset.License}) → {assets.GetDirectory(assetId)}");
            var lastPercent = -1;
            var progress = new InlineProgress(p =>
            {
                var percent = (int)(p.Fraction * 100);
                if (percent / 5 == lastPercent / 5) return;
                lastPercent = percent;
                output.WriteLine($"  {percent,3}%  {FormatBytes(p.BytesDone)} / {FormatBytes(p.BytesTotal)}  {p.File}");
            });
            var directory = await assets.EnsureAsync(assetId, progress, cancellationToken).ConfigureAwait(false);
            output.WriteLine($"✅ {assetId} verified at {Diagnostics.Friendly(directory)}");
        }
        return 0;
    }

    private static HardwareTier? TierFromSettings(Options options, ModelManifest manifest)
    {
        var store = options.Store();
        if (!store.Exists) return null;
        var balanced = store.Load().FindTier(ChatTierNames.Balanced)?.Model;
        return manifest.OllamaTiers.Where(t => t.Value.Balanced == balanced).Select(t => (HardwareTier?)t.Key).FirstOrDefault();
    }

    private static void WriteDetection(TextWriter output, LocalRuntimeReport report, HardwareTier tier)
    {
        output.WriteLine($"Hardware: {report.Hardware.RamGb:0.#} GB RAM, {(report.Hardware.VramGb is { } vram ? $"{vram:0.#} GB VRAM ({report.Hardware.GpuName ?? "GPU"})" : "no discrete GPU detected")} → tier {report.RecommendedTier}{(tier != report.RecommendedTier ? $" (using {tier})" : "")}");
        output.WriteLine(report.Ollama.Available
            ? $"Ollama: running, {report.Ollama.Models.Length} model(s): {string.Join(", ", report.Ollama.Models)}"
            : "Ollama: not running. Install: winget install Ollama.Ollama");
        if (report.LlamaApp is { } llama) output.WriteLine($"Llama.app: running ({llama.Models.Length} models)");
        if (report.FoundryLocalInstalled) output.WriteLine("Foundry Local: installed");
        output.WriteLine($"espeak-ng: {report.EspeakNgPath ?? "not found (optional)"}");
    }

    public static string FormatBytes(long bytes) => bytes switch {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes + " B"
    };

    private sealed class InlineProgress(Action<ModelAssetProgress> report) : IProgress<ModelAssetProgress>
    {
        public void Report(ModelAssetProgress value) => report(value);
    }

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] CommonFlags = ["--allow-xl", "--cloud-only", "--cloud-speech", "--fp32"];
        private static readonly string[] CommonValued = ["--tier", "--env", "--from-file", "--tenant", "--id", "--settings", "--models", "--manifest", "--fast", "--balanced", "--vision"];
        // `configure copilot --tier` is a flag (add the copilot tier), not a hardware tier.
        private static readonly string[] CopilotFlags = ["--tier", "--runtime", "--illustrations", "--allow-cloud", "--off"];
        private static readonly string[] CopilotValued = ["--model", "--byok", "--settings", "--models", "--manifest"];

        public static Options Parse(IEnumerable<string> args, bool copilot = false)
        {
            var options = new Options();
            var flags = copilot ? CopilotFlags : CommonFlags;
            var valued = copilot ? CopilotValued : CommonValued;
            using var enumerator = args.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var name = enumerator.Current;
                if (flags.Contains(name, StringComparer.OrdinalIgnoreCase)) options._values[name] = null;
                else if (valued.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!enumerator.MoveNext() || enumerator.Current.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"{name} needs a value.");
                    options._values[name] = enumerator.Current;
                }
                else throw new ArgumentException($"Unknown option '{name}'.{Environment.NewLine}{Usage}");
            }
            return options;
        }

        public bool Has(string name) => _values.ContainsKey(name);
        public string? Value(string name) => _values.TryGetValue(name, out var value) ? value : null;

        public HardwareTier? Tier() => Value("--tier") is { } tier
            ? Enum.TryParse<HardwareTier>(tier, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed
                : throw new ArgumentException($"--tier must be S, M, L or XL (got '{tier}').")
            : null;

        public ProviderSettingsStore Store() => new(Value("--settings") is { } path ? Path.GetFullPath(path) : null,
            () => ProviderSettingsDefaults.Local(Tier() ?? HardwareTier.M, Manifest()));

        public ModelManifest Manifest() => Value("--manifest") is { } path ? ModelManifest.Load(path) : ModelManifest.LoadDefault();
        public ModelAssets Assets(ModelManifest manifest) => new(manifest, Value("--models"));
    }

}
