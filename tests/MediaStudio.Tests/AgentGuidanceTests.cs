using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace MediaStudio.Tests;

// Keeps the coding-agent guidance usable: skills load in every agent (Agent Skills frontmatter rules),
// every skill is discoverable from AGENTS.md and docs/coding-agents.md, and relative links resolve.
public sealed partial class AgentGuidanceTests
{
    private static readonly string Root = TestPaths.RepoRoot;
    private static readonly string SkillsDirectory = Path.Combine(Root, ".github", "skills");

    public static TheoryData<string> Skills()
    {
        var data = new TheoryData<string>();
        foreach (var directory in Directory.GetDirectories(SkillsDirectory).Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(directory));
        return data;
    }

    [Theory]
    [MemberData(nameof(Skills))]
    public void Skill_frontmatter_follows_the_agent_skills_rules(string folder)
    {
        var path = Path.Combine(SkillsDirectory, folder, "SKILL.md");
        Assert.True(File.Exists(path), $"{folder} has no SKILL.md");

        var frontmatter = ReadFrontmatter(File.ReadAllText(path));
        Assert.True(frontmatter.TryGetValue("name", out var name), $"{folder}: frontmatter has no name");
        Assert.Equal(folder, name);
        Assert.True(name.Length <= 64, $"{folder}: name is longer than 64 characters");
        Assert.Matches(SkillName(), name);

        Assert.True(frontmatter.TryGetValue("description", out var description), $"{folder}: frontmatter has no description");
        Assert.False(string.IsNullOrWhiteSpace(description), $"{folder}: description is empty");
        Assert.True(description.Length <= 1024, $"{folder}: description is {description.Length} characters (max 1024)");
    }

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("docs/coding-agents.md")]
    public void Agent_entry_points_list_every_media_studio_skill(string file)
    {
        var text = File.ReadAllText(Path.Combine(Root, file));
        var missing = Directory.GetDirectories(SkillsDirectory)
            .Select(Path.GetFileName)
            .Where(name => name!.StartsWith("media-studio-", StringComparison.Ordinal) && !text.Contains(name, StringComparison.Ordinal))
            .ToList();
        Assert.True(missing.Count == 0, $"{file} does not mention: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Relative_links_in_agent_guidance_and_docs_resolve()
    {
        var files = new List<string>
        {
            Path.Combine(Root, "AGENTS.md"),
            Path.Combine(Root, "README.md"),
            Path.Combine(Root, "CONTRIBUTING.md"),
            Path.Combine(Root, ".github", "copilot-instructions.md"),
        };
        files.AddRange(Directory.GetFiles(Path.Combine(Root, "docs"), "*.md"));
        files.AddRange(Directory.GetFiles(SkillsDirectory, "*.md", SearchOption.AllDirectories));

        var broken = new List<string>();
        foreach (var file in files)
        {
            foreach (var target in RelativeLinks(File.ReadAllText(file)))
            {
                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, Uri.UnescapeDataString(target)));
                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                    broken.Add($"{Path.GetRelativePath(Root, file)} -> {target}");
            }
        }

        Assert.True(broken.Count == 0, "Broken relative links:\n" + string.Join("\n", broken));
    }

    private static Dictionary<string, string> ReadFrontmatter(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.True(lines.Length > 2 && lines[0] == "---", "SKILL.md must start with YAML frontmatter");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < lines.Length && lines[i] != "---"; i++)
        {
            var match = FrontmatterKey().Match(lines[i]);
            if (!match.Success) continue;

            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value.Trim();
            if (value is ">" or ">-" or "|" or "|-")
            {
                var block = new StringBuilder();
                while (i + 1 < lines.Length && (lines[i + 1].StartsWith(' ') || lines[i + 1].Length == 0) && lines[i + 1] != "---")
                    block.Append(lines[++i].Trim()).Append(' ');
                value = block.ToString().Trim();
            }
            else if (value.Length >= 2 && (value[0] is '"' or '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }
            values[key] = value;
        }
        return values;
    }

    private static IEnumerable<string> RelativeLinks(string markdown)
    {
        var inFence = false;
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
            if (inFence) continue;

            var line = CodeSpan().Replace(raw, string.Empty);
            foreach (Match match in MarkdownLink().Matches(line))
            {
                var target = match.Groups["target"].Value.Trim('<', '>');
                if (target.Length == 0 || target[0] == '#' || target.Contains("://", StringComparison.Ordinal) ||
                    target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var hash = target.IndexOf('#', StringComparison.Ordinal);
                yield return hash >= 0 ? target[..hash] : target;
            }
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SkillName();

    [GeneratedRegex(@"^(?<key>[A-Za-z][A-Za-z0-9_-]*):(?<value>.*)$")]
    private static partial Regex FrontmatterKey();

    [GeneratedRegex("`[^`]*`")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"\]\((?<target>[^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex MarkdownLink();
}
