using MediaStudio.Configuration;

namespace VideoProduction.Providers;

/// <summary><c>configure production [--write] [--state-root &lt;dir&gt;]</c>: show (and optionally write) the derived default production configuration.</summary>
public static class DefaultProductionCommand
{
    public static bool Handles(string[] args) => args is ["configure", "production", ..];

    /// <param name="defaultStateRoot">State root when <c>--state-root</c> is absent (the app passes its own; the CLI uses
    /// <c>%LOCALAPPDATA%\MediaStudio\production</c>).</param>
    public static int Run(string[] args, TextWriter output, string? defaultStateRoot = null,
        Func<string, string?>? findExecutable = null, Func<string?>? findEspeak = null, string? sourceRoot = null, string? fontDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 2; i < args.Length; i++)
            {
                var name = args[i];
                if (name == "--write") values[name] = null;
                else if (name is "--state-root" or "--settings" or "--models" or "--manifest")
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"{name} needs a value.");
                    values[name] = args[++i];
                }
                else throw new ArgumentException($"Unknown option '{name}'. Usage: configure production [--write] [--state-root <dir>] [--settings <path>] [--models <dir>] [--manifest <path>]");
            }
            string? Value(string name) => values.TryGetValue(name, out var value) ? value : null;
            var store = new ProviderSettingsStore(Value("--settings") is { } path ? Path.GetFullPath(path) : null);
            var settings = store.TryLoad();
            if (settings is null)
                output.WriteLine($"No settings at {store.FilePath}; using built-in local defaults (run 'configure local' to save them).");
            var manifest = Value("--manifest") is { } manifestPath ? ModelManifest.Load(manifestPath) : ModelManifest.LoadDefault();
            var assets = new ModelAssets(manifest, Value("--models"));
            var stateRoot = Path.GetFullPath(Value("--state-root") ?? defaultStateRoot ?? Path.Combine(MediaStudioPaths.Root, "production"));
            var write = values.ContainsKey("--write");
            if (write) Directory.CreateDirectory(stateRoot);
            var result = write
                ? DefaultProductionConfiguration.Write(settings ?? new ProviderSettings(), stateRoot, assets, sourceRoot, findExecutable, findEspeak, fontDirectory)
                : DefaultProductionConfiguration.Build(settings ?? new ProviderSettings(), stateRoot, assets, sourceRoot, findExecutable, findEspeak, fontDirectory);
            output.WriteLine($"State root:        {stateRoot}");
            output.WriteLine($"Media config:      {result.ConfigurationFile}");
            output.WriteLine($"Produce config:    {result.FileProductionConfigurationFile}   (video-production produce --production-config)");
            output.WriteLine($"Summary:           {result.Summary}");
            foreach (var missing in result.Missing) output.WriteLine($"  ❌ {missing}");
            if (result.Configuration is null) output.WriteLine("Nothing written: fix the items above first.");
            else if (write) output.WriteLine(result.Written ? "Written (owner-only files, no secrets)." : "Up to date; nothing changed.");
            else output.WriteLine("Preview only; pass --write to save it.");
            if (result.Ready) output.WriteLine("✅ Ready to generate.");
            return result.Ready ? 0 : 1;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException
                                          or InvalidOperationException or VideoException)
        {
            output.WriteLine("error: " + error.Message);
            return 1;
        }
    }
}
