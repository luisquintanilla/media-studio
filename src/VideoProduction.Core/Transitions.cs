using System.Globalization;

namespace VideoProduction;

public sealed record TransitionFrame(long TimelineFrame, string OutgoingScene, long OutgoingFrame,
    string IncomingScene, long IncomingFrame, int IncomingWeightNumerator, int WeightDenominator,
    bool AuthoredBridge = true);

public static class Transitions
{
    public static void Validate(SceneTransition transition) =>
        Guard.Require(transition.Frames is >= 1 and <= 30
            && transition.OutgoingStaticFrames is >= 2 and <= 30
            && transition.IncomingStaticFrames is >= 2 and <= 30
            && transition.AudioRampSamples is >= 1 and <= 2400 && !string.IsNullOrWhiteSpace(transition.Reason),
            "transition.fields", "Bridge requires1..30frames,2..30static handle frames,1..2400silent ramp samples and reason.");

    public static TransitionFrame[] Maps(Project project, Timeline timeline)
    {
        var result = new List<TransitionFrame>();
        for (var index = 1; index < project.Scenes.Length; index++)
        {
            if (project.Scenes[index].TransitionIn is not { } transition) continue;
            Validate(transition);
            var previous = timeline.Scenes[index - 1];
            var incoming = timeline.Scenes[index];
            Guard.Require(transition.OutgoingStaticFrames <= previous.FrameCount
                && transition.IncomingStaticFrames <= incoming.FrameCount
                && transition.AudioRampSamples <= previous.DeliverySamples
                && transition.AudioRampSamples <= incoming.DeliverySamples,
                "transition.handles", "Declared settled/silent handle exceeds scene duration.");
            for (var frame = 0; frame < transition.Frames; frame++)
                result.Add(new TransitionFrame(incoming.StartFrame - transition.Frames + frame,
                    previous.Id, previous.FrameCount - 1, incoming.Id, 0, frame + 1, transition.Frames + 1));
        }
        return result.ToArray();
    }

    public static string AudioFilter(int incomingRamp, int outgoingRamp, long samples)
    {
        var filters = new List<string> { "asetpts=N/SR/TB" };
        if (incomingRamp > 0) filters.Add($"afade=t=in:ss=0:ns={incomingRamp}");
        if (outgoingRamp > 0) filters.Add($"afade=t=out:ss={samples - outgoingRamp}:ns={outgoingRamp}");
        return string.Join(',', filters);
    }
}

public sealed partial class Production
{
    private async Task VerifyStaticHandle(string file, long first, int count, CancellationToken ct)
    {
        var result = await Ffmpeg(["-v", "error", "-i", file, "-map", "0:v:0", "-an",
            "-vf", $"trim=start_frame={first}:end_frame={first + count},setpts=PTS-STARTPTS",
            "-f", "framehash", "-hash", "sha256", "-"], ct);
        var hashes = result.Stdout.Split('\n').Where(line => !line.StartsWith('#') && line.Contains(','))
            .Select(line => line.Split(',')[^1].Trim()).ToArray();
        Guard.Require(hashes.Length == count && hashes.Distinct(StringComparer.Ordinal).Count() == 1,
            "transition.moving-handle", "Declared handle is not pixel-identical after rendering/crop/effects. Never dissolve moving UI; select a genuinely settled handle.");
    }

    private async Task VerifySilentHandle(string file, long start, int samples, string output, CancellationToken ct)
    {
        await Ffmpeg(["-y", "-v", "error", "-i", file, "-map", "0:a:0", "-vn",
            "-af", $"atrim=start_sample={start}:end_sample={start + samples},asetpts=N/SR/TB",
            "-c:a", "pcm_s16le", "-f", "s16le", output], ct);
        var bytes = await File.ReadAllBytesAsync(output, ct);
        Guard.Require(bytes.Length == samples * 2 && bytes.All(b => b == 0),
            "transition.nonzero-audio", "Audio boundary ramps require actual exact-zero rendered PCM handles; speech/nonzero audio cannot be faded.");
    }

    private async Task<(string[] Files, string[] AudioFilters)> InsertTransitions(Project project,
        Timeline timeline, List<string> scenes, string work, CancellationToken ct)
    {
        var files = new List<string>();
        var filters = new List<string>();
        for (var index = 0; index < scenes.Count; index++)
        {
            if (project.Scenes[index].TransitionIn is { } transition)
            {
                var previous = timeline.Scenes[index - 1];
                await VerifyStaticHandle(scenes[index - 1], previous.FrameCount - transition.OutgoingStaticFrames,
                    transition.OutgoingStaticFrames, ct);
                await VerifyStaticHandle(scenes[index], 0, transition.IncomingStaticFrames, ct);
                await VerifySilentHandle(scenes[index - 1], previous.DeliverySamples - transition.AudioRampSamples,
                    transition.AudioRampSamples, Path.Combine(work, $"out-{index}.pcm"), ct);
                await VerifySilentHandle(scenes[index], 0, transition.AudioRampSamples,
                    Path.Combine(work, $"in-{index}.pcm"), ct);
                var outgoingPng = Path.Combine(work, $"out-{index}.png");
                var incomingPng = Path.Combine(work, $"in-{index}.png");
                await Ffmpeg(["-y", "-i", scenes[index - 1], "-vf",
                    $"select=eq(n\\,{previous.FrameCount - 1})", "-frames:v", "1", outgoingPng], ct);
                await Ffmpeg(["-y", "-i", scenes[index], "-frames:v", "1", incomingPng], ct);
                var bridge = Path.Combine(work, $"bridge-{index}.mkv");
                var denominator = transition.Frames + 1;
                await Ffmpeg(["-y", "-loop", "1", "-framerate", "30", "-i", outgoingPng,
                    "-loop", "1", "-framerate", "30", "-i", incomingPng,
                    "-f", "lavfi", "-i", "anullsrc=r=48000:cl=mono",
                    "-filter_complex", $"[0:v]format=yuv420p[a];[1:v]format=yuv420p[b];[a][b]blend=all_expr='A*(1-N/{denominator})+B*N/{denominator}',trim=end_frame={transition.Frames},settb=1/30,setpts=N[v]",
                    "-map", "[v]", "-map", "2:a:0", "-af", $"atrim=end_sample={transition.Frames * 1600},asetpts=N/SR/TB",
                    "-frames:v", transition.Frames.ToString(CultureInfo.InvariantCulture),
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "0", "-pix_fmt", "yuv420p",
                    "-color_range", "tv", "-colorspace", "bt709", "-color_trc", "bt709", "-color_primaries", "bt709",
                    "-c:a", "pcm_s16le", bridge], ct);
                files.Add(bridge);
                filters.Add("asetpts=N/SR/TB");
            }
            files.Add(scenes[index]);
            filters.Add(Transitions.AudioFilter(project.Scenes[index].TransitionIn?.AudioRampSamples ?? 0,
                index + 1 < scenes.Count ? project.Scenes[index + 1].TransitionIn?.AudioRampSamples ?? 0 : 0,
                timeline.Scenes[index].DeliverySamples));
        }
        return (files.ToArray(), filters.ToArray());
    }
}
