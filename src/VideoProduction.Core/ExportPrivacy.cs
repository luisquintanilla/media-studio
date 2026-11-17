using System.Text.Json;

namespace VideoProduction;

public static class ExportPrivacy
{
    private static readonly HashSet<string> ForbiddenKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "accessToken", "apiKey", "password", "authorization", "cookie", "cookies", "storageState",
        "authentication", "requestHeaders", "responseHeaders", "headers", "secret", "credential", "credentials"
    };
    private static readonly HashSet<string> TakeFiles = new(StringComparer.Ordinal)
    {
        "browser.webm", "probe.json", "source-clock.json", "landmarks.json", "quality.json", "process.json",
        "stdout.txt", "stderr.txt", "anchors.json", "observations.json", "native-evidence.json",
        "before.png", "after.png", "search.png", "result.png", "detail.png", "search-click.png", "result-click.png"
    };

    public static void CheckTake(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            Guard.Require(TakeFiles.Contains(Path.GetFileName(file)) && Path.GetDirectoryName(file) == directory
                && !File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint),
                "export.private-file", "Unexpected file in frozen take; traces/HAR/auth/config are never exportable.");
            if (Path.GetExtension(file) == ".json") CheckJson(File.ReadAllText(file));
        }
    }

    public static void CheckAsset(string path)
    {
        Guard.Require(new[] { ".json", ".txt", ".md", ".csv", ".srt", ".vtt", ".wav", ".png", ".jpg", ".jpeg",
                ".mp4", ".webm", ".mkv", ".ttf" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            && !new[] { "auth.json", "storagestate.json", "config.json", ".env", ".npmrc" }
                .Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase),
            "export.asset-format", "Project source assets cannot bundle executable/model/native/private-config files.");
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) CheckJson(File.ReadAllText(path));
    }

    public static void CheckJson(string text)
    {
        using var document = JsonDocument.Parse(text);
        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject())
                {
                    Guard.Require(!ForbiddenKeys.Contains(property.Name), "export.secret", "Sensitive auth/header/state field cannot be exported.");
                    Visit(property.Value);
                }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var value in element.EnumerateArray()) Visit(value);
            else if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString()!;
                Guard.Require(!value.Contains("api_key=", StringComparison.OrdinalIgnoreCase)
                    && !value.Contains("access_token=", StringComparison.OrdinalIgnoreCase)
                    && !value.Contains("X-Emby-Token", StringComparison.OrdinalIgnoreCase)
                    && !value.Contains("MediaBrowser Token=", StringComparison.OrdinalIgnoreCase),
                    "export.secret-url", "Sensitive token URL/header string cannot be exported.");
            }
        }
        Visit(document.RootElement);
    }
}
