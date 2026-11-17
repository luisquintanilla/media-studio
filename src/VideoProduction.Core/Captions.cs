using System.Globalization;
using System.Text;

namespace VideoProduction;

public sealed record ProjectedCaption(long StartMilliseconds, long EndMilliseconds, string Text, Evidence Evidence, string? PolicyException);

public static class Captions
{
    public static void ValidateCue(Cue cue, int sampleRate)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(cue.Text) && !cue.Text.Contains("-->", StringComparison.Ordinal)
            && !cue.Text.Any(c => char.IsControl(c) && c != '\n'),
            "caption.text", "Caption text is empty, contains control characters or a timing delimiter.");
        var lines = cue.Text.Split('\n');
        var seconds = (double)(cue.EndSample - cue.StartSample) / sampleRate;
        var compliant = lines.Length <= 2 && lines.All(l => l.Length <= 42) && seconds >= 1 && seconds <= 6
            && cue.Text.Replace("\n", "", StringComparison.Ordinal).Length / seconds <= 20;
        Guard.Require(compliant || !string.IsNullOrWhiteSpace(cue.PolicyException),
            "caption.policy", "Captions require <=2 lines, <=42 chars/line, 1..6s and <=20 chars/sec, or a documented exception.");
    }

    public static ProjectedCaption[] Project(Project project, Timeline timeline)
    {
        if (project.DeliveryCaptions is not null) return DeliveryCaptions.Project(project, timeline);
        var result = new List<ProjectedCaption>();
        foreach (var placement in timeline.Scenes)
        {
            var narration = project.Scenes.Single(x => x.Id == placement.Id).Narration!;
            var offset = timeline.Fps.Samples(placement.StartFrame, 48000, Rounding.Nearest);
            foreach (var cue in narration.Cues)
            {
                if (cue.StartSample < placement.AudioStartSample || cue.EndSample > placement.AudioStartSample + placement.AudioSamples) continue;
                var start = offset + Rate.Divide((System.Numerics.BigInteger)(cue.StartSample - placement.AudioStartSample) * 48000, narration.SampleRate, Rounding.Nearest);
                var end = offset + Rate.Divide((System.Numerics.BigInteger)(cue.EndSample - placement.AudioStartSample) * 48000, narration.SampleRate, Rounding.Nearest);
                result.Add(new ProjectedCaption((start + 24) / 48, (end + 24) / 48, cue.Text, cue.Evidence, cue.PolicyException));
            }
        }
        return result.ToArray();
    }

    public static string Render(IEnumerable<ProjectedCaption> cues, bool vtt)
    {
        var output = new StringBuilder(vtt ? "WEBVTT\n\n" : "");
        var index = 0;
        foreach (var cue in cues)
        {
            output.Append(++index).Append('\n').Append(Stamp(cue.StartMilliseconds, vtt)).Append(" --> ")
                .Append(Stamp(cue.EndMilliseconds, vtt)).Append('\n')
                .Append(cue.Text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
                    .Replace(">", "&gt;", StringComparison.Ordinal)).Append("\n\n");
        }
        return output.ToString();
    }

    private static string Stamp(long milliseconds, bool vtt) =>
        string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}:{milliseconds / 1000 % 60:00}{(vtt ? '.' : ',')}{milliseconds % 1000:000}");
}
