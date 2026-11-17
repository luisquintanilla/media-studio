using MediaStudio.Audio;
using VideoProduction;
using VideoProduction.Providers;

internal static class AudioAdmissionTests
{
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var state = Path.Combine(root, "independent-audio-state");
        var store = new FileProductionStore(state);
        var other = new FileProductionStore(state);
        var backend = new GatedNarration();
        var request = new AudioProductionRequest(1, "reviewed-audio", 1, "audio-master", 16000, 32000,
            [new("explanation", "The saved revision is the authority boundary, not the current text box.", 2000)],
            new("synthetic-not-speech", "en"), new("synthetic-no-model", typeof(GatedNarration).FullName!, []));
        var reviewed = new ReviewedAudioRequest(1, "audio-once", request, AudioProduction.Approve(request, "offline-reviewer"));
        FileAttemptStatus? accepted = null;
        var work = store.ProduceAudio(reviewed, backend, status => accepted ??= status);
        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(accepted is { Status: "running", Live: true }, "Audio is durably admitted before synthesis returns.");
        Check(other.Status(accepted!.AttemptId).Live, "A fresh observer verifies the existing audio producer.");
        var replay = await other.ProduceAudio(reviewed, backend);
        Check(replay.Replay && replay.AttemptId == accepted.AttemptId && backend.Calls == 1,
            "Exact active audio replay never duplicates synthesis.");
        try { await other.ProduceAudio(reviewed with { IdempotencyKey = "second-audio" }, backend); throw new Exception("Expected busy"); }
        catch (VideoException error) { Check(error.Code == "attempt.busy", "Audio shares the existing exclusive producer admission."); }
        backend.Release.TrySetResult();
        var result = await work;
        Check(result.Status == "completed" && result.Artifacts.Any(a => a.Kind == "audio-master")
            && result.Artifacts.All(a => a.Kind.StartsWith("audio-", StringComparison.Ordinal)),
            "Audio completion commits audio-only hashed artifact handles, without visual setup.");
        var master = AudioProduction.Verify(Path.Combine(state, "attempts", result.AttemptId, "audio-master"));
        Check(master.SampleCount == 10000, "Tracked export retains actual take and pause clocks.");
        replay = await other.ProduceAudio(reviewed, backend);
        Check(replay is { Replay: true, Status: "completed" } && backend.Calls == 1, "Terminal replay reopens, never reexecutes.");
        try
        {
            var changed = request with { Revision = 2 };
            await other.ProduceAudio(reviewed with { Request = changed, Approval = AudioProduction.Approve(changed, "offline-reviewer") }, backend);
            throw new Exception("Expected conflict");
        }
        catch (VideoException error) { Check(error.Code == "attempt.idempotency-conflict", "A key cannot authorize a different saved audio revision."); }
        var cancelling = new GatedNarration();
        var cancelWork = other.ProduceAudio(reviewed with { IdempotencyKey = "explicit-audio-cancel" }, cancelling,
            status => accepted = status);
        await cancelling.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        store.Cancel(accepted!.AttemptId);
        var cancelled = await cancelWork.WaitAsync(TimeSpan.FromSeconds(10));
        Check(cancelled.Status == "cancelled" && cancelled.Artifacts.Length == 0,
            "Explicit persisted audio cancellation cannot publish a partial master.");
        Check(TeachingGuidance.Instructions.Contains("self-contained", StringComparison.Ordinal)
            && TeachingGuidance.Identity.Name == "media-explanation", "Reviewed canonical guidance is packaged and hash-checked.");
        return count;
    }

    private sealed class GatedNarration : INarrationBackend
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public async Task<NarrationTake> Synthesize(string text, string directory, CancellationToken ct)
        {
            Calls++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            Directory.CreateDirectory(directory);
            var wav = Path.Combine(directory, "narration.wav");
            using (var stream = File.Create(wav))
            using (var writer = new BinaryWriter(stream))
            {
                Wav.WriteHeader(writer, 16000, 8000);
                for (var sample = 0; sample < 8000; sample++)
                    writer.Write((short)(Math.Sin(2 * Math.PI * 440 * sample / 16000) * 4000));
            }
            var metadata = Path.Combine(directory, "speech.json");
            var hash = Files.Hash(wav);
            Json.Write(metadata, new
            {
                schemaVersion = 1, wavSha256 = hash, sampleCount = 8000, displayText = text,
                execution = new
                {
                    client = new SpeechClientIdentity("synthetic-no-model", GetType().FullName!, []),
                    request = new SpeechRequest("synthetic-not-speech", "en")
                }
            });
            return new(wav, hash, 16000, 8000, text, metadata);
        }
    }
}
