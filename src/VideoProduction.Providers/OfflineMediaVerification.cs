using System.Globalization;
using System.Text;
using System.Text.Json;
using MediaStudio.Audio;
using Microsoft.Extensions.AI;

namespace VideoProduction.Providers;

internal sealed class OfflineNarrationBackend(int delayMilliseconds = 0) : INarrationBackend, IDisposable
{
    public static SpeechRequest Voice { get; } = new("synthetic-tone-not-speech", "en");
    public static SpeechClientIdentity Identity { get; } = new("offline-synthetic-no-model", typeof(ToneClient).FullName!, []);
    private readonly ToneClient client = new();
    public async Task<NarrationTake> Synthesize(string text, string outputDirectory, CancellationToken ct)
    {
        Guard.Require(delayMilliseconds is >= 0 and <= 1000, "verification.delay", "Synthetic latency is explicitly bounded to one second per take.");
        await Task.Delay(delayMilliseconds, ct);
        return await new MeaiNarrationBackend(client, Voice, Identity).Synthesize(text, outputDirectory, ct);
    }
    public void Dispose() => client.Dispose();

    private sealed class ToneClient : ITextToSpeechClient
    {
        private int calls;
        public Task<TextToSpeechResponse> GetAudioAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                Wav.WriteHeader(writer, 16000, 16000);
                var frequency = ++calls % 2 == 0 ? 660 : 440;
                for (var sample = 0; sample < 16000; sample++)
                    writer.Write((short)(Math.Sin(2 * Math.PI * frequency * sample / 16000) * 4000));
            }
            return Task.FromResult(new TextToSpeechResponse([new DataContent(stream.ToArray(), "audio/wav")]) { ModelId = "synthetic-tone-fixture-v1-not-tts" });
        }
        public IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(string text,
            TextToSpeechOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Offline verification uses measured whole-take fixtures, not a streaming TTS service.");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

internal sealed class OfflineEncodedVideoPipeline(OfflineMediaConfiguration configuration) : IVideoJobPipeline
{
    public void EnsureConfigured(VideoRequest request)
    {
        Guard.Require(request.Plan is not null, "verification.plan", "Offline encoding requires an actual reviewed storyboard, not a staged completed result.");
        ChatVideoPlanner.Validate(request.Plan, request.Constraints);
        _ = Files.FindExecutable(configuration.Ffmpeg);
        _ = Files.FindExecutable(configuration.Ffprobe);
        Guard.Require(Files.ValidHash(configuration.FontSha256) && Files.Hash(PrivateFiles.LocalPath(configuration.FontFile)) == configuration.FontSha256,
            "verification.font", "Offline video requires the exact explicitly configured locally licensed font.");
        Guard.Require(request.Plan!.Scenes.All(s => s.Visual is null && s.Motion is null && s.IllustrationPrompt is null),
            "verification.capability", "Offline verification supports authored static cards only; it cannot claim a visual preset or generated illustration was rendered.");
    }

    public async Task<VideoArtifact[]> Execute(string root, VideoRequest request, Action<string> progress, CancellationToken ct)
    {
        EnsureConfigured(request);
        progress("offline-synthetic-video-narration");
        var storyboard = request.Plan!;
        var parts = storyboard.Scenes.Select((s, i) => new AudioScriptPart($"scene-{i + 1}", s.Script, 0)).ToArray();
        var audio = new AudioProductionRequest(1, "offline-video-fixture", 1, "video-narration", 16000,
            checked(16000L * request.Constraints.MaxDurationSeconds), parts, OfflineNarrationBackend.Voice, OfflineNarrationBackend.Identity);
        using var backend = new OfflineNarrationBackend(configuration.DelayMilliseconds);
        var narration = Path.Combine(root, "video-narration");
        var master = await AudioProduction.Produce(audio, AudioProduction.Approve(audio, "explicit-offline-verification"),
            backend, narration, ct);
        var directory = Path.Combine(root, "offline-video");
        Directory.CreateDirectory(directory);
        File.Copy(configuration.FontFile, Path.Combine(directory, "font.ttf"));
        var segments = new List<string>();
        var captions = new StringBuilder("WEBVTT\n\nNOTE Synthetic tones are not speech. Cues contain the reviewed intended script on actual take sample ranges.\n\n");
        foreach (var (scene, index) in storyboard.Scenes.Select((scene, index) => (scene, index)))
        {
            ct.ThrowIfCancellationRequested();
            progress("offline-encoded-authored-card");
            var text = "OFFLINE SYNTHETIC VERIFICATION / NOT LIVE TTS\n\n" + scene.Title + "\n\n"
                + scene.Text + "\n\nReviewed script: " + scene.Script;
            Guard.Require(text.Split('\n').All(line => line.Length <= 90) && text.Length <= 1800,
                "verification.card", "Offline cards support bounded short lines only; revise the authored text rather than silently clipping it.");
            await File.WriteAllTextAsync(Path.Combine(directory, $"card-{index}.txt"), text, ct);
            var output = $"scene-{index}.mp4";
            await Processes.Run(configuration.Ffmpeg,
                ["-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=0x173351:s=960x540:r=30:d=1",
                 "-i", Path.Combine(narration, "takes", parts[index].Id, "narration.wav"),
                 "-vf", $"drawtext=fontfile=font.ttf:textfile=card-{index}.txt:expansion=none:fontsize=19:fontcolor=white:x=30:y=30:line_spacing=8",
                 "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-t", "1", "-movflags", "+faststart", output],
                directory, ct, 30, 200_000);
            segments.Add($"file '{output}'");
            captions.Append(index + 1).Append('\n').Append(Time(index)).Append(" --> ").Append(Time(index + 1))
                .Append('\n').Append(scene.Script).Append("\n\n");
        }
        await File.WriteAllLinesAsync(Path.Combine(directory, "concat.txt"), segments, ct);
        var movie = Path.Combine(root, "candidate.mp4");
        await Processes.Run(configuration.Ffmpeg,
            ["-hide_banner", "-nostdin", "-loglevel", "error", "-f", "concat", "-safe", "1", "-i", "concat.txt",
             "-c", "copy", "-movflags", "+faststart", movie], directory, ct, 30, 200_000);
        var probe = await Processes.Run(configuration.Ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_type,codec_name:format=duration", "-of", "json", movie],
            directory, ct, 30, 200_000);
        using var observation = JsonDocument.Parse(probe.Stdout);
        var streams = observation.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var duration = double.Parse(observation.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        Guard.Require(streams.Any(s => s.GetProperty("codec_type").GetString() == "video" && s.GetProperty("codec_name").GetString() == "h264")
            && streams.Any(s => s.GetProperty("codec_type").GetString() == "audio")
            && Math.Abs(duration - storyboard.Scenes.Length) < 0.2 && duration <= request.Constraints.MaxDurationSeconds + 0.2,
            "verification.encoding", "Actual encoded streams or duration do not match the reviewed bounded fixture.");
        await Processes.Run(configuration.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-i", movie, "-f", "null", "-"],
            directory, ct, 30, 200_000);
        await File.WriteAllTextAsync(Path.Combine(root, "captions.vtt"), captions.ToString(), ct);
        Json.Write(Path.Combine(root, "offline-verification.json"), new
        {
            schemaVersion = 1, mode = "offline-synthetic-native-workflow", requestSha256 = Files.Fingerprint(request),
            actualDurationSeconds = duration, narrationMasterSha256 = master.MasterSha256,
            fontSha256 = configuration.FontSha256, provider = OfflineNarrationBackend.Identity,
            claims = "Actual FFmpeg H264/AAC encoding and authored inert cards. Synthetic tones, not TTS quality, acoustic certification, visual-preset evidence, live inference, release or publication."
        });
        return new[] { ("candidate.mp4", "candidate-mp4"), ("captions.vtt", "captions-vtt"),
                ("offline-verification.json", "offline-verification") }
            .Select(item => new VideoArtifact(item.Item1, Files.Hash(Path.Combine(root, item.Item1)), item.Item2)).ToArray();
    }
    private static string Time(int seconds) => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
}
