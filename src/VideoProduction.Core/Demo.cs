namespace VideoProduction;

public static class Demo
{
    /// <summary>Authors the synthetic demo. Without an explicit font it uses the bundled Inter font and its OFL license.</summary>
    public static string Create(string directory, string? fontFile = null, string? fontLicense = null)
    {
        Guard.Require(!Directory.Exists(directory), "demo.exists", "Demo directory must be new.");
        Guard.Require((fontFile is null) == (fontLicense is null), "demo.font",
            "Pass --font and --font-license together, or neither to use the bundled Inter font.");
        var bundled = fontFile is null;
        fontFile ??= BundledFont.RequireFontFile();
        fontLicense ??= BundledFont.LicenseFile;
        var fontName = bundled ? BundledFont.FileName : "font.ttf";
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "assets", "fonts"));
        File.Copy(fontFile, Path.Combine(directory, "assets", "fonts", fontName));
        File.Copy(fontLicense, Path.Combine(directory, "assets", "fonts", "OFL.txt"));
        var assets = new[]
        {
            new Asset("font", "assets/fonts/" + fontName, Files.Hash(fontFile),
                bundled ? BundledFont.Description : "Explicitly imported OFL font; frozen copy", "SIL Open Font License 1.1; adjacent OFL.txt")
        };
        Scene Make(string id, SceneKind kind, string title, string text, string script, bool hold = false,
            string? visual = null, Effect[]? effects = null) =>
            new(id, kind, title, text, script, TimingMaster.Narration, 0, hold, visual, null, effects ?? []);
        var project = new Project(1, "synthetic-coding-demo", 1920, 1080, new Rate(30, 1), assets,
        [
            Make("opening", SceneKind.Title, "Local, headless video production",
                "Real browser + real CLI\nFrozen narration + portable sources\nNo desktop capture or publishing",
                "A local demo, built from real evidence."),
            Make("counter", SceneKind.Browser, "Counter fixture", "", "Click increment. The counter becomes one.", hold: true),
            Make("sum", SceneKind.Terminal, "Actual noninteractive CLI output", "", "The real command adds one through five."),
            Make("code", SceneKind.Code, "Readable code, not an IDE recording",
                "int Sum(int[] values)\n{\n    return values.Sum();\n}",
                "This function returns the sum of the values.",
                effects: [new Effect(EffectKind.Highlight, 12, 36)]),
            Make("diagram", SceneKind.Diagram, "Evidence-backed assembly",
                "Capture\nNarration\nFrame compiler\nChecked movie",
                "Capture, narration, and frames form one movie.",
                effects: [new Effect(EffectKind.Arrow, 0, 30, "Frozen, hash-verified assets")]),
            Make("imported", SceneKind.Media, "Imported synthetic media", "",
                "Imported media stays in the source bundle.", hold: true, visual: "imported-card")
        ]);
        Json.Write(Path.Combine(directory, "project.json"), project);
        return Path.Combine(directory, "project.json");
    }

    public static void AttachImportedCard(string projectFile, string png)
    {
        var root = Path.GetDirectoryName(projectFile)!;
        var target = Files.Under(root, "assets/imported/card.png");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(png, target);
        var project = Json.Read<Project>(projectFile);
        Json.Write(projectFile, project with {
            Assets = project.Assets.Append(new Asset("imported-card",
            "assets/imported/card.png", Files.Hash(target),
            "Locally generated synthetic color-card PNG, imported as a frozen visual", "MIT; authored fixture")).ToArray()
        });
    }
}
