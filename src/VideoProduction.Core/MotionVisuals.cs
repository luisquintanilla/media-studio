using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoProduction;

[JsonConverter(typeof(MotionShapeConverter))]
public enum MotionShape { Circle, Box, Cloud, Drop, Sun, Arrow }
public sealed class MotionShapeConverter() : JsonStringEnumConverter<MotionShape>(JsonNamingPolicy.CamelCase, false);
[JsonConverter(typeof(MotionToneConverter))]
public enum MotionTone { Water, Warm, Air, Neutral }
public sealed class MotionToneConverter() : JsonStringEnumConverter<MotionTone>(JsonNamingPolicy.CamelCase, false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MotionKey(int Percent, int X, int Y, double Opacity);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MotionEntity(string Id, MotionShape Shape, MotionTone Tone, string Label,
    int Width, int Height, MotionKey[] Keys);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MotionStage(int Percent, string Caption);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MotionVisual(MotionEntity[] Entities, MotionStage[] Stages);
public sealed record MotionPose(string Id, double X, double Y, double Opacity);

public static class MotionVisuals
{
    public static void Validate(MotionVisual? motion, SceneKind kind, bool incompatible = false, long? frames = null)
    {
        if (motion is null) return;
        Guard.Require(kind == SceneKind.Diagram && !incompatible, "motion.compatibility",
            "Animated mechanisms require a Diagram scene, without an illustration, imported visual or another visual preset.");
        Guard.Require(motion.Entities is { Length: > 0 and <= 20 } && motion.Stages is { Length: > 0 and <= 6 },
            "motion.bounds", "Use 1..20 named entities and 1..6 explanatory stages.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in motion.Entities!)
        {
            Guard.Require(entity is not null && Files.ValidId(entity.Id) && ids.Add(entity.Id)
                && Enum.IsDefined(entity.Shape) && Enum.IsDefined(entity.Tone) && Text(entity.Label, 28, empty: true)
                && entity.Width is >= 12 and <= 240 && entity.Height is >= 12 and <= 140
                && entity.Keys is { Length: >= 2 and <= 8 },
                "motion.entity", "Use unique stable IDs, supported shapes/colors, bounded labels/sizes and 2..8 poses.");
            var previous = -1;
            foreach (var key in entity!.Keys)
            {
                var margin = Math.Max(entity.Width / 2.0 + 12, entity.Label.Length * 9 + 12);
                Guard.Require(key is not null, "motion.pose", $"Entity '{entity.Id}' needs non-null poses.");
                Guard.Require(key!.Percent > previous && key.Percent is >= 0 and <= 100,
                    "motion.pose", $"Entity '{entity.Id}' needs strictly increasing pose percentages within 0..100.");
                Guard.Require(key.X >= margin && key.X <= 1000 - margin
                    && key.Y >= entity.Height / 2.0 + 45 && key.Y <= 580 - entity.Height / 2.0,
                    "motion.pose", FormattableString.Invariant(
                        $"Entity '{entity.Id}' at {key.Percent}% needs x within {margin}..{1000 - margin} and y within {entity.Height / 2.0 + 45}..{580 - entity.Height / 2.0}, even when invisible. Keep the pose inside these label-safe bounds; do not clamp it silently."));
                Guard.Require(double.IsFinite(key.Opacity) && key.Opacity is >= 0 and <= 1,
                    "motion.pose", $"Entity '{entity.Id}' at {key.Percent}% needs finite opacity within 0..1.");
                previous = key!.Percent;
            }
            Guard.Require(entity.Keys[0].Percent == 0 && entity.Keys[^1].Percent == 100,
                "motion.pose", "Each entity needs explicit initial and final poses at 0 and 100 percent.");
        }
        var previousStage = -1;
        foreach (var stage in motion.Stages!)
        {
            Guard.Require(stage is not null && stage.Percent > previousStage && stage.Percent is >= 0 and <= 95
                && Text(stage.Caption, 100), "motion.stage", "Stages need increasing percentages and readable explanatory captions.");
            previousStage = stage!.Percent;
        }
        Guard.Require(motion.Stages[0].Percent == 0, "motion.stage", "The first explanatory stage starts at zero.");
        Guard.Require(motion.Entities.Any(entity => entity.Keys.Zip(entity.Keys.Skip(1), (start, end) =>
            (start.Opacity > 0 || end.Opacity > 0) &&
            (start.X != end.X || start.Y != end.Y || start.Opacity != end.Opacity)).Any(changed => changed)),
            "motion.change", "An animated mechanism needs a visible change, not an all-static or invisible slide.");
        Guard.Require(frames is null || frames is >= 60 and <= 18000, "motion.duration",
            "An animated mechanism needs at least two measured seconds; no synthetic hold or narration truncation is added.");
    }

    private static bool Text(string? value, int maximum, bool empty = false) => value is not null
        && value.Length <= maximum && (empty || !string.IsNullOrWhiteSpace(value))
        && !value.Any(c => char.IsControl(c) || char.IsSurrogate(c));

    public static MotionPose[] At(MotionVisual motion, double percent)
    {
        Validate(motion, SceneKind.Diagram);
        Guard.Require(double.IsFinite(percent) && percent is >= 0 and <= 100, "motion.seek", "Seek within 0..100 percent.");
        return motion.Entities.Select(entity =>
        {
            var right = Array.FindIndex(entity.Keys, key => key.Percent >= percent);
            var end = entity.Keys[right];
            var start = entity.Keys[Math.Max(0, right - 1)];
            var fraction = end.Percent == start.Percent ? 0 : (percent - start.Percent) / (end.Percent - start.Percent);
            return new MotionPose(entity.Id, start.X + (end.X - start.X) * fraction,
                start.Y + (end.Y - start.Y) * fraction, start.Opacity + (end.Opacity - start.Opacity) * fraction);
        }).ToArray();
    }

    public static string Caption(MotionVisual motion, double percent)
    {
        Validate(motion, SceneKind.Diagram);
        Guard.Require(double.IsFinite(percent) && percent is >= 0 and <= 100, "motion.seek", "Seek within 0..100 percent.");
        return motion.Stages.Last(stage => stage.Percent <= percent).Caption;
    }
    public static string Color(MotionTone tone) => tone switch {
        MotionTone.Water => "#67c9ff",
        MotionTone.Warm => "#ffc56e",
        MotionTone.Air => "#7ee3c5",
        MotionTone.Neutral => "#d7dded",
        _ => throw new VideoException("motion.color", "Unknown schematic color.")
    };
    public static string Glyph(MotionShape shape) => shape switch {
        MotionShape.Circle => "<ellipse cx=\"0\" cy=\"0\" rx=\"50\" ry=\"50\"/>",
        MotionShape.Box => "<rect x=\"-50\" y=\"-50\" width=\"100\" height=\"100\" rx=\"8\"/>",
        MotionShape.Cloud => "<path d=\"M-38 22 C-50 0 -32 -22 -18 -14 C-10 -47 28 -44 30 -15 C49 -14 50 26 35 28 L-34 28 Z\"/>",
        MotionShape.Drop => "<path d=\"M0 -50 C-12 -23 -43 3 -34 25 C-24 58 24 58 34 25 C43 3 12 -23 0 -50 Z\"/>",
        MotionShape.Sun => "<circle r=\"28\"/><path fill=\"none\" stroke=\"currentColor\" stroke-width=\"6\" d=\"M0 -50 V-36 M0 36 V50 M-50 0 H-36 M36 0 H50 M-35 -35 L-26 -26 M26 26 L35 35 M-35 35 L-26 26 M26 -26 L35 -35\"/>",
        MotionShape.Arrow => "<path d=\"M-12 50 V-8 H-36 L0 -50 L36 -8 H12 V50 Z\"/>",
        _ => throw new VideoException("motion.shape", "Unknown schematic shape.")
    };
    public static string Svg(MotionVisual motion, double percent)
    {
        var poses = At(motion, percent);
        var svg = new StringBuilder("<svg viewBox=\"0 0 1000 600\" role=\"img\" aria-label=\"Authored animated mechanism schematic\" style=\"width:100%;height:100%;background:#101827\">");
        for (var index = 0; index < motion.Entities.Length; index++)
        {
            var entity = motion.Entities[index];
            var pose = poses[index];
            svg.Append(CultureInfo.InvariantCulture, $"<g data-motion-entity=\"{entity.Id}\" transform=\"translate({pose.X} {pose.Y})\" opacity=\"{pose.Opacity}\">");
            svg.Append(CultureInfo.InvariantCulture, $"<g fill=\"{Color(entity.Tone)}\" color=\"{Color(entity.Tone)}\" transform=\"scale({entity.Width / 100.0} {entity.Height / 100.0})\">{Glyph(entity.Shape)}</g>");
            svg.Append(CultureInfo.InvariantCulture, $"<text text-anchor=\"middle\" y=\"{-entity.Height / 2.0 - 16}\" fill=\"#f1f5ff\" font-size=\"17\" font-family=\"sans-serif\">{WebUtility.HtmlEncode(entity.Label)}</text></g>");
        }
        return svg.Append("</svg>").ToString();
    }
}
