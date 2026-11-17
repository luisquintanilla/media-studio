using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoProduction;

public sealed record TranscriberConfig(string Executable, string ExecutableSha256,
    ArtifactHash[] Dependencies, string ModelPath, string ModelSha256,
    string Ffmpeg, string FfmpegSha256, int TimeoutSeconds = 600,
    int MaxOutputBytes = 4_000_000, int MaxAudioSeconds = 1800, string? Profile = null);

public sealed record TranscriptSegmentEvidence(long StartMilliseconds, long EndMilliseconds,
    long StartSample, long EndSample, string Text, Evidence Evidence);
public sealed record TranscriptDiscrepancy(bool RequiresReview, string Comparison,
    string IntendedNormalized, string ObservedNormalized);
public sealed record TranscriptLearning(int SchemaVersion, string Source, string MasterSha256,
    int MasterSampleRate, long MasterSampleCount, string Derivative, string DerivativeSha256,
    int DerivativeSampleRate, long DerivativeSampleCount, string Mapping,
    string Provider, string Model, string ModelArtifact, string ModelSha256, ArtifactHash[] TranscriberArtifacts,
    string FfmpegSha256, string RawResultSha256, string IntendedScript,
    string DescriptiveAccessibilityText, string ObservedText, TranscriptSegmentEvidence[] Segments,
    TranscriptDiscrepancy Discrepancy, string? Profile = null, string? ProfileEvidenceSha256 = null);

public static partial class Transcription
{
    public const string Provider = "Whisper.net/whisper.cpp";
    public const string Model = "openai/whisper-base";
    public const string ModelSha256 = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe";
    public const string ModelArtifact = "sandrohanea/whisper.net@v5/classic/ggml-base.bin";
    public static string DefaultModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioTranscriber", "model-cache", "ggml-base.bin");

    public static IReadOnlyList<string> RequiredDependencyPaths { get; } = Array.AsReadOnly(new[]
    {
        "audio-transcriber.dll", "audio-transcriber.deps.json", "audio-transcriber.runtimeconfig.json",
        "AudioTranscriber.dll", "Whisper.net.dll", "Microsoft.Extensions.AI.Abstractions.dll",
        "runtimes/win-x64/whisper.dll", "runtimes/win-x64/ggml-whisper.dll",
        "runtimes/win-x64/ggml-base-whisper.dll", "runtimes/win-x64/ggml-cpu-whisper.dll"
    });

    [GeneratedRegex(@"^\d{2,}:[0-5]\d:[0-5]\d\.\d{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    public static TranscriptDiscrepancy Compare(string intendedScript, string observedText)
    {
        static string Normalize(string text) => Whitespace().Replace(text.Trim(), " ").ToUpperInvariant();
        var intended = Normalize(intendedScript);
        var observed = Normalize(observedText);
        return new TranscriptDiscrepancy(intended != observed,
            "Case/whitespace-insensitive exact text comparison; punctuation retained; not WER or semantic verification.",
            intended, observed);
    }

    public static async Task<TranscriptLearning> Learn(string masterWav, string intendedScript,
        string descriptiveAccessibilityText, string outputDirectory, TranscriberConfig config, CancellationToken ct)
    {
        Guard.Require(OperatingSystem.IsWindows() && Environment.Is64BitProcess
            && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.X64,
            "transcription.platform", "The verified transcriber profile requires Windows x64.");
        Guard.Require(config.TimeoutSeconds is > 0 and <= 1800
            && config.MaxOutputBytes is >= 1024 and <= 16_000_000
            && config.MaxAudioSeconds is > 0 and <= 1800,
            "transcription.bounds", "Invalid transcription resource bounds.");
        Guard.Require(config.Profile is null or DigitalSilenceProfile,
            "transcription.profile", "Unknown transcription profile; default strict behavior is unchanged.");
        Guard.Require(config.ModelSha256 == ModelSha256
            && Path.GetFullPath(config.ModelPath).Equals(Path.GetFullPath(DefaultModelPath), StringComparison.OrdinalIgnoreCase),
            "transcription.model-profile", "Only the preprovisioned default fixed-English Whisper Base cache is supported.");
        if (intendedScript is null || descriptiveAccessibilityText is null)
            throw new VideoException("transcription.text",
                "Script and descriptive text must be explicit strings; use empty script for unknown speech.");
        masterWav = Path.GetFullPath(masterWav);
        outputDirectory = Path.GetFullPath(outputDirectory);
        Guard.Require(!Directory.Exists(outputDirectory) && !File.Exists(outputDirectory),
            "transcription.immutable", "Choose a new output directory; existing transcript evidence is never overwritten.");
        var deployment = Path.GetDirectoryName(Path.GetFullPath(config.Executable))!;
        Guard.Require(config.Dependencies is { Length: > 0 }
            && config.Dependencies.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == config.Dependencies.Length
            && RequiredDependencyPaths.All(p => config.Dependencies.Any(x => x.Path == p)),
            "transcription.artifacts", "Pin the complete supported CLI, managed assembly and Windows x64 native dependency set.");

        var held = new List<FileStream>();
        try
        {
            void HoldVerified(string path, string expectedHash, string code)
            {
                Guard.Require(Files.ValidHash(expectedHash) && File.Exists(path), code,
                    "Required local artifact is missing or its expected SHA-256 is invalid; provision explicitly before transcription.");
                held.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                Guard.Require(Files.Hash(path) == expectedHash, code, "Local artifact SHA-256 mismatch; transcription was not started.");
            }
            HoldVerified(config.Executable, config.ExecutableSha256, "transcription.executable");
            foreach (var dependency in config.Dependencies)
                HoldVerified(Files.Under(deployment, dependency.Path), dependency.Sha256, "transcription.artifacts");
            // The upstream CLI otherwise downloads a missing model. Hold its verified default cache
            // read-only for the entire child lifetime so the checked file cannot disappear on Windows.
            HoldVerified(config.ModelPath, ModelSha256, "transcription.model");
            var ffmpeg = Files.FindExecutable(config.Ffmpeg);
            HoldVerified(ffmpeg, config.FfmpegSha256, "transcription.ffmpeg");
            Guard.Require(File.Exists(masterWav), "transcription.master", "Master WAV is missing.");
            held.Add(new FileStream(masterWav, FileMode.Open, FileAccess.Read, FileShare.Read));
            var master = Wav.Inspect(masterWav);
            Guard.Require(master.Samples <= (long)config.MaxAudioSeconds * master.SampleRate,
                "transcription.duration", "Master exceeds the approved transcription duration.");
            var masterHash = Files.Hash(masterWav);
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputDirectory);
            var derivative = Path.Combine(outputDirectory, "stt-16khz.wav");
            await Processes.Run(ffmpeg,
                ["-hide_banner", "-v", "error", "-nostdin", "-n", "-protocol_whitelist", "file,pipe",
                    "-i", masterWav, "-map", "0:a:0", "-vn", "-ar", "16000", "-ac", "1",
                    "-c:a", "pcm_s16le", "-map_metadata", "-1", derivative],
                outputDirectory, ct, config.TimeoutSeconds, config.MaxOutputBytes);
            var audio = Wav.Inspect(derivative);
            Guard.Require(audio.SampleRate == 16000 && audio.Channels == 1
                && Math.Abs(checked(audio.Samples * (long)master.SampleRate - master.Samples * 16000L)) <= master.SampleRate,
                "transcription.derivative", "Expected a duration-preserving 16 kHz mono PCM16 derivative.");
            var derivativeHash = Files.Hash(derivative);
            held.Add(new FileStream(derivative, FileMode.Open, FileAccess.Read, FileShare.Read));
            var diagnostics = Path.Combine(outputDirectory, "transcriber-profile.json");
            string[] argv = ["transcribe", "--input", derivative, "--format", "json"];
            if (config.Profile is not null)
                argv = [.. argv, "--profile", config.Profile, "--input-sha256", derivativeHash, "--diagnostics", diagnostics];
            var process = await Processes.Run(Path.GetFullPath(config.Executable), argv,
                outputDirectory, ct, config.TimeoutSeconds, config.MaxOutputBytes, requireSuccess: false);
            var rawPath = Path.Combine(outputDirectory, "transcriber-result.json");
            Atomic.Write(rawPath, process.Stdout);
            Atomic.Write(Path.Combine(outputDirectory, "transcriber-stderr.txt"), process.Stderr);
            Guard.Require(process.ExitCode == 0, "transcription.process",
                $"Transcriber exited {process.ExitCode}; inspect retained local stderr. No transcript was accepted.");
            var segments = ParseResult(process.Stdout, Path.GetFileName(derivative), audio, master);
            if (config.Profile is not null) ValidateDigitalSilence(diagnostics, derivative, derivativeHash, audio, segments, ct);
            Guard.Require(Files.Hash(masterWav) == masterHash && Files.Hash(derivative) == derivativeHash,
                "transcription.integrity", "Master or derivative changed during inference.");
            var observed = string.Join(' ', segments.Select(x => x.Text));
            var report = new TranscriptLearning(1, Path.GetFileName(masterWav), masterHash,
                master.SampleRate, master.Samples, Path.GetFileName(derivative), derivativeHash,
                audio.SampleRate, audio.Samples,
                "CLI millisecond segment timestamps; master start=floor(ms*rate/1000), end=ceiling(ms*rate/1000); shared boundaries reuse prior end. No word alignment.",
                Provider, Model, ModelArtifact, ModelSha256,
                [new ArtifactHash(Path.GetFileName(config.Executable), config.ExecutableSha256), .. config.Dependencies],
                config.FfmpegSha256, Files.Hash(rawPath), intendedScript, descriptiveAccessibilityText,
                observed, segments, Compare(intendedScript, observed), config.Profile,
                config.Profile is null ? null : Files.Hash(diagnostics));
            Json.Write(Path.Combine(outputDirectory, "transcript.json"), report);
            return report;
        }
        finally
        {
            foreach (var stream in held) stream.Dispose();
        }
    }

    public static TranscriptSegmentEvidence[] ParseResult(string rawJson, string expectedSource,
        WavInfo derivative, WavInfo master)
    {
        Guard.Require(derivative.SampleRate == 16000 && derivative.Channels == 1 && derivative.Samples > 0
            && master.SampleRate is >= 8000 and <= 192000 && master.Channels == 1 && master.Samples > 0,
            "transcription.audio", "Invalid master/derivative clocks.");
        try
        {
            using var document = JsonDocument.Parse(rawJson, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            Guard.Require(root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1,
                "transcription.contract", "Expected exactly one transcript in the external CLI JSON array.");
            var transcript = root[0];
            Shape(transcript, ["source", "model", "provider", "segments"]);
            Guard.Require(Text(transcript, "source") == expectedSource && Text(transcript, "model") == Model
                && Text(transcript, "provider") == Provider, "transcription.identity",
                "External source, model or provider does not match the approved invocation.");
            var items = transcript.GetProperty("segments");
            Guard.Require(items.ValueKind == JsonValueKind.Array && items.GetArrayLength() <= 10000,
                "transcription.contract", "Expected a bounded segment array.");
            var segments = new List<TranscriptSegmentEvidence>();
            long previousEnd = 0;
            foreach (var item in items.EnumerateArray())
            {
                Shape(item, ["start", "end", "text"]);
                var start = Milliseconds(Text(item, "start"));
                var end = Milliseconds(Text(item, "end"));
                var text = Text(item, "text");
                Guard.Require(start >= previousEnd && end > start && !string.IsNullOrWhiteSpace(text),
                    "transcription.segment", "Empty, reversed, unordered or overlapping external segment.");
                Guard.Require(checked(end * 16000L) <= checked(derivative.Samples * 1000L),
                    "transcription.segment", "External segment exceeds derivative duration.");
                var startSample = checked(start * master.SampleRate) / 1000;
                var endSample = checked(end * master.SampleRate + 999) / 1000;
                if (segments.Count > 0 && start == previousEnd) startSample = segments[^1].EndSample;
                Guard.Require(startSample < endSample && endSample <= master.Samples,
                    "transcription.segment", "External segment exceeds original master duration.");
                segments.Add(new TranscriptSegmentEvidence(start, end, startSample, endSample, text, Evidence.AsrSegment));
                previousEnd = end;
            }
            Guard.Require(segments.Count > 0, "transcription.no-speech",
                "The model returned no speech segments; no script-derived transcript will be substituted.");
            return segments.ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new VideoException("transcription.contract", "Malformed or unsupported external CLI JSON/timestamp contract.");
        }
    }

    private static void Shape(JsonElement element, string[] fields)
    {
        Guard.Require(element.ValueKind == JsonValueKind.Object
            && element.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(fields.Order()),
            "transcription.contract", "External JSON has missing, duplicate or unknown fields.");
    }

    private static string Text(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        Guard.Require(value.ValueKind == JsonValueKind.String, "transcription.contract", "External JSON field must be a string.");
        return value.GetString()!;
    }

    private static long Milliseconds(string timestamp)
    {
        Guard.Require(TimestampPattern().IsMatch(timestamp), "transcription.contract", "Expected external HH:MM:SS.mmm timestamps.");
        var parts = timestamp.Split([':', '.']);
        return checked(long.Parse(parts[0], CultureInfo.InvariantCulture) * 3_600_000
            + long.Parse(parts[1], CultureInfo.InvariantCulture) * 60_000
            + long.Parse(parts[2], CultureInfo.InvariantCulture) * 1000
            + long.Parse(parts[3], CultureInfo.InvariantCulture));
    }
}
