using VideoProduction;

internal static class DeliveryCaptionTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool test, string message) { if (!test) throw new Exception(message); count++; }
        var hash = Files.HashText("audio");
        var audio = new Asset("audio", "assets/audio.wav", hash, "test", "test");
        var narration = new Narration("audio", 48000, 48000, "One.", [new Cue(0, 48000, "One.", Evidence.TakeWindow)]);
        var a = new Scene("first", SceneKind.Title, "One", "Text", "One.", TimingMaster.Fixed, 60, false,
            null, narration, []);
        var b = a with { Id = "second", Title = "Two", Timing = TimingMaster.Narration };
        var project = new Project(1, "wholemaster-test", 1920, 1080, new Rate(30, 1), [audio], [a, b]);
        var composition = DeliveryCaptions.SourceComposition(project, ".");
        var timeline = Validation.Resolve(project, hash, _ => null);
        Check(timeline.TotalFrames == 90 && timeline.Scenes[1].StartFrame == 60, "Actual ordered pauses and source offsets");
        var deliverySpeech = narration with { SampleCount = 144000, Cues = [new Cue(96000, 144000, "Observed.", Evidence.AsrSegment)] };
        var binding = new DeliveryCaptionBinding(composition, DeliveryCaptions.TimelineComposition(composition, timeline),
            deliverySpeech, "Whole intended script.", "", hash, hash, hash, 144000, "unit fixture only");
        project = project with { DeliveryCaptions = binding };
        Check(DeliveryCaptions.SourceComposition(project, ".") == composition, "Binding does not recursively alter source fingerprint");
        var projected = Captions.Project(project, timeline);
        Check(projected is [{ StartMilliseconds: 2000, EndMilliseconds: 3000, Text: "Observed.", Evidence: Evidence.AsrSegment }],
            "Wholemaster acoustic cues replace local scene captions without scene-derived timing fiction");
        foreach (var changed in new[]
        {
            project with { Scenes = [b, a] },
            project with { Scenes = [a with { FixedFrames = 61 }, b] },
            project with { Scenes = [a with { Script = "Changed speech." }, b] },
            project with { Scenes = [a with { AudioSelection = new SampleSelection(0, 24000, "Authored trim") }, b] },
            project with { Assets = [audio with { Sha256 = Files.HashText("different source") }] },
            project with { Scenes = [a with { Effects = [new Effect(EffectKind.Fade, 0, 10)] }, b] }
        })
            Check(DeliveryCaptions.SourceComposition(changed, ".") != composition, "Changed composition must invalidate wholemaster transcript");
        try
        {
            Captions.Project(project, timeline with { Scenes = [timeline.Scenes[1], timeline.Scenes[0]] });
            throw new Exception("Changed ordered resolved timeline was accepted");
        }
        catch (VideoException ex) { Check(ex.Code == "delivery-transcript.timeline", "Changed scene clocks/offsets fail"); }
        return count;
    }
}
