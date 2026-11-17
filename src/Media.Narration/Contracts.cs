using System.Text.Json;
using System.Text.Json.Serialization;

// Retained namespace keeps the extracted capability source-compatible with the video adapter.
namespace VideoProduction;

public sealed class VideoException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class Guard
{
    public static void Require(bool condition, string code, string message)
    {
        if (!condition) throw new VideoException(code, message);
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    public static string Text<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new VideoException("json.empty", $"Empty JSON: {path}");
    public static void Write<T>(string path, T value) => Atomic.Write(path, Text(value));
}

public sealed record Asset(string Id, string Path, string Sha256, string Provenance, string License);
public sealed record ArtifactHash(string Path, string Sha256);
public sealed record StageRecord(string Fingerprint, ArtifactHash[] Outputs);
public sealed record NarrationTake(string WavPath, string WavSha256, int SampleRate, long SampleCount,
    string DisplayText, string MetadataPath);

public interface INarrationBackend
{
    Task<NarrationTake> Synthesize(string displayText, string outputDirectory, CancellationToken ct);
}
