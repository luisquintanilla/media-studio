namespace VideoProduction;

public sealed record SilenceInput(string Source, string Sha256, int SampleRate, int Channels,
    long SampleCount, long DurationTicks, string Format);
public sealed record SilenceProvenance(string Model, string Provider, string PackageVersion, string ModelSha256);
public sealed record SilenceIntersection(long StartSample, long EndSampleExclusive, long VerifiedZeroSamples);
public sealed record SilenceObservation(int Index, string Text, long StartTicks, long EndTicks,
    string Classification, string Reason, SilenceIntersection? Intersection);
public sealed record SilenceSpeech(int RawIndex, long StartTicks, long EndTicks, string Text);
public sealed record SilenceDiagnostics(string SchemaVersion, string Profile, string Status, string? RejectionReason,
    SilenceInput Input, SilenceProvenance Provenance, SilenceObservation[] RawObservations, SilenceSpeech[] EffectiveSegments);

public static partial class Transcription
{
    public const string DigitalSilenceProfile = "verified-digital-silence-v1";

    public static void ValidateDigitalSilence(string diagnosticsFile, string derivativeFile, string derivativeHash,
        WavInfo audio, TranscriptSegmentEvidence[] segments, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var report = Json.Read<SilenceDiagnostics>(diagnosticsFile);
        Guard.Require(report.SchemaVersion == "1.0" && report.Profile == DigitalSilenceProfile
            && report.Status == "accepted" && report.RejectionReason is null
            && report.Input.Source == Path.GetFileName(derivativeFile) && report.Input.Sha256 == derivativeHash
            && Files.Hash(derivativeFile) == derivativeHash && report.Input.SampleRate == 16000
            && report.Input.Channels == 1 && report.Input.Format == "pcm16"
            && audio.SampleRate == 16000 && audio.Channels == 1
            && report.Input.SampleCount == audio.Samples && report.Input.DurationTicks == checked(audio.Samples * 625)
            && report.Provenance.Model == Model && report.Provenance.Provider == Provider
            && report.Provenance.ModelSha256 == ModelSha256 && !string.IsNullOrWhiteSpace(report.Provenance.PackageVersion),
            "transcription.profile", "Accepted profile evidence must bind the exact derivative clock/hash and fixed model.");
        Guard.Require(report.RawObservations.Length is > 0 and <= 10000
            && report.EffectiveSegments.Length == segments.Length && segments.Length > 0,
            "transcription.profile", "Missing full raw/effective inference evidence; marker-only success is forbidden.");
        var speech = new List<SilenceObservation>();
        long previousStart = -1;
        foreach (var (observation, index) in report.RawObservations.Select((value, index) => (value, index)))
        {
            ct.ThrowIfCancellationRequested();
            Guard.Require(observation.Index == index + 1 && observation.StartTicks >= previousStart
                && observation.StartTicks >= 0 && observation.EndTicks > observation.StartTicks
                && !string.IsNullOrWhiteSpace(observation.Text) && !string.IsNullOrWhiteSpace(observation.Reason),
                "transcription.profile", "Raw inference records must be complete, ordered and unmodified.");
            previousStart = observation.StartTicks;
            if (observation.Classification == "speech")
            {
                Guard.Require(observation.Intersection is null && !observation.Text.Contains("[BLANK_AUDIO]", StringComparison.Ordinal)
                    && !(observation.Text.Trim().StartsWith('[') && observation.Text.Trim().EndsWith(']'))
                    && observation.EndTicks <= report.Input.DurationTicks,
                    "transcription.profile", "Speech/unknown markers cannot use the digital-silence exemption or overrun EOF.");
                speech.Add(observation);
                continue;
            }
            Guard.Require(observation.Classification == "verifiedDigitalSilence"
                && observation.Text.Trim() == "[BLANK_AUDIO]" && observation.StartTicks < report.Input.DurationTicks,
                "transcription.profile", "Only the literal whole non-speech marker with in-file onset may be classified.");
            var start = checked(observation.StartTicks * 16000) / TimeSpan.TicksPerSecond;
            var end = Math.Min(audio.Samples,
                checked(observation.EndTicks * 16000 + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond);
            Guard.Require(observation.Intersection == new SilenceIntersection(start, end, end - start)
                && DigitalZero(derivativeFile, audio, start, end, ct),
                "transcription.profile", "Non-speech classification requires every intersecting actual PCM16 sample to be exactly zero.");
        }
        Guard.Require(speech.Count == segments.Length, "transcription.profile", "Raw speech coverage differs from effective output.");
        for (var index = 0; index < speech.Count; index++)
        {
            var raw = speech[index];
            var segment = segments[index];
            Guard.Require(raw.StartTicks == checked(segment.StartMilliseconds * TimeSpan.TicksPerMillisecond)
                && raw.EndTicks == checked(segment.EndMilliseconds * TimeSpan.TicksPerMillisecond)
                && raw.Text.Trim() == segment.Text
                && report.EffectiveSegments[index] == new SilenceSpeech(raw.Index, raw.StartTicks, raw.EndTicks, segment.Text),
                "transcription.profile", "Effective speech must preserve every raw spoken range/text; no clipping or script substitution.");
        }
    }

    private static bool DigitalZero(string file, WavInfo audio, long start, long end, CancellationToken ct)
    {
        Guard.Require(start >= 0 && start < end && end <= audio.Samples,
            "transcription.profile", "Invalid non-speech PCM intersection.");
        using var stream = File.OpenRead(file);
        using var reader = new BinaryReader(stream);
        stream.Position = 12;
        long dataOffset = -1;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var length = reader.ReadUInt32();
            var next = checked(stream.Position + length + (length & 1));
            Guard.Require(next <= stream.Length, "transcription.profile", "Truncated diagnostic derivative.");
            if (id == "data")
            {
                Guard.Require(dataOffset < 0 && length == audio.Samples * 2,
                    "transcription.profile", "Ambiguous PCM data cannot substantiate digital silence.");
                dataOffset = stream.Position;
            }
            stream.Position = next;
        }
        Guard.Require(dataOffset >= 0, "transcription.profile", "Missing PCM data.");
        stream.Position = checked(dataOffset + start * 2);
        var remaining = checked((end - start) * 2);
        Span<byte> buffer = stackalloc byte[4096];
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            var length = stream.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            Guard.Require(length > 0, "transcription.profile", "Truncated PCM silence interval.");
            if (buffer[..length].IndexOfAnyExcept((byte)0) >= 0) return false;
            remaining -= length;
        }
        return true;
    }
}
