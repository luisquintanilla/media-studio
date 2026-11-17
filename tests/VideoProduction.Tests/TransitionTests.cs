using VideoProduction;

internal static class TransitionTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (VideoException error) { Check(error.Code == code, error.Code + " instead of " + code); }
        }
        var hash = Files.HashText("fixture");
        var audio = new Narration("audio", 48000, 48000, "Speech.",
            [new Cue(4800, 43200, "Speech.", Evidence.TakeWindow)]);
        var first = new Scene("first", SceneKind.Title, "First", "", "Speech.", TimingMaster.Fixed,
            60, false, null, audio, []);
        var second = first with { Id = "second", TransitionIn = new SceneTransition(12, 6, 6, 240, "Settled cards") };
        var project = new Project(1, "transitions", 1920, 1080, new Rate(30, 1), [], [first, second],
            [new Chapter("first", "first", "First"), new Chapter("second", "second", "Second")]);
        var timeline = Validation.Resolve(project, hash, _ => null);
        Check(timeline.TotalFrames == 132 && timeline.Scenes[0].StartFrame == 0
            && timeline.Scenes[1].StartFrame == 72 && timeline.Scenes[1].TransitionInFrames == 12,
            "Inserted bridge retains both complete scenes and exact frame offsets");
        Check(timeline.Fps.Samples(timeline.TotalFrames, 48000, Rounding.Nearest) == 211200,
            "Inserted bridge retains exact48k sample accounting");
        var captions = Captions.Project(project, timeline);
        Check(captions[0].StartMilliseconds == 100 && captions[1].StartMilliseconds == 2500
            && captions[1].EndMilliseconds == 3300, "Incoming cues shift by exact400ms bridge without edits");
        var chapters = Editing.Chapters(project, timeline);
        Check(chapters[0].EndFrame == 72 && chapters[1].StartFrame == 72 && chapters[1].EndFrame == 132,
            "Chapters begin on incoming scene, not inside bridge");
        var maps = Transitions.Maps(project, timeline);
        Check(maps.Length == 12 && maps[0] == new TransitionFrame(60, "first", 59, "second", 0, 1, 13)
            && maps[^1].TimelineFrame == 71 && maps[^1].IncomingWeightNumerator == 12,
            "Every bridge frame declares two repeated original scene frames and rational dissolve weight");
        Reject(() => Transitions.Maps(project, timeline with {
            Scenes = [
            timeline.Scenes[0], timeline.Scenes[1] with { FrameCount = 5 }]
        }), "transition.handles");
        var hard = project with { Scenes = [first, second with { TransitionIn = null }] };
        var hardTimeline = Validation.Resolve(hard, hash, _ => null);
        Check(hardTimeline.TotalFrames == 120 && hardTimeline.Scenes[1].StartFrame == 60
            && Transitions.Maps(hard, hardTimeline).Length == 0,
            "Absent field retains original hard-cut clocks");
        Check(!Json.Text(hard).Contains("transitionIn", StringComparison.Ordinal)
            && !Json.Text(hardTimeline).Contains("transitionInFrames", StringComparison.Ordinal),
            "Absent additive fields preserve existing source/timeline composition hashes");
        Check(Transitions.AudioFilter(0, 0, 96000) == "asetpts=N/SR/TB", "Absent ramps preserve default audio join filter");
        Check(Transitions.AudioFilter(240, 480, 96000).Contains("ss=95520:ns=480", StringComparison.Ordinal),
            "Boundary ramps use integer samples, not rounded milliseconds");
        var clock = new SourceClock(hash, new Rate(25, 1), 1, 1000, [0, 40, 80, 120, 160], 1920, 1080);
        var edit = new FootageEdit(hash, clock.Fps, 0, 5, "Whole source", null, [], TailHold: new TailHold(3, "Authored settle"));
        var map = Editing.Map(edit, clock, 9);
        Check(map.Length == 9 && map.Take(6).All(f => !f.AuthoredTailHold)
            && map.Skip(6).All(f => f.AuthoredTailHold && f.SourceFrame == 4 && f.SourcePts == 160),
            "Held tail repeats actual last selected frame with explicit provenance");
        Reject(() => Editing.Map(edit, clock, 8), "edit.tail-duration");
        Reject(() => Editing.Map(edit, clock, 10), "edit.tail-duration");
        Reject(() => Editing.Map(edit with { TailHold = new TailHold(-1, "Invalid") }, clock, 5), "edit.tail-duration");
        Reject(() => Editing.Map(edit with { TailHold = null }, clock, 9), "edit.underlength");
        Check(!Json.Text(edit with { TailHold = null }).Contains("tailHold", StringComparison.Ordinal)
            && !Json.Text(Editing.Map(edit with { TailHold = null }, clock, 6)).Contains("authoredTailHold", StringComparison.Ordinal),
            "Default edited-source hashes/maps omit new nullable/default fields");
        var region = new Region(0.1, 0.1, 0.2, 0.2);
        var anchor = new DomAnchor("target", "Observed", Files.HashText("Observed"), region, 1920, 1080,
            0, 4, 1, hash, hash, "observed");
        var annotated = edit with {
            AnchorAssetId = "anchors",
            Effects = [new SpatialEffect(SpatialKind.Highlight, 6, 9, "target", region)]
        };
        Reject(() => Editing.ValidateAnchors(annotated,
            new AnchorLedger(1, hash, clock.Fps, 1920, 1080, [anchor], "Fixture"), clock, 9), "edit.stale-target");
        Editing.ValidateAnchors(annotated,
            new AnchorLedger(1, hash, clock.Fps, 1920, 1080, [anchor with { EndSourceFrame = 5 }], "Fixture"), clock, 9);
        Check(true, "Held-frame annotation requires actual final-frame anchor coverage, never extended evidence");
        var heldScene = first with {
            Kind = SceneKind.Media,
            Edit = edit,
            Timing = TimingMaster.Action,
            Narration = audio with { SampleCount = 1000, Cues = [new Cue(0, 1000, "Speech.", Evidence.TakeWindow)] }
        };
        var heldProject = project with { Scenes = [heldScene], Chapters = null };
        Check(Validation.Resolve(heldProject, hash, _ => 9).TotalFrames == 9, "Action duration includes explicit held tail");
        Reject(() => Validation.Resolve(heldProject with { Scenes = [heldScene with { Timing = TimingMaster.Narration }] },
            hash, _ => 9), "edit.tail-duration");
        Reject(() => Validation.Validate(project with { Scenes = [second] }, ""), "transition.fields");
        foreach (var invalid in new[] {
            second.TransitionIn! with { Frames = 0 },
            second.TransitionIn! with { Frames = 31 },
            second.TransitionIn! with { OutgoingStaticFrames = 1 },
            second.TransitionIn! with { IncomingStaticFrames = 31 },
            second.TransitionIn! with { AudioRampSamples = 0 },
            second.TransitionIn! with { AudioRampSamples = 2401 },
            second.TransitionIn! with { Reason = " " } })
            Reject(() => Transitions.Validate(invalid), "transition.fields");
        var roundtrip = System.Text.Json.JsonSerializer.Deserialize<Project>(Json.Text(project), Json.Options)!;
        Check(roundtrip.Scenes[1].TransitionIn == second.TransitionIn, "Explicit transition authoring survives strict JSON round-trip");
        return count;
    }
}
