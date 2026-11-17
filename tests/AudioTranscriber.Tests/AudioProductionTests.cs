using Microsoft.Extensions.AI;
using MediaStudio.Audio;
using VideoProduction;

#pragma warning disable MEAI001
namespace AudioTranscriber.Tests;

public sealed class AudioProductionTests
{
    [Fact]
    public async Task Audio_only_commits_playable_pcm_with_actual_clocks_and_no_visual_setup()
    {
        var destination = Environment.GetEnvironmentVariable("MEDIA_SAMPLE_FIXTURE_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "media-audio-" + Guid.NewGuid().ToString("N"));
        using var client = new SyntheticToneClient();
        var request = Request(destination);
        var approval = AudioProduction.Approve(request, "offline-fixture-review");
        try
        {
            var result = await AudioProduction.Produce(request, approval,
                new MeaiNarrationBackend(client, request.Voice, request.Provider), destination, CancellationToken.None);
            Assert.Equal(2, client.Calls);
            Assert.Equal(20000, result.SampleCount);
            Assert.Equal(16000, result.SampleRate);
            Assert.Equal("audio-candidate", result.Kind);
            Assert.Equal(0, result.Parts[0].StartSample);
            Assert.Equal(8000, result.Parts[0].EndSample);
            Assert.Equal(10000, result.Parts[1].StartSample);
            Assert.Equal(18000, result.Parts[1].EndSample);
            Assert.Equal(result.MasterSha256, AudioProduction.Verify(destination).MasterSha256);
            Assert.Equal(40044, new FileInfo(Path.Combine(destination, "master.wav")).Length);
            Assert.DoesNotContain(typeof(AudioProduction).Assembly.GetReferencedAssemblies(),
                assembly => assembly.Name?.Contains("VideoProduction", StringComparison.Ordinal) == true);
            await Assert.ThrowsAsync<VideoException>(() => AudioProduction.Produce(request, approval,
                new MeaiNarrationBackend(client, request.Voice, request.Provider), destination, CancellationToken.None));
            Assert.Equal(2, client.Calls);
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("MEDIA_SAMPLE_FIXTURE_ROOT") is null && Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task Awaited_backend_cannot_mutate_the_approved_request_arrays()
    {
        var destination = Path.Combine(Path.GetTempPath(), "media-audio-" + Guid.NewGuid().ToString("N"));
        using var client = new SyntheticToneClient();
        var request = Request(destination);
        var approval = AudioProduction.Approve(request, "reviewer");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new InterceptedNarration(new MeaiNarrationBackend(client, request.Voice, request.Provider),
            async (_, _) => { entered.TrySetResult(); await release.Task; });
        try
        {
            var work = AudioProduction.Produce(request, approval, backend, destination, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            request.Parts[1] = request.Parts[1] with { Text = "Unapproved replacement while synthesis awaits." };
            release.TrySetResult();
            var result = await work;
            Assert.Equal(approval.RequestSha256, result.RequestSha256);
            Assert.Equal("This tests independent audio composition and exact sample clocks only.", result.Parts[1].Text);
            Assert.Equal(result.MasterSha256, AudioProduction.Verify(destination).MasterSha256);
        }
        finally { if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true); }
    }

    [Theory]
    [InlineData(19999, false)]
    [InlineData(20000, true)]
    public async Task Actual_sample_bound_is_exact_and_overrun_never_truncates_or_commits(long maximumSamples, bool fits)
    {
        var ownedRoot = Directory.CreateTempSubdirectory("media-audio-bound-").FullName;
        var destination = Path.Combine(ownedRoot, "candidate");
        using var client = new SyntheticToneClient();
        var request = Request(destination) with { MaximumSamples = maximumSamples };
        try
        {
            var backend = new MeaiNarrationBackend(client, request.Voice, request.Provider);
            var approval = AudioProduction.Approve(request, "explicit-bound-review");
            if (fits)
            {
                var result = await AudioProduction.Produce(request, approval, backend, destination, CancellationToken.None);
                Assert.Equal(20000, result.SampleCount);
                Assert.Equal(20000, AudioProduction.Verify(destination).SampleCount);
            }
            else
            {
                var error = await Assert.ThrowsAsync<VideoException>(() =>
                    AudioProduction.Produce(request, approval, backend, destination, CancellationToken.None));
                Assert.Equal("audio.bounds", error.Code);
                Assert.Contains("20000 samples", error.Message);
                Assert.Contains("19999 samples", error.Message);
                Assert.Contains("No audio was truncated", error.Message);
                Assert.False(Directory.Exists(destination));
                Assert.Empty(Directory.GetDirectories(ownedRoot));
            }
            Assert.Equal(2, client.Calls);
        }
        finally { Directory.Delete(ownedRoot, recursive: true); }
    }

    [Fact]
    public async Task Lying_metadata_never_becomes_a_committed_candidate()
    {
        var ownedRoot = Directory.CreateTempSubdirectory("media-audio-evidence-").FullName;
        var destination = Path.Combine(ownedRoot, "candidate");
        using var client = new SyntheticToneClient();
        var request = Request(destination);
        var backend = new InterceptedNarration(new MeaiNarrationBackend(client, request.Voice, request.Provider),
            (_, take) =>
            {
                var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(take.MetadataPath))!.AsObject();
                metadata["sampleCount"] = 1;
                File.WriteAllText(take.MetadataPath, metadata.ToJsonString());
                return Task.CompletedTask;
            });
        try
        {
            await Assert.ThrowsAsync<VideoException>(() => AudioProduction.Produce(request,
                AudioProduction.Approve(request, "reviewer"), backend, destination, CancellationToken.None));
            Assert.False(Directory.Exists(destination));
            Assert.Empty(Directory.GetDirectories(ownedRoot));
        }
        finally { Directory.Delete(ownedRoot, recursive: true); }
    }

    [Fact]
    public void Outcome_union_requires_an_explicit_case_and_uses_a_stable_dto()
    {
        AudioOutcome complete = new AudioCompleted(new string('a', 64), 20000);
        Assert.Equal("completed", AudioOutcomeDto.From(complete).Status);
        AudioOutcome failure = new AudioFailed("audio.integrity", "Changed evidence");
        Assert.Equal("audio.integrity", AudioOutcomeDto.From(failure).Code);
        AudioOutcome cancelled = new AudioCancelled("Operator requested");
        Assert.Equal("cancelled", AudioOutcomeDto.From(cancelled).Status);
        Assert.Throws<InvalidDataException>(() => AudioOutcomeDto.From(default));
    }

    [Fact]
    public async Task Changed_take_or_ledger_is_not_a_verified_audio_export()
    {
        var destination = Path.Combine(Path.GetTempPath(), "media-audio-" + Guid.NewGuid().ToString("N"));
        using var client = new SyntheticToneClient();
        var request = Request(destination);
        try
        {
            var result = await AudioProduction.Produce(request, AudioProduction.Approve(request, "reviewer"),
                new MeaiNarrationBackend(client, request.Voice, request.Provider), destination, CancellationToken.None);
            var metadata = Files.Under(destination, result.Parts[0].MetadataPath);
            var saved = File.ReadAllBytes(metadata);
            File.AppendAllText(metadata, " ");
            Assert.Throws<VideoException>(() => AudioProduction.Verify(destination));
            File.WriteAllBytes(metadata, saved);
            var changedParts = result.Parts.ToArray();
            changedParts[0] = changedParts[0] with { StartSample = 1 };
            Json.Write(Path.Combine(destination, "manifest.json"), result with { Parts = changedParts });
            Assert.Throws<VideoException>(() => AudioProduction.Verify(destination));
        }
        finally { if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true); }
    }

    [Fact]
    public async Task Changed_revision_voice_or_bounds_never_reuses_audio_approval()
    {
        var destination = Path.Combine(Path.GetTempPath(), "media-audio-" + Guid.NewGuid().ToString("N"));
        var request = Request(destination);
        var approval = AudioProduction.Approve(request, "reviewer");
        using var client = new SyntheticToneClient();
        foreach (var changed in new[]
        {
            request with { Revision = 2 },
            request with { Voice = request.Voice with { Voice = "different" } },
            request with { MaximumSamples = request.MaximumSamples + 1 }
        })
            await Assert.ThrowsAsync<VideoException>(() => AudioProduction.Produce(changed, approval,
                new MeaiNarrationBackend(client, request.Voice, request.Provider), destination, CancellationToken.None));
        Assert.Equal(0, client.Calls);
        Assert.False(Directory.Exists(destination));
    }

    private static AudioProductionRequest Request(string destination) => new(1, "synthetic-audio", 1,
        "audio-master", 16000, 32000,
        [new("introduction", "Synthetic fixture: two audible tones, not synthesized speech.", 2000),
         new("conclusion", "This tests independent audio composition and exact sample clocks only.", 2000)],
        new("synthetic-tone", "en", 1),
        new("synthetic-fixture-not-tts", typeof(SyntheticToneClient).FullName!, []));

    private sealed class SyntheticToneClient : ITextToSpeechClient
    {
        public int Calls { get; private set; }
        public Task<TextToSpeechResponse> GetAudioAsync(string text, TextToSpeechOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                Wav.WriteHeader(writer, 16000, 8000);
                for (var sample = 0; sample < 8000; sample++)
                    writer.Write((short)(Math.Sin(2 * Math.PI * (Calls == 1 ? 440 : 660) * sample / 16000) * 4000));
            }
            return Task.FromResult(new TextToSpeechResponse([new DataContent(stream.ToArray(), "audio/wav")]) { ModelId = "synthetic-sine-fixture-v1" });
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(string text,
            TextToSpeechOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This offline fixture exercises measured whole-take synthesis only.");
        public void Dispose() { }
    }

    private sealed class InterceptedNarration(INarrationBackend inner,
        Func<string, NarrationTake, Task> intercept) : INarrationBackend
    {
        public async Task<NarrationTake> Synthesize(string text, string outputDirectory, CancellationToken ct)
        {
            var take = await inner.Synthesize(text, outputDirectory, ct);
            await intercept(text, take);
            return take;
        }
    }
}
