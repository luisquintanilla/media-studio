namespace VideoProduction;

public enum VisualPreset { ArchitectureFlowEmphasis, ProgressiveCodeFocus, BeforeAfterComparison }
public sealed record VisualAnchor(string Id, int StartLine, int EndLine);
public sealed record VisualReveal(string TargetId, long StartFrame, long EndFrame);
public sealed record VisualTreatment(VisualPreset Preset, VisualAnchor[] Anchors, VisualReveal[] Reveals);

public static class Visuals
{
    // Lines are 1-based inclusive; reveal intervals are scene-local, half-open 30fps frames.
    // An anchor stays visible after its reveal; only its emphasis ends at EndFrame.
    public static void Validate(VisualTreatment? visual, SceneKind kind, string text, long? frames = null)
    {
        if (visual is null) return;
        Guard.Require(Enum.IsDefined(visual.Preset)
            && (visual.Preset == VisualPreset.ArchitectureFlowEmphasis && kind == SceneKind.Diagram
                || visual.Preset == VisualPreset.ProgressiveCodeFocus && kind == SceneKind.Code
                || visual.Preset == VisualPreset.BeforeAfterComparison && kind is SceneKind.Code or SceneKind.Diagram),
            "visual.preset", "Unknown preset or incompatible template kind.");
        var lines = text.Split('\n');
        var comparison = visual.Preset == VisualPreset.BeforeAfterComparison;
        var architecture = visual.Preset == VisualPreset.ArchitectureFlowEmphasis;
        Guard.Require(lines.Length <= (architecture ? 6 : comparison ? 20 : 14)
            && lines.All(l => l.Length <= (architecture ? 48 : comparison ? 42 : 88)
                && !l.Any(c => char.IsControl(c) || char.IsSurrogate(c))),
            "visual.overflow", "Visual text exceeds the preset's bounded lines/columns or contains unsupported control/surrogate characters.");
        Guard.Require(visual.Anchors is { Length: > 0 and <= 8 }
            && (!architecture || visual.Anchors.Length is >= 2 and <= 6)
            && (!comparison || visual.Anchors.Length == 2),
            "visual.anchors", "Expected bounded stable line anchors (2..6 flow nodes; exactly two comparison panels).");
        var anchors = visual.Anchors!;
        Guard.Require(anchors.All(a => a is not null && Files.ValidId(a.Id)
            && a.StartLine >= 1 && a.EndLine >= a.StartLine && a.EndLine <= lines.Length)
            && anchors.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == anchors.Length,
            "visual.anchors", "Anchor IDs must be unique stable lowercase IDs with valid inclusive line ranges.");
        var previousLine = 0;
        foreach (var anchor in anchors)
        {
            Guard.Require(anchor.StartLine > previousLine
                && (!architecture || anchor.StartLine == anchor.EndLine)
                && (!comparison || anchor.EndLine - anchor.StartLine < 10),
                "visual.anchors", "Anchors must be in text order, nonoverlapping, and within preset size bounds.");
            if (architecture || comparison)
                Guard.Require(anchor.StartLine == previousLine + 1, "visual.anchors", "Flow/comparison anchors must cover every line without gaps.");
            previousLine = anchor.EndLine;
        }
        Guard.Require(!(architecture || comparison) || previousLine == lines.Length,
            "visual.anchors", "Flow/comparison anchors must cover all text.");
        Guard.Require(visual.Reveals is not null && visual.Reveals.Length == anchors.Length,
            "visual.reveal", "Each anchor needs exactly one ordered reveal interval.");
        long previousEnd = 0;
        for (var i = 0; i < anchors.Length; i++)
        {
            var reveal = visual.Reveals![i];
            Guard.Require(reveal is not null && reveal.TargetId == anchors[i].Id
                && reveal.StartFrame >= previousEnd && (i != 0 || reveal.StartFrame == 0)
                && reveal.EndFrame > reveal.StartFrame && reveal.EndFrame <= 18_000,
                "visual.reveal", "Reveals must target anchors in order, begin at frame zero, and use nonoverlapping bounded half-open intervals.");
            Guard.Require(frames is null || reveal!.EndFrame <= frames, "visual.overrun", "Visual reveal exceeds resolved scene duration.");
            previousEnd = reveal!.EndFrame;
        }
    }
}
