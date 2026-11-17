using VideoProduction;
using System.Text.Json;

namespace MediaStudio.Audio;

public sealed record AudioScriptPart(string Id, string Text, long PauseAfterSamples);
public sealed record AudioProductionRequest(int SchemaVersion, string ProjectId, int Revision,
    string OutputName, int SampleRate, long MaximumSamples, AudioScriptPart[] Parts,
    SpeechRequest Voice, SpeechClientIdentity Provider);
public sealed record AudioApproval(int SchemaVersion, string RequestSha256, string ApprovedBy);
public sealed record AudioTranscriptPart(string Id, string Text, long StartSample, long EndSample,
    long PauseAfterSamples, string TakePath, string TakeSha256, string MetadataPath, string MetadataSha256);
public sealed record AudioMaster(int SchemaVersion, string Kind, string RequestSha256, string ApprovalSha256,
    string MasterPath, string MasterSha256, int SampleRate, long SampleCount, string TranscriptSha256,
    AudioTranscriptPart[] Parts, string Interpretation, AudioOutcomeDto Outcome);

public static class AudioProduction
{
    public static AudioApproval Approve(AudioProductionRequest request, string approvedBy)
    {
        Validate(request);
        Guard.Require(!string.IsNullOrWhiteSpace(approvedBy), "audio.approver", "An explicit approver is required.");
        return new(1, Files.Fingerprint(request), approvedBy);
    }

    public static async Task<AudioMaster> Produce(AudioProductionRequest request, AudioApproval approval,
        INarrationBackend narration, string outputDirectory, CancellationToken ct)
    {
        request = JsonSerializer.Deserialize<AudioProductionRequest>(Json.Text(request), Json.Options)
            ?? throw new VideoException("audio.request", "A complete approved audio request is required.");
        Validate(request);
        Guard.Require(approval is { SchemaVersion: 1 } && !string.IsNullOrWhiteSpace(approval.ApprovedBy)
            && approval.RequestSha256 == Files.Fingerprint(request), "audio.approval",
            "Approval must bind this exact saved audio request, voice/provider, output and sample bounds.");
        ArgumentNullException.ThrowIfNull(narration);
        ct.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(outputDirectory);
        Guard.Require(!Directory.Exists(target) && !File.Exists(target), "audio.immutable",
            "Audio output already exists; choose a new output directory.");
        var staging = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        Directory.CreateDirectory(staging);
        try
        {
            var takes = new List<NarrationTake>();
            var transcript = new List<AudioTranscriptPart>();
            long cursor = 0;
            foreach (var part in request.Parts)
            {
                ct.ThrowIfCancellationRequested();
                var take = await narration.Synthesize(part.Text, Path.Combine(staging, "takes", part.Id), ct);
                var clock = Wav.Inspect(take.WavPath);
                Guard.Require(clock.SampleRate == request.SampleRate && clock.Samples == take.SampleCount
                    && take.SampleRate == clock.SampleRate && take.DisplayText == part.Text
                    && Files.Hash(take.WavPath) == take.WavSha256, "audio.take",
                    "Narration take text, measured clock or integrity differs from the approved script.");
                _ = Wav.PcmRange(take.WavPath);
                var metadata = Json.Read<System.Text.Json.JsonElement>(take.MetadataPath);
                Guard.Require(metadata.GetProperty("execution").GetProperty("client")
                        .Deserialize<SpeechClientIdentity>(Json.Options) is { } identity
                    && Files.Fingerprint(identity) == Files.Fingerprint(request.Provider)
                    && metadata.GetProperty("execution").GetProperty("request")
                        .Deserialize<SpeechRequest>(Json.Options) == request.Voice,
                    "audio.identity", "Returned synthesis identity differs from the approved provider/voice.");
                var end = checked(cursor + take.SampleCount);
                var next = checked(end + part.PauseAfterSamples);
                Guard.Require(next <= request.MaximumSamples, "audio.bounds",
                    $"Actual synthesized audio reached {next} samples ({next / (double)request.SampleRate:F1}s), exceeding the approved {request.MaximumSamples} samples ({request.MaximumSamples / (double)request.SampleRate:F1}s) maximum. Shorten the script or explicitly approve a larger maximum in a new revision. No audio was truncated.");
                var takePath = Path.GetRelativePath(staging, take.WavPath).Replace('\\', '/');
                var metadataPath = Path.GetRelativePath(staging, take.MetadataPath).Replace('\\', '/');
                Guard.Require(Files.Under(staging, takePath) == Path.GetFullPath(take.WavPath)
                    && Files.Under(staging, metadataPath) == Path.GetFullPath(take.MetadataPath),
                    "audio.take", "Retained take evidence must be contained in this candidate.");
                transcript.Add(new(part.Id, part.Text, cursor, end, part.PauseAfterSamples,
                    takePath, take.WavSha256, metadataPath, Files.Hash(take.MetadataPath)));
                takes.Add(take);
                cursor = next;
            }
            var masterPath = Path.Combine(staging, "master.wav");
            using (var output = new FileStream(masterPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(output))
            {
                Wav.WriteHeader(writer, request.SampleRate, cursor);
                var buffer = new byte[65536];
                for (var index = 0; index < takes.Count; index++)
                {
                    var take = takes[index];
                    var range = Wav.PcmRange(take.WavPath);
                    using var input = File.OpenRead(take.WavPath);
                    input.Position = range.Offset;
                    for (long left = range.Bytes; left > 0;)
                    {
                        ct.ThrowIfCancellationRequested();
                        var count = (int)Math.Min(buffer.Length, left);
                        input.ReadExactly(buffer.AsSpan(0, count));
                        writer.Write(buffer, 0, count);
                        left -= count;
                    }
                    Guard.Require(Files.Hash(take.WavPath) == take.WavSha256
                        && Files.Hash(take.MetadataPath) == transcript[index].MetadataSha256,
                        "audio.integrity", "A take changed during composition.");
                    Array.Clear(buffer);
                    for (long left = checked(request.Parts[index].PauseAfterSamples * 2); left > 0;)
                    {
                        ct.ThrowIfCancellationRequested();
                        var count = (int)Math.Min(buffer.Length, left);
                        writer.Write(buffer, 0, count);
                        left -= count;
                    }
                }
            }
            var masterClock = Wav.Inspect(masterPath);
            Guard.Require(masterClock.Samples == cursor && masterClock.SampleRate == request.SampleRate,
                "audio.master", "Composed master clock differs from actual take/pause samples.");
            var transcriptPath = Path.Combine(staging, "transcript.json");
            Json.Write(transcriptPath, transcript.ToArray());
            Json.Write(Path.Combine(staging, "request.json"), request);
            Json.Write(Path.Combine(staging, "approval.json"), approval);
            var result = new AudioMaster(1, "audio-candidate", Files.Fingerprint(request), Files.Fingerprint(approval),
                "master.wav", Files.Hash(masterPath), masterClock.SampleRate, masterClock.Samples,
                Files.Hash(transcriptPath), transcript.ToArray(),
                "Reviewed script on actual synthesis take sample ranges, with real PCM silence pauses. "
                + "Not an ASR transcript, acoustic certification, release approval or publication.",
                AudioOutcomeDto.From(new AudioCompleted(Files.Hash(masterPath), masterClock.Samples)));
            Json.Write(Path.Combine(staging, "manifest.json"), result);
            _ = Verify(staging);
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
            return result;
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public static AudioMaster Verify(string directory)
    {
        var result = Json.Read<AudioMaster>(Path.Combine(directory, "manifest.json"));
        var request = Json.Read<AudioProductionRequest>(Path.Combine(directory, "request.json"));
        var approval = Json.Read<AudioApproval>(Path.Combine(directory, "approval.json"));
        Validate(request);
        Guard.Require(result.SchemaVersion == 1 && result.Kind == "audio-candidate"
            && result.MasterPath == "master.wav"
            && result.Outcome == AudioOutcomeDto.From(new AudioCompleted(result.MasterSha256, result.SampleCount))
            && result.RequestSha256 == Files.Fingerprint(request)
            && approval.SchemaVersion == 1 && !string.IsNullOrWhiteSpace(approval.ApprovedBy)
            && approval.RequestSha256 == result.RequestSha256
            && result.ApprovalSha256 == Files.Fingerprint(approval),
            "audio.manifest", "Audio manifest, request or approval identity differs.");
        var master = Files.Under(directory, result.MasterPath);
        var clock = Wav.Inspect(master);
        Guard.Require(clock.SampleRate == request.SampleRate && clock.SampleRate == result.SampleRate
            && clock.Samples == result.SampleCount && clock.Samples <= request.MaximumSamples
            && Files.Hash(master) == result.MasterSha256
            && Files.Hash(Path.Combine(directory, "transcript.json")) == result.TranscriptSha256,
            "audio.manifest", "Audio master clock or transcript/master hash differs.");
        var transcript = Json.Read<AudioTranscriptPart[]>(Path.Combine(directory, "transcript.json"));
        Guard.Require(result.Parts is not null && transcript is not null
            && Files.Fingerprint(result.Parts) == Files.Fingerprint(transcript)
            && result.Parts.Length == request.Parts.Length,
            "audio.manifest", "Transcript and manifest must contain the same complete part ledger.");
        long cursor = 0;
        using var masterStream = File.OpenRead(master);
        var masterRange = Wav.PcmRange(master);
        masterStream.Position = masterRange.Offset;
        var actual = new byte[65536];
        var expected = new byte[actual.Length];
        for (var index = 0; index < request.Parts.Length; index++)
        {
            var part = result.Parts![index];
            var approved = request.Parts[index];
            Guard.Require(part is not null && part.Id == approved.Id && part.Text == approved.Text
                && part.StartSample == cursor && part.EndSample > cursor
                && part.PauseAfterSamples == approved.PauseAfterSamples,
                "audio.ledger", "Part ledger differs from the approved script or actual contiguous sample ranges.");
            var take = Files.Under(directory, part!.TakePath);
            var metadataPath = Files.Under(directory, part.MetadataPath);
            var takeClock = Wav.Inspect(take);
            var takeRange = Wav.PcmRange(take);
            var metadata = Json.Read<JsonElement>(metadataPath);
            Guard.Require(Files.Hash(take) == part.TakeSha256
                && Files.Hash(metadataPath) == part.MetadataSha256
                && takeClock.SampleRate == request.SampleRate
                && takeClock.Samples == part.EndSample - part.StartSample
                && metadata.GetProperty("wavSha256").GetString() == part.TakeSha256
                && metadata.GetProperty("sampleCount").GetInt64() == takeClock.Samples
                && metadata.GetProperty("displayText").GetString() == part.Text
                && Files.Fingerprint(metadata.GetProperty("execution").GetProperty("client")
                    .Deserialize<SpeechClientIdentity>(Json.Options)!) == Files.Fingerprint(request.Provider)
                && metadata.GetProperty("execution").GetProperty("request")
                    .Deserialize<SpeechRequest>(Json.Options) == request.Voice,
                "audio.evidence", "Retained take metadata, synthesis identity, hash or actual clock differs.");
            using var takeStream = File.OpenRead(take);
            takeStream.Position = takeRange.Offset;
            for (long left = takeRange.Bytes; left > 0;)
            {
                var count = (int)Math.Min(actual.Length, left);
                masterStream.ReadExactly(actual.AsSpan(0, count));
                takeStream.ReadExactly(expected.AsSpan(0, count));
                Guard.Require(actual.AsSpan(0, count).SequenceEqual(expected.AsSpan(0, count)),
                    "audio.samples", "Master PCM differs from the retained approved take.");
                left -= count;
            }
            for (long left = checked(part.PauseAfterSamples * 2); left > 0;)
            {
                var count = (int)Math.Min(actual.Length, left);
                masterStream.ReadExactly(actual.AsSpan(0, count));
                Guard.Require(actual.AsSpan(0, count).IndexOfAnyExcept((byte)0) < 0,
                    "audio.samples", "Approved pause contains nonzero PCM.");
                left -= count;
            }
            cursor = checked(part.EndSample + part.PauseAfterSamples);
        }
        Guard.Require(cursor == clock.Samples && masterStream.Position == masterRange.Offset + masterRange.Bytes,
            "audio.ledger", "The complete master sample clock must be covered by takes and pauses.");
        return result;
    }

    private static void Validate(AudioProductionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Guard.Require(request.SchemaVersion == 1 && Files.ValidId(request.ProjectId) && request.Revision > 0
            && Files.ValidId(request.OutputName)
            && request.SampleRate is >= 8000 and <= 192000
            && request.MaximumSamples > 0 && request.MaximumSamples <= (long)request.SampleRate * 1800,
            "audio.request", "Explicit revision, named output and bounded actual sample clock are required.");
        Guard.Require(request.Parts is { Length: > 0 and <= 64 }
            && request.Parts.All(p => p is not null && Files.ValidId(p.Id)
                && !string.IsNullOrWhiteSpace(p.Text) && p.Text.Length <= 8000
                && p.PauseAfterSamples >= 0 && p.PauseAfterSamples <= request.SampleRate * 10L)
            && request.Parts.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == request.Parts.Length
            && request.Parts.Sum(p => (long)p.Text.Length) <= 32000,
            "audio.script", "A bounded unique reviewed script and explicit sample pauses are required.");
        Guard.Require(request.Voice is not null && !string.IsNullOrWhiteSpace(request.Voice.Voice)
            && !string.IsNullOrWhiteSpace(request.Voice.Language)
            && float.IsFinite(request.Voice.Speed) && request.Voice.Speed is >= 0.5f and <= 2
            && request.Provider is not null && !string.IsNullOrWhiteSpace(request.Provider.Provider)
            && !string.IsNullOrWhiteSpace(request.Provider.ClientType) && request.Provider.Artifacts is not null,
            "audio.provider", "Explicit voice, language, speed and provider identity are required.");
    }
}
