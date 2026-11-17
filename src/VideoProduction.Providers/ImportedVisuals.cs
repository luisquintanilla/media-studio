using VideoProduction;

namespace VideoProduction.Providers;

public static class ImportedVisuals
{
    public static void Validate(ImportedVisual[]? imports, int? sceneCount = null)
    {
        if (imports is null) return;
        Guard.Require(imports.Length is > 0 and <= 12 && imports.All(i => i is not null)
            && imports.Select(i => i.SceneId).Distinct(StringComparer.Ordinal).Count() == imports.Length,
            "generation.import", "Imported visuals require distinct bounded scene identities.");
        foreach (var item in imports)
        {
            Guard.Require(item.SceneId is { Length: 8 }
                && System.Text.RegularExpressions.Regex.IsMatch(item.SceneId, @"\Ascene-[0-9]{2}\z")
                && int.TryParse(item.SceneId.AsSpan(6), out var number) && number > 0
                && (sceneCount is null || number <= sceneCount)
                && item.Provenance is { Length: > 0 and <= 4096 } && !item.Provenance.Contains('\0')
                && item.License is { Length: > 0 and <= 4096 } && !item.License.Contains('\0')
                && !string.IsNullOrWhiteSpace(item.File) && item.Sha256 is { Length: 64 },
                "generation.import", "Imported media must bind an existing scene and explicit provenance/rights.");
            var path = PrivateFiles.LocalPath(item.File);
            Guard.Require(new[] { ".mp4", ".webm", ".mkv", ".png", ".jpg", ".jpeg" }
                .Contains(Path.GetExtension(path).ToLowerInvariant()), "generation.import", "Unsupported frozen visual format.");
            Guard.Require(File.Exists(path) && Files.ValidHash(item.Sha256) && Files.Hash(path) == item.Sha256,
                "generation.import-pin", "Imported visual bytes differ from the explicit reviewed pin.");
        }
    }

    public static Project Apply(Project project, string root, ImportedVisual[]? imports)
    {
        Validate(imports, project.Scenes.Length);
        if (imports is null) return project;
        foreach (var item in imports)
        {
            var scene = project.Scenes.SingleOrDefault(s => s.Id == item.SceneId);
            Guard.Require(scene is not null && scene.Visual is null && scene.Motion is null, "generation.import",
                "Imported footage must bind an existing scene without replacing an animated template silently.");
            var id = "import-" + item.SceneId;
            var relative = $"assets/imported/{id}{Path.GetExtension(item.File).ToLowerInvariant()}";
            var target = Files.Under(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(PrivateFiles.LocalPath(item.File), target, overwrite: false);
            Guard.Require(Files.Hash(target) == item.Sha256, "generation.import-pin", "Imported bytes changed during freezing.");
            project = project with {
                Assets = [.. project.Assets, new(id, relative, item.Sha256, item.Provenance, item.License)],
                Scenes = project.Scenes.Select(s => s.Id == item.SceneId ? s with {
                    Kind = SceneKind.Media,
                    VisualAssetId = id,
                    AllowHold = item.AllowHold,
                    VisualDescription = item.Provenance
                } : s).ToArray()
            };
        }
        return project;
    }
}
