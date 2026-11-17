using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoProduction;

public sealed record Region(double X, double Y, double Width, double Height);
public enum SpatialKind { Highlight, Dim, Callout, Zoom, Pointer, Click }
public sealed record SpatialEffect(SpatialKind Kind, long StartFrame, long EndFrame, string AnchorId,
    Region ExpectedRegion, string Text = "");
public sealed record FootageEdit(string SourceSha256, Rate SourceFps, long StartFrame, long EndFrame,
    string Reason, string? AnchorAssetId, SpatialEffect[] Effects, bool ReducedMotion = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TailHold? TailHold = null);
public sealed record TailHold(int Frames, string Reason);
public sealed record SceneTransition(int Frames, int OutgoingStaticFrames, int IncomingStaticFrames,
    int AudioRampSamples, string Reason);
public sealed record DomAnchor(string Id, string TargetIdentity, string TargetSha256, Region Region,
    int ViewportWidth, int ViewportHeight, long StartSourceFrame, long EndSourceFrame,
    long ObservationSourceFrame, string ScreenshotSha256, string LayoutSha256,
    string Event, double? ClickX = null, double? ClickY = null);
public sealed record AnchorLedger(int SchemaVersion, string SourceSha256, Rate SourceFps, int Width, int Height,
    DomAnchor[] Anchors, string TimingEvidence);
public sealed record Chapter(string Id, string SceneId, string Title);
public sealed record ChapterTime(string Id, string Title, long StartFrame, long EndFrame);
public sealed record SourceFrameMap(long OutputFrame, long SourceFrame, long SourcePts, int TimeBaseNumerator, int TimeBaseDenominator,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool AuthoredTailHold = false);
public sealed record SourceClock(string SourceSha256, Rate Fps, int TimeBaseNumerator, int TimeBaseDenominator,
    long[] PresentationTimestamps, int Width, int Height);

public static class Editing
{
    public static void ValidateRegion(Region region)
    {
        Guard.Require(new[] { region.X, region.Y, region.Width, region.Height }.All(double.IsFinite)
            && region.X >= 0 && region.Y >= 0 && region.Width > 0 && region.Height > 0
            && region.X + region.Width <= 1 && region.Y + region.Height <= 0.80,
            "edit.geometry", "Target must be finite, inside viewport, and above the bottom20% caption-safe area.");
    }

    public static long SourceFrame(long outputFrame, Rate outputFps, Rate sourceFps, long startFrame)
    {
        outputFps.Validate();
        sourceFps.Validate();
        Guard.Require(outputFrame >= 0 && startFrame >= 0, "edit.frame", "Negative source/output frame.");
        return checked(startFrame + Rate.Divide((System.Numerics.BigInteger)outputFrame * sourceFps.Numerator * outputFps.Denominator,
            (System.Numerics.BigInteger)sourceFps.Denominator * outputFps.Numerator, Rounding.Floor));
    }

    public static long AudioLength(Scene scene)
    {
        var narration = scene.Narration ?? throw new VideoException("narration.required", scene.Id);
        if (scene.AudioSelection is null) return narration.SampleCount;
        var selection = scene.AudioSelection;
        Guard.Require(selection.StartSample >= 0 && selection.EndSample > selection.StartSample
            && selection.EndSample <= narration.SampleCount && !string.IsNullOrWhiteSpace(selection.Reason),
            "edit.audio", "Audio selection must be explicit, nonempty, in range and documented.");
        foreach (var cue in narration.Cues)
            Guard.Require(!(cue.StartSample < selection.StartSample && cue.EndSample > selection.StartSample)
                && !(cue.StartSample < selection.EndSample && cue.EndSample > selection.EndSample),
                "edit.speech-cut", "Audio selection cuts through transcript evidence; select complete cues or supply reviewed boundaries.");
        Guard.Require(narration.Cues.Any(c => c.StartSample >= selection.StartSample && c.EndSample <= selection.EndSample),
            "edit.audio-empty", "Selected narration contains no complete transcript cue.");
        return selection.EndSample - selection.StartSample;
    }

    public static void Validate(Scene scene)
    {
        _ = AudioLength(scene);
        if (scene.Edit is not { } edit) return;
        Guard.Require(scene.Kind is SceneKind.Browser or SceneKind.Media, "edit.kind", "Source edits only apply to recorded/imported footage.");
        Guard.Require(Files.ValidHash(edit.SourceSha256) && edit.StartFrame >= 0 && edit.EndFrame > edit.StartFrame
            && !string.IsNullOrWhiteSpace(edit.Reason), "edit.range", "Source selection must bind a hash, half-open frame range and explicit editorial reason.");
        edit.SourceFps.Validate();
        if (edit.TailHold is { } hold)
            Guard.Require(hold.Frames is >= 1 and <= 300 && !string.IsNullOrWhiteSpace(hold.Reason),
                "edit.tail-hold", "Explicit repeated tail requires1..300output frames and a reason; not native elapsed time.");
        Guard.Require(edit.ReducedMotion, "edit.motion", "Only static reduced-motion focus is supported; animated zoom/pointer motion is not implemented.");
        Guard.Require(edit.Effects.Length <= 32 && (edit.Effects.Length == 0 || edit.AnchorAssetId is not null),
            "edit.anchors", "Spatial effects require a hashed anchor ledger asset; max32effects.");
        foreach (var effect in edit.Effects)
        {
            Guard.Require(effect.StartFrame >= 0 && effect.EndFrame > effect.StartFrame
                && Files.ValidId(effect.AnchorId) && effect.Text.Length <= 80
                && !effect.Text.Any(char.IsControl), "edit.effect", "Invalid spatial effect interval/anchor/text.");
            ValidateRegion(effect.ExpectedRegion);
            Guard.Require(effect.ExpectedRegion.Width * 1920 >= 6 && effect.ExpectedRegion.Height * 1080 >= 6,
                "edit.geometry", "Target is smaller than the encoded focus stroke.");
            if (effect.Kind == SpatialKind.Zoom)
                Guard.Require(effect.ExpectedRegion.Width >= 0.5 && effect.ExpectedRegion.Height >= 0.5,
                    "edit.zoom", "Zoom is bounded to at most2x; source region must be at least half width/height.");
            if (effect.Kind == SpatialKind.Callout)
                Guard.Require(!string.IsNullOrWhiteSpace(effect.Text), "edit.callout", "Callout text is required.");
        }
        var zooms = edit.Effects.Where(e => e.Kind == SpatialKind.Zoom).OrderBy(e => e.StartFrame).ToArray();
        Guard.Require(zooms.Zip(zooms.Skip(1)).All(p => p.First.EndFrame <= p.Second.StartFrame),
            "edit.zoom-overlap", "Overlapping zooms are unsupported.");
    }

    public static void ValidateAnchors(FootageEdit edit, AnchorLedger ledger, SourceClock clock, long outputFrames)
    {
        Guard.Require(ledger.SchemaVersion == 1 && ledger.SourceSha256 == edit.SourceSha256
            && ledger.SourceSha256 == clock.SourceSha256 && ledger.SourceFps == edit.SourceFps
            && clock.Fps == edit.SourceFps && ledger.Width == clock.Width && ledger.Height == clock.Height
            && clock.Width == 1920 && clock.Height == 1080 && edit.EndFrame <= clock.PresentationTimestamps.LongLength,
            "edit.stale-source", "Source hash/fps/viewport/range differs from anchor evidence; reacquire or review new anchors.");
        Guard.Require(ledger.Anchors.Select(a => a.Id).Distinct().Count() == ledger.Anchors.Length,
            "edit.anchor-duplicate", "Anchor IDs must be unique.");
        foreach (var effect in edit.Effects)
        {
            var anchor = ledger.Anchors.SingleOrDefault(a => a.Id == effect.AnchorId)
                ?? throw new VideoException("edit.anchor-missing", "Spatial target anchor does not exist.");
            ValidateRegion(anchor.Region);
            Guard.Require(anchor.ViewportWidth == 1920 && anchor.ViewportHeight == 1080
                && anchor.Region == effect.ExpectedRegion && Files.ValidHash(anchor.TargetSha256)
                && Files.HashText(anchor.TargetIdentity) == anchor.TargetSha256
                && Files.ValidHash(anchor.LayoutSha256) && Files.ValidHash(anchor.ScreenshotSha256),
                "edit.stale-layout", "DOM target identity, expected geometry or viewport changed.");
            Guard.Require(effect.EndFrame <= outputFrames, "edit.effect-overrun", "Spatial effect exceeds selected scene duration.");
            var first = MappedFrame(edit, effect.StartFrame).Frame;
            var last = MappedFrame(edit, effect.EndFrame - 1).Frame;
            Guard.Require(first >= anchor.StartSourceFrame && last < anchor.EndSourceFrame
                && anchor.ObservationSourceFrame >= anchor.StartSourceFrame && anchor.ObservationSourceFrame < anchor.EndSourceFrame,
                "edit.stale-target", "Effect extends outside observed stable DOM target interval.");
            if (effect.Kind is SpatialKind.Pointer or SpatialKind.Click)
                Guard.Require(anchor.Event == "click" && anchor.ClickX is { } x && anchor.ClickY is { } y
                    && double.IsFinite(x) && double.IsFinite(y) && x >= anchor.Region.X && x <= anchor.Region.X + anchor.Region.Width
                    && y >= anchor.Region.Y && y <= anchor.Region.Y + anchor.Region.Height,
                    "edit.click-evidence", "Pointer/click emphasis must bind an actual observed click inside the target.");
            var crops = edit.Effects.Where(e => e.Kind == SpatialKind.Zoom && e.StartFrame < effect.EndFrame
                && e.EndFrame > effect.StartFrame).Select(e => e.ExpectedRegion)
                .Prepend(new Region(0, 0, 1, 1));
            if (effect.Kind == SpatialKind.Zoom) continue;
            foreach (var crop in crops)
            {
                var transformed = new Region((anchor.Region.X - crop.X) / crop.Width,
                    (anchor.Region.Y - crop.Y) / crop.Height, anchor.Region.Width / crop.Width, anchor.Region.Height / crop.Height);
                ValidateRegion(transformed);
                if (effect.Kind is SpatialKind.Pointer or SpatialKind.Click)
                {
                    var pixelX = (anchor.ClickX!.Value - crop.X) / crop.Width * 1920;
                    var pixelY = (anchor.ClickY!.Value - crop.Y) / crop.Height * 1080;
                    var radius = effect.Kind == SpatialKind.Click ? 26 : 13;
                    Guard.Require(pixelX >= radius && pixelX <= 1920 - radius && pixelY >= radius && pixelY <= 864 - radius,
                        "edit.click-geometry", "Click emphasis would extend outside the encoded/caption-safe viewport.");
                }
            }
        }
    }

    public static SourceFrameMap[] Map(FootageEdit edit, SourceClock clock, long outputFrames)
    {
        Guard.Require(outputFrames > 0 && outputFrames <= 18000, "edit.duration", "Output frame bound exceeded.");
        Guard.Require(edit.StartFrame >= 0 && edit.EndFrame <= clock.PresentationTimestamps.LongLength
            && edit.EndFrame > edit.StartFrame && SelectedFrames(edit) > 0,
            "edit.stale-source", "Selected range must contain actual source frames and a nonempty output duration.");
        if (edit.TailHold is { } hold)
            Guard.Require(hold.Frames is >= 1 and <= 300 && !string.IsNullOrWhiteSpace(hold.Reason)
                && outputFrames == SelectedFrames(edit) + hold.Frames,
                "edit.tail-duration", "Explicit tail hold must be rendered exactly, not silently clipped or extended.");
        var result = new SourceFrameMap[outputFrames];
        for (long i = 0; i < outputFrames; i++)
        {
            var (frame, held) = MappedFrame(edit, i);
            Guard.Require(frame < edit.EndFrame && frame < clock.PresentationTimestamps.LongLength,
                "edit.underlength", "Edited visual cannot silently hold beyond the selected source frame range.");
            result[i] = new SourceFrameMap(i, frame, clock.PresentationTimestamps[frame],
                clock.TimeBaseNumerator, clock.TimeBaseDenominator, held);
        }
        return result;
    }

    public static long SelectedFrames(FootageEdit edit) => Rate.Divide(
        (System.Numerics.BigInteger)(edit.EndFrame - edit.StartFrame) * edit.SourceFps.Denominator * 30,
        edit.SourceFps.Numerator, Rounding.Floor);

    private static (long Frame, bool Held) MappedFrame(FootageEdit edit, long outputFrame)
    {
        var selected = SelectedFrames(edit);
        if (outputFrame >= selected && edit.TailHold is { } hold && outputFrame < selected + hold.Frames)
            return (edit.EndFrame - 1, true);
        return (SourceFrame(outputFrame, new Rate(30, 1), edit.SourceFps, edit.StartFrame), false);
    }

    public static ChapterTime[] Chapters(Project project, Timeline timeline)
    {
        var chapters = project.Chapters ?? [];
        Guard.Require(chapters.Select(c => c.Id).Distinct().Count() == chapters.Length
            && chapters.Select(c => c.SceneId).Distinct().Count() == chapters.Length, "chapter.duplicate", "Chapter IDs and start scenes must be unique.");
        var ordered = chapters.Select(c =>
        {
            Guard.Require(Files.ValidId(c.Id) && !string.IsNullOrWhiteSpace(c.Title) && c.Title.Length <= 100
                && !c.Title.Any(char.IsControl), "chapter.fields", "Invalid chapter identity/title.");
            var scene = timeline.Scenes.SingleOrDefault(s => s.Id == c.SceneId)
                ?? throw new VideoException("chapter.scene", "Chapter references a missing scene.");
            return (Chapter: c, scene.StartFrame);
        }).OrderBy(c => c.StartFrame).ToArray();
        return ordered.Select((c, i) => new ChapterTime(c.Chapter.Id, c.Chapter.Title, c.StartFrame,
            i + 1 < ordered.Length ? ordered[i + 1].StartFrame : timeline.TotalFrames)).ToArray();
    }

    public static string ChapterMetadata(ChapterTime[] chapters) => ";FFMETADATA1\n"
        + string.Join("", chapters.Select(c => $"[CHAPTER]\nTIMEBASE=1/30\nSTART={c.StartFrame.ToString(CultureInfo.InvariantCulture)}\n"
            + $"END={c.EndFrame.ToString(CultureInfo.InvariantCulture)}\ntitle={c.Title.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("=", "\\=", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal).Replace("#", "\\#", StringComparison.Ordinal)}\n"));

    public static async Task<SourceClock> ProbeClock(Tools tools, string file, CancellationToken ct)
    {
        var result = await Processes.Run(tools.Ffprobe, ["-v", "error", "-protocol_whitelist", "file,pipe",
            "-select_streams", "v:0", "-show_streams", "-show_frames", "-show_entries",
            "stream=width,height,avg_frame_rate,time_base:frame=best_effort_timestamp", "-of", "json", file],
            Path.GetDirectoryName(file)!, ct, 300, 50_000_000);
        using var json = JsonDocument.Parse(result.Stdout);
        var stream = json.RootElement.GetProperty("streams")[0];
        var fps = stream.GetProperty("avg_frame_rate").GetString()!.Split('/');
        var tb = stream.GetProperty("time_base").GetString()!.Split('/');
        var pts = json.RootElement.GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("best_effort_timestamp").GetInt64()).ToArray();
        Guard.Require(pts.Length > 0 && pts[0] >= 0 && pts.Zip(pts.Skip(1)).All(p => p.First < p.Second),
            "edit.pts", "Source PTS must be nonnegative and strictly increasing.");
        var rate = new Rate(int.Parse(fps[0], CultureInfo.InvariantCulture), int.Parse(fps[1], CultureInfo.InvariantCulture));
        var clock = new SourceClock(Files.Hash(file), rate, int.Parse(tb[0], CultureInfo.InvariantCulture),
            int.Parse(tb[1], CultureInfo.InvariantCulture), pts, stream.GetProperty("width").GetInt32(), stream.GetProperty("height").GetInt32());
        // V1 deliberately refuses VFR edits rather than inventing a uniform source clock.
        var first = pts[0];
        for (var i = 0; i < pts.Length; i++)
            Guard.Require(Math.Abs((pts[i] - first) * (double)clock.TimeBaseNumerator / clock.TimeBaseDenominator
                - (double)i * rate.Denominator / rate.Numerator) <= (double)clock.TimeBaseNumerator / clock.TimeBaseDenominator + 0.000001,
                "edit.vfr", "VFR source editing is unsupported; provide an explicit separately normalized take with provenance.");
        return clock;
    }
}
