using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoProduction;

public sealed record AnchorObservation(string Id, string TargetIdentity, Region Region, string Screenshot,
    string Event, double? ClickX, double? ClickY, string LayoutSha256);
public sealed record ObservedAnchors(int SchemaVersion, AnchorObservation[] Anchors);

public static partial class CaptureAnchors
{
    [GeneratedRegex(@"n:(\d+).*All:([0-9.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SsimLine();

    public static async Task Bind(Tools tools, string directory, SourceClock clock, CancellationToken ct)
    {
        var observations = Json.Read<ObservedAnchors>(Path.Combine(directory, "observations.json"));
        Guard.Require(observations.SchemaVersion == 1 && observations.Anchors.Length is > 0 and <= 20,
            "anchor.observations", "Capture did not produce bounded genuine DOM observations.");
        var anchors = new List<DomAnchor>();
        foreach (var observation in observations.Anchors)
        {
            Guard.Require(Files.ValidId(observation.Id) && observation.Screenshot == observation.Id + ".png",
                "anchor.path", "Anchor screenshot must use its stable ID.");
            Editing.ValidateRegion(observation.Region);
            var r = observation.Region;
            var x = (int)(r.X * 1920) / 2 * 2;
            var y = (int)(r.Y * 1080) / 2 * 2;
            var width = Math.Max(2, (int)(r.Width * 1920) / 2 * 2);
            var height = Math.Max(2, (int)(r.Height * 1080) / 2 * 2);
            var statsName = $"{observation.Id}-ssim.txt";
            var screenshot = Path.Combine(directory, observation.Screenshot);
            await Processes.Run(tools.Ffmpeg, ["-hide_banner", "-nostdin", "-loglevel", "error", "-protocol_whitelist", "file,pipe",
                "-i", Path.Combine(directory, "browser.webm"), "-loop", "1", "-i", screenshot,
                "-filter_complex", $"[0:v]crop={width}:{height}:{x}:{y},format=yuv420p[v];[1:v]crop={width}:{height}:{x}:{y},format=yuv420p[s];[v][s]ssim=stats_file={statsName}",
                "-frames:v", clock.PresentationTimestamps.Length.ToString(CultureInfo.InvariantCulture), "-an", "-f", "null", "-"],
                directory, ct, 300);
            var scores = File.ReadAllLines(Path.Combine(directory, statsName)).Select(line => SsimLine().Match(line))
                .Where(m => m.Success).Select(m => (Frame: long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1,
                    Score: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToArray();
            Guard.Require(scores.Length > 0 && scores.Max(s => s.Score) >= 0.94,
                "anchor.visual-match", "Observed DOM target cannot be reconciled with genuine encoded source frames.");
            var best = scores.MaxBy(s => s.Score);
            // Only the contiguous visually-matched interval around the best observation is valid.
            var threshold = Math.Max(0.94, best.Score - 0.01);
            var start = best.Frame;
            var end = best.Frame + 1;
            while (start > 0 && scores.Any(s => s.Frame == start - 1 && s.Score >= threshold)) start--;
            while (end < clock.PresentationTimestamps.LongLength && scores.Any(s => s.Frame == end && s.Score >= threshold)) end++;
            anchors.Add(new DomAnchor(observation.Id, observation.TargetIdentity, Files.HashText(observation.TargetIdentity),
                r, 1920, 1080, start, end, best.Frame, Files.Hash(screenshot), observation.LayoutSha256,
                observation.Event, observation.ClickX, observation.ClickY));
            File.Delete(Path.Combine(directory, statsName));
        }
        Json.Write(Path.Combine(directory, "anchors.json"), new AnchorLedger(1, clock.SourceSha256, clock.Fps, 1920, 1080,
            anchors.ToArray(), "Real DOM screenshot/geometry matched to decoded source ROI SSIM (>=0.94, contiguous window within0.01best to allow measured VP8 quantization variation); frame PTS from ffprobe. Not word or exact click-time alignment."));
    }
}
