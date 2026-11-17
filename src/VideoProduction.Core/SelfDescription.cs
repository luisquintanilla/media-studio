using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoProduction;

public sealed record Prerequisite(string Name, string Purpose, string Install, bool Optional = false);
public sealed record ToolManifest(
    [property: JsonPropertyName("smart_tool_format")] int Format,
    string Name, string Version, string Description,
    [property: JsonPropertyName("use_cases")] string[] UseCases,
    string[] Platforms, Prerequisite[] Requires);
public sealed record ManifestDocument(ToolManifest Manifest, string Body);

public static class SelfDescription
{
    public static ManifestDocument Manifest()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VideoProduction.SMART_TOOL.md")
            ?? throw new VideoException("manifest.resource", "Canonical embedded manifest is missing.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        var sections = text.Split("---\n", 3, StringSplitOptions.None);
        Guard.Require(sections.Length == 3 && sections[0].Length == 0, "manifest.format", "Expected canonical frontmatter and body.");
        // The canonical frontmatter uses JSON flow syntax, a strict subset of YAML.
        var manifest = JsonSerializer.Deserialize<ToolManifest>(sections[1], Json.Options)
            ?? throw new VideoException("manifest.format", "Canonical manifest is empty.");
        return new ManifestDocument(manifest, sections[2].Trim());
    }
}
