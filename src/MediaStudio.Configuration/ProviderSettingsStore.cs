using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MediaStudio.Configuration;

/// <summary>
/// Reads and writes <c>model-providers.json</c> (v2). Writes are validated, serialized under a <c>.lock</c> file
/// (same convention as the v1 store) and replaced atomically. A v1 file found at the path is migrated on load,
/// with the original kept as <see cref="ProviderSettingsMigrator.BackupFileName"/>.
/// </summary>
public sealed class ProviderSettingsStore
{
    public const string FileName = "model-providers.json";
    private readonly Func<ProviderSettings> _baseline;

    public ProviderSettingsStore(string? path = null, Func<ProviderSettings>? migrationBaseline = null)
    {
        FilePath = Path.GetFullPath(path ?? MediaStudioPaths.SettingsFile);
        _baseline = migrationBaseline ?? DefaultBaseline;
    }

    public string FilePath { get; }
    public bool Exists => File.Exists(FilePath);
    public string BackupPath => Path.Combine(Path.GetDirectoryName(FilePath)!, ProviderSettingsMigrator.BackupFileName);

    /// <summary>Changes reported by the most recent migration performed by this store (empty if none).</summary>
    public IReadOnlyList<string> LastMigrationChanges { get; private set; } = [];

    /// <summary>Loads and validates the settings, migrating v1 in place. Throws with every problem listed.</summary>
    public ProviderSettings Load()
    {
        if (!Exists)
            throw new FileNotFoundException($"No provider settings at '{FilePath}'. Run `video-production configure local` or open the Setup wizard.", FilePath);
        var text = File.ReadAllText(FilePath);
        JsonNode? node;
        try { node = JsonNode.Parse(text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"'{FilePath}' is not valid JSON (line {exception.LineNumber + 1}): {exception.Message}", exception);
        }
        if (node is null) throw new InvalidDataException($"'{FilePath}' is empty.");
        var version = ProviderSettingsMigrator.ReadSchemaVersion(node);
        ProviderSettings settings;
        if (version == 1)
        {
            var migration = ProviderSettingsMigrator.Migrate(node, _baseline());
            ProviderSettingsValidator.ThrowIfInvalid(migration.Settings);
            File.Copy(FilePath, BackupPath, overwrite: true);
            WriteLocked(migration.Settings);
            LastMigrationChanges = migration.Changes;
            return migration.Settings;
        }
        if (version != ProviderSettings.CurrentSchemaVersion)
            throw new InvalidDataException($"'{FilePath}' has schemaVersion {version}; this build understands 1 and {ProviderSettings.CurrentSchemaVersion}.");
        try { settings = ProviderSettingsJson.Deserialize(text); }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"'{FilePath}' does not match the v2 schema at {exception.Path}: {exception.Message}", exception);
        }
        ProviderSettingsValidator.ThrowIfInvalid(settings);
        return settings;
    }

    /// <summary>Returns null when the file does not exist; still throws for an invalid file so problems are visible.</summary>
    public ProviderSettings? TryLoad() => Exists ? Load() : null;

    public void Save(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings with { SchemaVersion = ProviderSettings.CurrentSchemaVersion };
        ProviderSettingsValidator.ThrowIfInvalid(settings);
        WriteLocked(settings);
    }

    /// <summary>Imports a v1 file from another location (e.g. the app's old state root) without modifying it, then saves v2 here.</summary>
    public MigrationResult ImportV1(string v1Path)
    {
        var node = JsonNode.Parse(File.ReadAllText(v1Path)) ?? throw new InvalidDataException($"'{v1Path}' is empty.");
        var migration = ProviderSettingsMigrator.Migrate(node, _baseline());
        Save(migration.Settings);
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        File.Copy(v1Path, BackupPath, overwrite: true);
        LastMigrationChanges = migration.Changes;
        return migration;
    }

    private void WriteLocked(ProviderSettings settings)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        using var gate = AcquireLock(FilePath + ".lock");
        var temp = FilePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, ProviderSettingsJson.Serialize(settings), new UTF8Encoding(false));
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static FileStream AcquireLock(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 50) { Thread.Sleep(100); }
        }
    }

    private static ProviderSettings DefaultBaseline()
    {
        try { return ProviderSettingsDefaults.Local(HardwareTier.M, ModelManifest.LoadDefault()); }
        catch (FileNotFoundException)
        {
            return ProviderSettingsDefaults.Local(HardwareTier.M, BuiltInManifest);
        }
    }

    private static readonly ModelManifest BuiltInManifest = new() {
        Assets =
        [
            new ModelAsset { Id = "whisper-large-v3-turbo-q5", Revision = "x", Dir = "whisper", Files = [] }
        ],
        OllamaTiers = new() {
            [HardwareTier.M] = new OllamaTier { Fast = "qwen3.5:4b", Balanced = "qwen3.5:9b", Vision = "qwen3.5:9b", Embedding = "embeddinggemma", WhisperAsset = "whisper-large-v3-turbo-q5" }
        }
    };
}
