using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaStudio.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<HardwareTier>))]
public enum HardwareTier { S, M, L, XL }

public sealed record ModelAssetFile
{
    /// <summary>Relative path inside the asset directory (forward slashes).</summary>
    public required string Path { get; init; }
    public required string Url { get; init; }
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record ModelAsset
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public string? License { get; init; }
    public string? Source { get; init; }
    public required string Revision { get; init; }
    /// <summary>Directory under the models root (forward slashes). Several assets may share one directory.</summary>
    public required string Dir { get; init; }
    public bool Optional { get; init; }
    public ModelAssetFile[] Files { get; init; } = [];
    [JsonIgnore] public long TotalBytes => Files.Sum(f => f.Size);
}

public sealed record OllamaTier
{
    public string? Description { get; init; }
    public required string Fast { get; init; }
    public required string Balanced { get; init; }
    public required string Vision { get; init; }
    public required string Embedding { get; init; }
    public required string WhisperAsset { get; init; }

    /// <summary>Distinct Ollama tags to pull for this tier.</summary>
    [JsonIgnore] public string[] Tags => new[] { Fast, Balanced, Vision, Embedding }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

public sealed record CloudVoice
{
    public required string Voice { get; init; }
    public string[] Styles { get; init; } = [];
    public string? DefaultStyle { get; init; }
    public bool Default { get; init; }
    public bool Quality { get; init; }
    public bool Fallback { get; init; }
}

/// <summary>assets/model-manifest.json: pinned local model files, Ollama tiers and MAI cloud voices.</summary>
public sealed record ModelManifest
{
    public const string FileName = "model-manifest.json";
    public int SchemaVersion { get; init; } = 1;
    public ModelAsset[] Assets { get; init; } = [];
    public Dictionary<HardwareTier, OllamaTier> OllamaTiers { get; init; } = [];
    public CloudVoice[] CloudVoices { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string DefaultPath => System.IO.Path.Combine(AppContext.BaseDirectory, "assets", FileName);

    public static ModelManifest LoadDefault() => Load(DefaultPath);

    public static ModelManifest Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Model manifest not found at '{path}'. Rebuild the app so assets/{FileName} is copied to the output.", path);
        return Parse(File.ReadAllText(path));
    }

    public static ModelManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<ModelManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("The model manifest is empty.");
        manifest.Validate();
        return manifest;
    }

    public ModelAsset GetAsset(string id) =>
        Assets.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown model asset '{id}'. Known: {string.Join(", ", Assets.Select(a => a.Id))}.");

    public OllamaTier GetTier(HardwareTier tier) =>
        OllamaTiers.TryGetValue(tier, out var value) ? value : throw new KeyNotFoundException($"The manifest has no Ollama tier '{tier}'.");

    public CloudVoice? DefaultCloudVoice => CloudVoices.FirstOrDefault(v => v.Default) ?? CloudVoices.FirstOrDefault();

    /// <summary>Required asset ids for a hardware tier: Kokoro (quantized), the tier's Whisper model and Julia.</summary>
    public string[] RequiredAssetsFor(HardwareTier tier, bool fp32Kokoro = false)
    {
        var ids = new List<string> { "kokoro-v1.0-q8", GetTier(tier).WhisperAsset, "julia-1" };
        if (fp32Kokoro) ids.Add("kokoro-v1.0-fp32");
        return ids.Where(id => Assets.Any(a => a.Id == id)).Distinct().ToArray();
    }

    private void Validate()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in Assets)
        {
            if (!ids.Add(asset.Id)) throw new InvalidDataException($"Duplicate asset id '{asset.Id}' in the model manifest.");
            ValidateRelative(asset.Dir, asset.Id);
            if (asset.Files.Length == 0) throw new InvalidDataException($"Asset '{asset.Id}' lists no files.");
            foreach (var file in asset.Files)
            {
                ValidateRelative(file.Path, asset.Id);
                if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException($"Asset '{asset.Id}' file '{file.Path}' must use an HTTPS URL.");
                if (!file.Url.Contains(asset.Revision, StringComparison.Ordinal))
                    throw new InvalidDataException($"Asset '{asset.Id}' file '{file.Path}' URL is not pinned to revision {asset.Revision}.");
                if (file.Size <= 0) throw new InvalidDataException($"Asset '{asset.Id}' file '{file.Path}' needs a positive size.");
                if (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                    throw new InvalidDataException($"Asset '{asset.Id}' file '{file.Path}' needs a 64-character SHA-256.");
            }
        }
        foreach (var (name, tier) in OllamaTiers)
            if (!ids.Contains(tier.WhisperAsset))
                throw new InvalidDataException($"Ollama tier '{name}' references unknown whisper asset '{tier.WhisperAsset}'.");
    }

    private static void ValidateRelative(string path, string asset)
    {
        if (string.IsNullOrWhiteSpace(path) || System.IO.Path.IsPathRooted(path) ||
            path.Split('/', '\\').Any(part => part is ".." or "." || part.Length == 0))
            throw new InvalidDataException($"Asset '{asset}' has an unsafe relative path '{path}'.");
    }
}
