using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VideoProduction;
using VideoProduction.Providers;

var count = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    count++;
}
void Reject(Action action, string code)
{
    try { action(); throw new Exception("Expected " + code); }
    catch (VideoException error) { Check(error.Code == code, $"{code}, got {error.Code}"); }
}
void RejectJson<T>(string text)
{
    try { JsonSerializer.Deserialize<T>(text, Json.Options); throw new Exception("Expected strict JSON rejection"); }
    catch (JsonException) { count++; }
}
VisualTreatment Treatment(VisualPreset preset) => new(preset,
    [new("source", 1, 1), new("result", 2, 2)],
    [new("source", 0, 15), new("result", 30, 45)]);
var treatments = Enum.GetValues<VisualPreset>().Select(Treatment).ToArray();
var narrated = new Narration("audio", 48000, 96000, "Synthetic fixture.",
    [new Cue(0, 96000, "Synthetic fixture.", Evidence.TakeWindow)]);
var scenes = treatments.Select((visual, i) => new Scene($"scene-{i + 1:D2}", i == 1 ? SceneKind.Code : SceneKind.Diagram,
    "Trusted visual fixture", "Source\nResult", "Synthetic fixture.", TimingMaster.Narration,
    0, false, null, narrated, [], Visual: visual)).ToArray();
var project = new Project(1, "visual-fixture", 1920, 1080, new Rate(30, 1), [], scenes);
var oldScene = scenes[0] with { Id = "scene-04", Kind = SceneKind.Title, Visual = null };
Check(!Json.Text(oldScene).Contains("\"visual\"", StringComparison.Ordinal), "Absent visual preserves legacy serialized fields");
var oldOutline = new VideoOutline("Legacy", [new(TemplateKind.Title, "Title", "Text", "Script.")]);
Check(!Json.Text(oldOutline).Contains("\"visual\"", StringComparison.Ordinal), "Old outline bytes omit new optional field");
Check(Json.Text(JsonSerializer.Deserialize<Project>(Json.Text(project), Json.Options)) == Json.Text(project), "Typed project visual round-trip");
var outline = new VideoOutline("Trusted visuals", scenes.Select(s => new ProposedScene(
    s.Kind == SceneKind.Code ? TemplateKind.Code : TemplateKind.Diagram, s.Title, s.Text, s.Script, Visual: s.Visual)).ToArray());
ChatVideoPlanner.Validate(outline, new());
Check(Json.Text(JsonSerializer.Deserialize<VideoOutline>(Json.Text(outline), Json.Options)) == Json.Text(outline), "Strict authored outline visual round-trip");
foreach (var scene in scenes)
{
    Visuals.Validate(scene.Visual, scene.Kind, scene.Text, 60);
    Check(Validation.Resolve(project with { Scenes = [scene] }, "fixture", _ => null).TotalFrames == 60, "Valid visual resolves on narration clock");
    Reject(() => Visuals.Validate(scene.Visual, scene.Kind, scene.Text, 44), "visual.overrun");
    Reject(() => Visuals.Validate(scene.Visual! with { Preset = (VisualPreset)999 }, scene.Kind, scene.Text), "visual.preset");
    Reject(() => Visuals.Validate(scene.Visual, SceneKind.Media, scene.Text), "visual.preset");
    Reject(() => Visuals.Validate(scene.Visual! with { Anchors = [] }, scene.Kind, scene.Text), "visual.anchors");
    Reject(() => Visuals.Validate(scene.Visual! with { Reveals = [] }, scene.Kind, scene.Text), "visual.reveal");
    Reject(() => Visuals.Validate(scene.Visual! with { Reveals = [new("source", 1, 15), new("result", 30, 45)] }, scene.Kind, scene.Text), "visual.reveal");
    Reject(() => Visuals.Validate(scene.Visual! with { Reveals = [new("source", 0, 15), new("missing", 30, 45)] }, scene.Kind, scene.Text), "visual.reveal");
    Reject(() => Visuals.Validate(scene.Visual! with { Reveals = [new("source", 0, 31), new("result", 30, 45)] }, scene.Kind, scene.Text), "visual.reveal");
    Reject(() => Visuals.Validate(scene.Visual! with { Reveals = [new("source", 0, 15), new("result", 30, long.MaxValue)] }, scene.Kind, scene.Text), "visual.reveal");
    Reject(() => Visuals.Validate(scene.Visual! with { Anchors = [new("source", 1, 1), new("source", 2, 2)] }, scene.Kind, scene.Text), "visual.anchors");
    Reject(() => Visuals.Validate(scene.Visual! with { Anchors = [new("source", 0, 1), new("result", 2, 2)] }, scene.Kind, scene.Text), "visual.anchors");
    Reject(() => Visuals.Validate(scene.Visual! with { Anchors = [new("source", 1, 2), new("result", 2, 2)] }, scene.Kind, scene.Text), "visual.anchors");
    Reject(() => Visuals.Validate(scene.Visual, scene.Kind, "Source\t\nResult"), "visual.overflow");
    Reject(() => Visuals.Validate(scene.Visual, scene.Kind, "Source\u0000\nResult"), "visual.overflow");
    Reject(() => Visuals.Validate(scene.Visual, scene.Kind, "Source\ud83d\ude00\nResult"), "visual.overflow");
    Reject(() => Visuals.Validate(scene.Visual, scene.Kind, new string('x', 89) + "\nResult"), "visual.overflow");
    Reject(() => Validation.Resolve(project with {
        Scenes = [scene with
        { Visual = scene.Visual! with { Reveals = [new("source", 0, 15), new("result", 30, 61)] } }]
    }, "fixture", _ => null), "visual.overrun");
}
Reject(() => ChatVideoPlanner.Validate(outline with { Scenes = [outline.Scenes[0] with { IllustrationPrompt = "Image" }] }, new()), "visual.preset");
RejectJson<VisualTreatment>("""{"preset":"untrusted","anchors":[],"reveals":[]}""");
RejectJson<VisualTreatment>("""{"preset":1,"anchors":[],"reveals":[]}""");
RejectJson<VisualTreatment>("""{"preset":"progressiveCodeFocus","anchors":[],"reveals":[],"javascript":"evil"}""");
RejectJson<VisualTreatment>("""{"preset":"progressiveCodeFocus","anchors":[{"id":"x","startLine":1,"endLine":1,"html":"evil"}],"reveals":[]}""");
RejectJson<VisualTreatment>("""{"preset":"progressiveCodeFocus","anchors":[],"reveals":[{"targetId":"x","startFrame":0.5,"endFrame":1}]}""");
RejectJson<VisualTreatment>("""{"preset":"progressiveCodeFocus","anchors":[]}""");
foreach (var (preset, lines, columns) in new[]
{
    (VisualPreset.ArchitectureFlowEmphasis, 6, 48),
    (VisualPreset.ProgressiveCodeFocus, 14, 88),
    (VisualPreset.BeforeAfterComparison, 20, 42)
})
{
    var anchors = preset == VisualPreset.ArchitectureFlowEmphasis
        ? Enumerable.Range(1, lines).Select(i => new VisualAnchor($"node-{i}", i, i)).ToArray()
        : [new VisualAnchor("source", 1, lines / 2), new VisualAnchor("result", lines / 2 + 1, lines)];
    var treatment = new VisualTreatment(preset, anchors,
        anchors.Select((a, i) => new VisualReveal(a.Id, i, i + 1)).ToArray());
    var text = string.Join('\n', Enumerable.Repeat(new string('W', columns), lines));
    var kind = preset == VisualPreset.ProgressiveCodeFocus ? SceneKind.Code : SceneKind.Diagram;
    Visuals.Validate(treatment, kind, text, 60);
    Check(true, "Exact preset maximum accepted");
    Reject(() => Visuals.Validate(treatment, kind, text + "\nExtra"), "visual.overflow");
    Reject(() => Visuals.Validate(treatment, kind, "W" + text), "visual.overflow");
}

var source = Directory.GetCurrentDirectory();
var motion = JsonSerializer.Deserialize<MotionVisual>(File.ReadAllText(Path.Combine(source, "fixtures", "rain-motion.json")), Json.Options)!;
MotionVisuals.Validate(motion, SceneKind.Diagram, frames: 60);
Check(MotionVisuals.At(motion, 100).Single(p => p.Id == "rain").Y == 490, "Motion final rain pose");
Check(MotionVisuals.At(motion, 60).Single(p => p.Id == "droplet-left").X == 675, "Motion segment interpolation");
Check(MotionVisuals.Caption(motion, 100) == motion.Stages[^1].Caption, "Motion final caption");
Reject(() => MotionVisuals.Validate(motion, SceneKind.Title), "motion.compatibility");
Reject(() => MotionVisuals.Validate(motion, SceneKind.Diagram, incompatible: true), "motion.compatibility");
Reject(() => MotionVisuals.Validate(motion, SceneKind.Diagram, frames: 59), "motion.duration");
Reject(() => MotionVisuals.At(motion, double.NaN), "motion.seek");
Reject(() => MotionVisuals.Caption(motion, -1), "motion.seek");
Reject(() => MotionVisuals.Validate(motion with { Entities = [] }, SceneKind.Diagram), "motion.bounds");
Reject(() => MotionVisuals.Validate(motion with { Entities = [motion.Entities[0], motion.Entities[0]] }, SceneKind.Diagram), "motion.entity");
Reject(() => MotionVisuals.Validate(motion with {
    Entities = motion.Entities.Select(e =>
    e with { Keys = e.Keys.Select(k => k with { Opacity = 0 }).ToArray() }).ToArray()
}, SceneKind.Diagram), "motion.change");
RejectJson<MotionVisual>("""{"entities":[],"stages":[],"javascript":"evil"}""");
RejectJson<MotionKey>("""{"percent":0,"x":1,"y":2,"opacity":1,"html":"evil"}""");
Check(Json.Text(JsonSerializer.Deserialize<MotionVisual>(Json.Text(motion), Json.Options)) == Json.Text(motion), "Motion typed round-trip");
Check(!Json.Text(oldScene).Contains("\"motion\"", StringComparison.Ordinal) && !Json.Text(oldOutline).Contains("\"motion\"", StringComparison.Ordinal),
    "Motion absence preserves legacy serialized identities");
var motionScene = oldScene with { Kind = SceneKind.Diagram, Motion = motion };
Check(Validation.Resolve(project with { Scenes = [motionScene] }, "fixture", _ => null).TotalFrames == 60,
    "Motion binds actual two-second narration clock");
Reject(() => MediaJudgements.EligibleVisuals(new(TemplateKind.Diagram, "Motion", "", "Explain.", Motion: motion)), "decision.motion");
if (args is ["--motion-poses", var posesFile])
    Json.Write(posesFile, Enumerable.Range(0, 101).Select(percent => new { percent, poses = MotionVisuals.At(motion, percent) }).ToArray());
using (var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "schemas", "project.schema.json"))))
    Check(schema.RootElement.GetProperty("$defs").GetProperty("scene").GetProperty("properties").TryGetProperty("visual", out _),
        "Project schema advertises optional visual");
using (var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "schemas", "video-outline.schema.json"))))
    Check(schema.RootElement.GetProperty("properties").GetProperty("scenes").GetProperty("items").GetProperty("properties").TryGetProperty("visual", out _),
        "Authoring schema advertises optional visual");

if (args is ["--render", var fontFile])
{
    var root = Path.Combine(source, "artifacts", "visual-integration-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var assets = Path.Combine(root, "frozen", "assets");
        Directory.CreateDirectory(assets);
        File.Copy(fontFile, Path.Combine(assets, "font.ttf"));
        File.WriteAllText(Path.Combine(assets, "OFL.txt"), "Ephemeral local system font test fixture; not a distributable font/license claim.");
        using (var writer = new BinaryWriter(File.Create(Path.Combine(assets, "tone.wav"))))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + 96000 * 2);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
            writer.Write((short)1); writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(96000 * 2);
            for (var i = 0; i < 96000; i++)
                writer.Write((short)(4000 * Math.Sin(i * 2 * Math.PI * 440 / 48000)));
        }
        var frozen = project with {
            Assets = [new("font", "assets/font.ttf", Files.Hash(Path.Combine(assets, "font.ttf")), "Local test font", "Test only"),
                new("audio", "assets/tone.wav", Files.Hash(Path.Combine(assets, "tone.wav")), "Synthetic test tone, not speech/model output", "Original test fixture")],
            Scenes = scenes.Select(s => s with { Visual = null }).Append(oldScene).Append(motionScene with { Id = "scene-05" }).ToArray()
        };
        outline = outline with {
            Scenes = [.. outline.Scenes, new(TemplateKind.Title, oldScene.Title, oldScene.Text, oldScene.Script),
            new(TemplateKind.Diagram, motionScene.Title, motionScene.Text, motionScene.Script, Motion: motion)]
        };
        var frozenFile = Path.Combine(root, "frozen", "project.json");
        Json.Write(frozenFile, frozen);
        Validation.Validate(frozen, Path.GetDirectoryName(frozenFile)!);
        var tools = new Tools(source, Files.FindExecutable("node"), Files.FindExecutable("ffmpeg"), Files.FindExecutable("ffprobe"));
        var toolsFile = Path.Combine(root, "tools.json");
        Json.Write(toolsFile, tools);
        PrivateFiles.Protect(toolsFile);
        var job = Path.Combine(root, "job");
        Directory.CreateDirectory(job);
        var config = new GenerationConfiguration(toolsFile, Path.Combine(assets, "font.ttf"), Files.Hash(Path.Combine(assets, "font.ttf")),
            Path.Combine(assets, "OFL.txt"), Files.Hash(Path.Combine(assets, "OFL.txt")),
            FrozenNarration: new(frozenFile, Files.Hash(frozenFile)));
        ProductionVideoPipeline.ValidatePrerequisites(config);
        Check(tools.Speech is null, "Explicit verified frozen narration needs no speech deployment or model assets");
        Reject(() => ProductionVideoPipeline.ValidatePrerequisites(config with {
            FrozenNarration = config.FrozenNarration! with { ProjectSha256 = new string('0', 64) }
        }), "generation.frozen-pin");
        using var planner = new OptionalChatPlanner(null);
        var pipeline = new ProductionVideoPipeline(planner, config);
        var artifacts = await pipeline.Execute(job, new("Synthetic bounded visual fixture", new(20, 5), outline),
            stage => Console.WriteLine("Visual integration: " + stage), CancellationToken.None);
        var actualFile = Files.Under(job, artifacts.Single(a => a.Kind == "project").Path);
        var actual = Json.Read<Project>(actualFile);
        for (var i = 0; i < treatments.Length; i++)
            Check(Json.Text(actual.Scenes[i].Visual) == Json.Text(treatments[i]), "Pipeline preserves exact authored treatment");
        Check(actual.Scenes[^2].Visual is null && actual.Scenes[^2].Motion is null, "Pipeline preserves old scene default");
        Check(Json.Text(actual.Scenes[^1].Motion) == Json.Text(motion), "Pipeline preserves exact authored motion");
        var production = new Production(actualFile, tools);
        var preview = await production.Render(true, CancellationToken.None);
        Check(File.Exists(Files.Under(production.Root, preview + "/movie.mp4")), "Actual preview render completes");
        var zip = Files.Under(job, artifacts.Single(a => a.Kind == "source-zip").Path);
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var relative in new[] { "src/VideoProduction.Core/Visuals.cs", "src/VideoProduction.Core/Contracts.cs",
                "src/VideoProduction.Core/Validation.cs", "src/VideoProduction.Core/MotionVisuals.cs",
                "workers/browser/worker.mjs", "workers/browser/visuals.mjs", "workers/browser/motion.mjs",
                "workers/browser/runtime/gsap.min.js", "workers/browser/runtime/hyperframe.runtime.iife.js",
                "workers/browser/runtime/NOTICE.txt", "workers/browser/runtime/HyperFrames-LICENSE.txt",
                "workers/browser/runtime/origins.json", "schemas/motion.schema.json",
                "schemas/project.schema.json", "schemas/video-outline.schema.json", "src/Media.Narration/Media.Narration.csproj",
                "src/Media.Audio/Media.Audio.csproj", ".github/skills/media-explanation/SKILL.md",
                "LICENSE", "SOURCE-ORIGINS.md" })
            {
                var entry = archive.GetEntry("renderer/" + relative) ?? throw new Exception("Missing exported " + relative);
                using var stream = entry.Open();
                Check(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream))
                    == Files.Hash(Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar))), "Export carries exact changed source: " + relative);
            }
        }
        var extracted = Path.Combine(root, "extracted");
        ZipFile.ExtractToDirectory(zip, extracted);
        var rerender = new Production(Path.Combine(extracted, "project", "project.json"),
            tools with { SourceRoot = Path.Combine(extracted, "renderer"), Speech = null });
        var rerendered = await rerender.Render(false, CancellationToken.None);
        var probe = await Media.Probe(tools.Ffprobe, Files.Under(rerender.Root, rerendered + "/movie.mp4"), CancellationToken.None);
        Check(probe.Frames == 300 && probe.Width == 1920 && probe.Height == 1080, "Extracted frozen bundle rerenders all 300 frames including motion without speech/model acquisition");
        Console.WriteLine("Visual integration: final + preview + extracted frozen rerender verified; no synthesis/ASR/model invoked.");
    }
    finally { Directory.Delete(root, recursive: true); }
}
Console.WriteLine($"PASS {count} visual contract/provider/export assertions.");
