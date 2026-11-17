using VideoProduction;

internal static class EditingTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool test, string message) { if (!test) throw new Exception(message); count++; }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (VideoException ex) { Check(ex.Code == code, ex.Code + " instead of " + code); }
        }
        var region = new Region(0.1, 0.2, 0.2, 0.1);
        Editing.ValidateRegion(region);
        Reject(() => Editing.ValidateRegion(region with { X = double.NaN }), "edit.geometry");
        Reject(() => Editing.ValidateRegion(region with { X = -0.1 }), "edit.geometry");
        Reject(() => Editing.ValidateRegion(region with { Y = 0.75 }), "edit.geometry");
        Reject(() => Editing.ValidateRegion(region with { Width = 1 }), "edit.geometry");
        var hash = Files.HashText("source");
        var identity = "Jellyfin native card: actual id";
        var anchor = new DomAnchor("card", identity, Files.HashText(identity), region, 1920, 1080,
            10, 100, 20, hash, hash, "click", 0.15, 0.25);
        var ledger = new AnchorLedger(1, hash, new Rate(25, 1), 1920, 1080, [anchor], "test observations only");
        var clock = new SourceClock(hash, new Rate(25, 1), 1, 1000, Enumerable.Range(0, 120).Select(i => (long)i * 40).ToArray(), 1920, 1080);
        var effect = new SpatialEffect(SpatialKind.Highlight, 0, 60, "card", region);
        var edit = new FootageEdit(hash, new Rate(25, 1), 10, 110, "Authored source selection", "anchors", [effect]);
        Editing.ValidateAnchors(edit, ledger, clock, 60);
        var map = Editing.Map(edit, clock, 60);
        Check(map[0].SourceFrame == 10 && map[0].SourcePts == 400 && map[59].SourceFrame == 59
            && map[59].SourcePts == 2360, "25->30 duration-preserving floor source/PTS projection");
        for (var i = 1; i < map.Length; i++)
            Check(map[i].SourceFrame >= map[i - 1].SourceFrame && map[i].SourceFrame - map[i - 1].SourceFrame <= 1,
                "No speed-up/skipped source frames");
        Reject(() => Editing.ValidateAnchors(edit, ledger with { SourceSha256 = Files.HashText("changed") }, clock, 60), "edit.stale-source");
        Reject(() => Editing.ValidateAnchors(edit, ledger with { Width = 1280 }, clock, 60), "edit.stale-source");
        Reject(() => Editing.ValidateAnchors(edit, ledger with { Anchors = [anchor with { Region = region with { X = 0.11 } }] }, clock, 60), "edit.stale-layout");
        Reject(() => Editing.ValidateAnchors(edit, ledger with { Anchors = [anchor with { TargetIdentity = "different" }] }, clock, 60), "edit.stale-layout");
        Reject(() => Editing.ValidateAnchors(edit, ledger with { Anchors = [anchor with { EndSourceFrame = 30 }] }, clock, 60), "edit.stale-target");
        Reject(() => Editing.ValidateAnchors(edit with { Effects = [effect with { AnchorId = "wrong" }] }, ledger, clock, 60), "edit.anchor-missing");
        Reject(() => Editing.ValidateAnchors(edit with { Effects = [effect with { EndFrame = 61 }] }, ledger, clock, 60), "edit.effect-overrun");
        var click = edit with { Effects = [effect with { Kind = SpatialKind.Click }] };
        Editing.ValidateAnchors(click, ledger, clock, 60);
        Reject(() => Editing.ValidateAnchors(click, ledger with { Anchors = [anchor with { Event = "observed" }] }, clock, 60), "edit.click-evidence");
        Reject(() => Editing.ValidateAnchors(click, ledger with { Anchors = [anchor with { ClickX = 0.9 }] }, clock, 60), "edit.click-evidence");
        Reject(() => Editing.Map(edit with { EndFrame = 11 }, clock, 60), "edit.underlength");
        var narration = new Narration("audio", 48000, 192000, "First. Second.",
            [new Cue(0, 96000, "First.", Evidence.AsrSegment), new Cue(96000, 192000, "Second.", Evidence.AsrSegment)]);
        var scene = new Scene("selected", SceneKind.Media, "Media", "", "First. Second.", TimingMaster.Narration,
            0, false, "media", narration, [], null, edit, new SampleSelection(96000, 192000, "Select second complete spoken segment."));
        Check(Editing.AudioLength(scene) == 96000, "Exact non-destructive audio sample selection");
        Reject(() => Editing.AudioLength(scene with { AudioSelection = new SampleSelection(96001, 192000, "cuts a word") }), "edit.speech-cut");
        Reject(() => Editing.Validate(scene with { Edit = edit with { Effects = [effect with { Kind = SpatialKind.Zoom }] } }), "edit.zoom");
        var project = new Project(1, "edited", 1920, 1080, new Rate(30, 1), [], [scene],
            [new Chapter("second", scene.Id, "Second chapter")]);
        var timeline = Validation.Resolve(project, hash, _ => 100);
        Check(timeline.Scenes[0].AudioStartSample == 96000 && timeline.TotalFrames == 60, "Selected audio drives resolved clock");
        var cues = Captions.Project(project, timeline);
        Check(cues.Length == 1 && cues[0].Text == "Second." && cues[0].StartMilliseconds == 0 && cues[0].EndMilliseconds == 2000,
            "Trimmed speech captions rebase, never invent first cue");
        var chapters = Editing.Chapters(project, timeline);
        Check(chapters.Length == 1 && chapters[0].EndFrame == 60
            && Editing.ChapterMetadata(chapters).Contains("TIMEBASE=1/30", StringComparison.Ordinal), "Actual rational chapter metadata");
        Reject(() => Editing.Chapters(project with { Chapters = [new Chapter("bad", "missing", "Bad")] }, timeline), "chapter.scene");
        Reject(() => Editing.Chapters(project with { Chapters = [project.Chapters![0], project.Chapters[0]] }, timeline), "chapter.duplicate");
        foreach (var secret in new[] { """{"accessToken":"private"}""", """{"headers":{"a":"b"}}""",
            """{"capture":"http://127.0.0.1:12345/path?api_key=private"}""" })
        {
            try { ExportPrivacy.CheckJson(secret); throw new Exception("Export allowed auth data"); }
            catch (VideoException ex) { Check(ex.Code.StartsWith("export.secret", StringComparison.Ordinal), "Privacy rejects secrets"); }
        }
        ExportPrivacy.CheckJson("""{"query":"real search","result":{"id":"public-identity","name":"Lesson"}}""");
        Check(true, "Safe sanitized native evidence permitted");
        var capture = new JellyfinProfile(1, "http://127.0.0.1:12345", "server", "10.11.0", "pinned-client",
            "declared-plugin", hash, new string('a', 32), [new string('b', 32)], "real query",
            new JellyfinResult(new string('c', 32), "Actual item", "Episode"), "/Items",
            ["/Items", "/System/Info/Public", "/Users/Public", "/QuickConnect/Enabled"], ["/socket"], 2000,
            [new ArtifactHash("web/index.html", hash)]);
        JellyfinCapture.Validate(capture);
        Check(true, "Bounded reviewed native profile accepted");
        JellyfinCapture.Validate(capture with {
            AllowedApiPaths = ["/Items",
            $"/Items/{capture.ExpectedResult.Id}/Ancestors", $"/Items/{capture.ExpectedResult.Id}/ThemeMedia",
            $"/Items/{capture.ExpectedResult.Id}/Collections", $"/Items/{capture.ExpectedResult.Id}/Similar"]
        });
        Check(true, "Exact read-only native detail paths accepted");
        Reject(() => JellyfinCapture.Validate(capture with {
            AllowedApiPaths = ["/Items", $"/Items/{capture.ExpectedResult.Id}/Delete"]
        }), "capture.permissions");
        Reject(() => JellyfinCapture.Validate(capture with { Query = new string('a', 41) }), "capture.query");
        Reject(() => JellyfinCapture.Validate(capture with { Origin = "http://localhost:12345" }), "capture.origin");
        Reject(() => JellyfinCapture.Validate(capture with { WebAssets = null }), "capture.web-assets");
        Reject(() => JellyfinCapture.Validate(capture with { WebAssets = [new ArtifactHash("web/../secret", hash)] }), "capture.web-assets");
        Reject(() => JellyfinCapture.Validate(capture with { AllowedApiPaths = ["/Items", "/Plugins/Admin/Rebuild"] }), "capture.permissions");
        return count;
    }
}
