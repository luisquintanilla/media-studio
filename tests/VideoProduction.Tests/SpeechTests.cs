using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using VideoProduction;

#pragma warning disable MEAI001
internal static class SpeechTests
{
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        async Task Reject(Func<Task> action, string code)
        {
            try { await action(); throw new Exception("Expected " + code); }
            catch (VideoException error) { Check(error.Code == code, error.Code + " instead of " + code); }
        }
        var wav = Wave();
        var request = new SpeechRequest("explicit-voice", "en-us", 0.9f);
        using var alternate = new AlternateSpeechClient((text, options, ct) =>
        {
            Check(text == "Actual interface.", "Original text delivered unchanged at the official interface");
            Check(options is { VoiceId: "explicit-voice", Language: "en-us", Speed: 0.9f, Volume: 1, AudioFormat: "wav" },
                "Explicit speech semantics mapped to actual MEAI options");
            Check(ct == CancellationToken.None, "Original cancellation token propagated");
            options!.VoiceId = "client mutation";
            return Task.FromResult(Response(wav));
        });
        var identity = new SpeechClientIdentity("alternate-test-provider", alternate.GetType().FullName!, []);
        var backend = new MeaiNarrationBackend(alternate, request, identity);
        var first = await backend.Synthesize("Actual interface.", Path.Combine(root, "meai-first"), CancellationToken.None);
        var second = await backend.Synthesize("Actual interface.", Path.Combine(root, "meai-second"), CancellationToken.None);
        Check(alternate.Calls == 2, "One synthesis call per take, with fresh options despite client mutation");
        Check(first.SampleRate == 24000 && first.SampleCount == 24000 && first.WavSha256 == Files.Hash(first.WavPath),
            "Returned actual PCM16 sample clock/hash, not guessed text duration");
        Check(File.ReadAllBytes(first.WavPath).SequenceEqual(wav), "Exact MEAI audio bytes adopted, not synthesized twice");
        using var json = JsonDocument.Parse(File.ReadAllText(first.MetadataPath));
        var metadata = json.RootElement;
        Check(metadata.GetProperty("execution").GetProperty("path").GetString() == "meai-in-process-v1"
            && metadata.GetProperty("execution").GetProperty("abstraction").GetString() == "Microsoft.Extensions.AI.ITextToSpeechClient"
            && metadata.GetProperty("execution").GetProperty("packageVersion").GetString() == "10.11.0",
            "New execution path records real official abstraction/version");
        Check(metadata.GetProperty("providerEvidence").ValueKind == JsonValueKind.Null
            && metadata.GetProperty("timingEvidence").GetString() == "whole-returned-take-sample-range-only",
            "Alternate provider without raw metadata does not invent chunk/word evidence");
        Check(metadata.GetProperty("wavPath").GetString() == "narration.wav"
            && !File.ReadAllText(first.MetadataPath).Contains(root, StringComparison.Ordinal), "Evidence relocates without absolute source paths");
        using var secondJson = JsonDocument.Parse(File.ReadAllText(second.MetadataPath));
        var firstKey = metadata.GetProperty("execution").GetProperty("cacheIdentity").GetString();
        Check(firstKey == secondJson.RootElement.GetProperty("execution").GetProperty("cacheIdentity").GetString(),
            "Same client/request/evidence has stable MEAI identity");
        var other = new MeaiNarrationBackend(alternate, request, identity with { Provider = "other-provider" });
        var third = await other.Synthesize("Actual interface.", Path.Combine(root, "meai-third"), CancellationToken.None);
        using var thirdJson = JsonDocument.Parse(File.ReadAllText(third.MetadataPath));
        Check(firstKey != thirdJson.RootElement.GetProperty("execution").GetProperty("cacheIdentity").GetString(),
            "Different provider cannot masquerade as cached original speech");
        await Reject(() => backend.Synthesize("Actual interface.", Path.GetDirectoryName(first.WavPath)!, CancellationToken.None), "narration.immutable");
        Check(alternate.Calls == 3, "Existing output rejected before inference");
        var invalidRequest = new MeaiNarrationBackend(alternate, request with { Speed = float.NaN }, identity);
        await Reject(() => invalidRequest.Synthesize("Text", Path.Combine(root, "meai-invalid-options"), CancellationToken.None), "speech.options");
        await Reject(() => new MeaiNarrationBackend(alternate, request, identity with { ClientType = "not-the-real-client" })
            .Synthesize("Text", Path.Combine(root, "meai-invalid-identity"), CancellationToken.None), "speech.identity");
        foreach (var invalid in new[]
        {
            new TextToSpeechResponse { ModelId = "model" },
            new TextToSpeechResponse([new DataContent(wav, "audio/mp3")]) { ModelId = "model" },
            new TextToSpeechResponse([new DataContent(wav, "audio/wav"), new DataContent(wav, "audio/wav")]) { ModelId = "model" },
            new TextToSpeechResponse([new DataContent(wav, "audio/wav")])
        })
        {
            using var fake = new AlternateSpeechClient((_, _, _) => Task.FromResult(invalid));
            var rejected = new MeaiNarrationBackend(fake, request, identity);
            await Reject(() => rejected.Synthesize("Text", Path.Combine(root, Guid.NewGuid().ToString("N")), CancellationToken.None), "speech.audio");
        }
        var corrupt = wav.ToArray();
        corrupt[20] = 3;
        using (var fake = new AlternateSpeechClient((_, _, _) => Task.FromResult(Response(corrupt))))
            await Reject(() => new MeaiNarrationBackend(fake, request, identity).Synthesize("Text",
                Path.Combine(root, "meai-invalid-pcm"), CancellationToken.None), "wav.encoding");
        using (var fake = new AlternateSpeechClient((_, _, _) => throw new NotSupportedException("No provider fallback")))
            await Reject(() => new MeaiNarrationBackend(fake, request, identity).Synthesize("Text",
                Path.Combine(root, "meai-client-error"), CancellationToken.None), "speech.client");
        using (var fake = new AlternateSpeechClient((_, _, _) => Task.FromResult(Response(wav))))
            await Reject(() => new MeaiNarrationBackend(fake, request, identity, NarrationSpeechClient.KokoroEvidence)
                .Synthesize("Text", Path.Combine(root, "meai-missing-kokoro-evidence"), CancellationToken.None), "speech.evidence");
        Check(!Directory.Exists(Path.Combine(root, "meai-missing-kokoro-evidence")), "Missing Kokoro raw evidence fails before committing media");
        const string originalText = ".NET C#";
        const string actualSpokenText = "dot net C sharp";
        JsonObject Evidence() => JsonSerializer.SerializeToNode(new
        {
            displayText = originalText, spokenText = actualSpokenText, sampleRate = 24000, sampleCount = 24000L,
            chunks = new[] { new { startSample = 0L, sampleCount = 24000L } },
            provenance = new
            {
                modelId = "alternate-local-test-model", voiceId = request.Voice, language = request.Language,
                speed = request.Speed, gain = 1f, modelSha256 = Files.HashText("model"), voiceSha256 = Files.HashText("voice"),
                frontendSha256 = Files.HashText("frontend")
            }
        }, Json.Options)!.AsObject();
        using (var fake = new AlternateSpeechClient((_, _, _) => Task.FromResult(Response(wav))))
        {
            var normalizerCalls = 0;
            var normalized = new MeaiNarrationBackend(fake, request, identity, _ => Evidence(), input =>
            {
                Check(input == originalText, "Provider normalizer receives unchanged display input");
                normalizerCalls++;
                return actualSpokenText;
            });
            var take = await normalized.Synthesize(originalText, Path.Combine(root, "meai-normalized"), CancellationToken.None);
            using var result = JsonDocument.Parse(File.ReadAllText(take.MetadataPath));
            var raw = result.RootElement.GetProperty("providerEvidence");
            Check(normalizerCalls == 1 && fake.Calls == 1
                && raw.GetProperty("displayText").GetString() == originalText
                && raw.GetProperty("spokenText").GetString() == actualSpokenText
                && File.ReadAllBytes(take.WavPath).SequenceEqual(wav),
                "Exact declared normalization retains distinct real display/spoken evidence without altering WAV or synthesizing twice");
        }
        foreach (var (field, mutate) in new (string, Action<JsonObject>)[]
        {
            ("displayText", e => e["displayText"] = "Changed display input"),
            ("spokenText", e => e["spokenText"] = originalText),
            ("sampleRate", e => e["sampleRate"] = 48000),
            ("sampleCount", e => e["sampleCount"] = 23999),
            ("provenance.modelId", e => e["provenance"]!["modelId"] = "other-model"),
            ("provenance.voiceId", e => e["provenance"]!["voiceId"] = "other-voice"),
            ("provenance.language", e => e["provenance"]!["language"] = "en-gb"),
            ("provenance.speed", e => e["provenance"]!["speed"] = 1),
            ("provenance.gain", e => e["provenance"]!["gain"] = 0.5),
            ("provenance.modelSha256", e => e["provenance"]!["modelSha256"] = "not-a-hash"),
            ("provenance.voiceSha256", e => e["provenance"]!["voiceSha256"] = "not-a-hash"),
            ("provenance.frontendSha256", e => e["provenance"]!["frontendSha256"] = "not-a-hash"),
            ("chunk", e => e["chunks"]![0]!["startSample"] = 1)
        })
        {
            using var fake = new AlternateSpeechClient((_, _, _) => Task.FromResult(Response(wav)));
            var directory = Path.Combine(root, "meai-evidence-" + field);
            var raw = Evidence();
            mutate(raw);
            var invalid = new MeaiNarrationBackend(fake, request, identity,
                _ => JsonNode.Parse(raw.ToJsonString(Json.Options))!.AsObject(), _ => actualSpokenText);
            try { await invalid.Synthesize(originalText, directory, CancellationToken.None); throw new Exception("Invalid evidence accepted"); }
            catch (VideoException error)
            {
                Check(error.Code == (field == "chunk" ? "speech.chunks" : "speech.evidence")
                    && error.Message.Contains(field, StringComparison.OrdinalIgnoreCase), "Field-specific strict evidence rejection: " + field);
            }
            using var failure = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "speech-validation-failure.json")));
            Check(!failure.RootElement.GetProperty("ok").GetBoolean()
                && JsonNode.DeepEquals(JsonNode.Parse(failure.RootElement.GetProperty("providerEvidence").GetRawText()), raw)
                && failure.RootElement.GetProperty("expected").GetProperty("spokenText").GetString() == actualSpokenText
                && !File.Exists(Path.Combine(directory, "speech.json")) && fake.Calls == 1,
                "Invalid " + field + " retains raw evidence but never commits a success or retries synthesis");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { await backend.Synthesize("Text", Path.Combine(root, "meai-cancelled"), cancellation.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { Check(alternate.Calls == 3, "Pre-cancellation performs no inference"); }
        }
        using (var cancellation = new CancellationTokenSource())
        using (var fake = new AlternateSpeechClient(async (_, _, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(1000, ct);
            return Response(wav);
        }))
        {
            try
            {
                await new MeaiNarrationBackend(fake, request, identity).Synthesize("Text",
                    Path.Combine(root, "meai-inflight-cancelled"), cancellation.Token);
                throw new Exception("Inflight cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                Check(!Directory.Exists(Path.Combine(root, "meai-inflight-cancelled")),
                "In-flight cancellation propagated, no success-shaped take");
            }
        }
        var sourceMetadataHash = Files.Hash(first.MetadataPath);
        var guarded = NarrationEndGuard.Apply(first, 250, CancellationToken.None);
        var guardedBytes = File.ReadAllBytes(guarded.WavPath);
        Check(guarded.SampleCount == 30000 && Wav.Inspect(guarded.WavPath).Samples == 30000,
            "Explicit250ms end guard is6000 real extra24kHz samples, not invented duration");
        Check(guardedBytes.AsSpan(44, wav.Length - 44).SequenceEqual(wav.AsSpan(44))
            && guardedBytes.AsSpan(wav.Length).ToArray().All(b => b == 0),
            "Original nonzero PCM is byte-identical and every appended sample is actual digital silence");
        Check(File.ReadAllBytes(first.WavPath).SequenceEqual(wav) && Files.Hash(first.MetadataPath) == sourceMetadataHash
            && guarded.WavSha256 == Files.Hash(guarded.WavPath),
            "Guard keeps original provider WAV/provenance unchanged and returns the measured guarded hash");
        using (var guardJson = JsonDocument.Parse(File.ReadAllText(guarded.MetadataPath)))
            Check(guardJson.RootElement.GetProperty("originalSamples").GetInt64() == 24000
                && guardJson.RootElement.GetProperty("addedSamples").GetInt64() == 6000
                && guardJson.RootElement.GetProperty("originalPcmSha256").GetString()
                    == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(wav.AsSpan(44))),
                "Guard receipt distinguishes original PCM hash, actual added samples and new master clock");
        await Reject(() => { NarrationEndGuard.Apply(first, 250, CancellationToken.None); return Task.CompletedTask; }, "narration.immutable");
        foreach (var invalidGuard in new[] { 0, 1001 })
            await Reject(() => { NarrationEndGuard.Apply(second, invalidGuard, CancellationToken.None); return Task.CompletedTask; }, "narration.end-guard");
        Check(new SpeechConfig("kokoro", "voice", "en-us").EndGuardMilliseconds == 0,
            "Existing speech configurations keep byte-identical unguarded behavior by default");
        var legacy = new KokoroBackend(new KokoroConfig("missing", "missing", "missing", "missing", "missing"));
        await Reject(() => legacy.Synthesize("Text", Path.Combine(root, "meai-no-legacy-fallback"), CancellationToken.None), "kokoro.legacy-explicit");
        var source = Path.Combine(root, "meai-config-project.json");
        Json.Write(source, new Project(1, "meai-options", 1920, 1080, new Rate(30, 1), [],
            [new Scene("title", SceneKind.Title, "Title", "", "Text", TimingMaster.Fixed, 30, false, null, null, [])]));
        var tools = new Tools(root, "node", "ffmpeg", "ffprobe", null);
        var speechOnly = JsonSerializer.Deserialize<Tools>(
            """{"sourceRoot":".","node":"node","ffmpeg":"ffmpeg","ffprobe":"ffprobe"}""", Json.Options)!;
        Check(speechOnly.Kokoro is null && speechOnly.Speech is null, "Tools config does not require unused legacy Kokoro section");
        await Reject(() => new Production(source, tools).Narrate(CancellationToken.None), "speech.unconfigured");
        var config = new SpeechConfig("kokoro", "af_heart", "en-us");
        await Reject(() => Task.FromResult(NarrationSpeechClient.Create(config with { Provider = "unsupported-tts" })), "speech.provider");
        await Reject(() => Task.FromResult(NarrationSpeechClient.Create(config with { Voice = "" })), "speech.provider");
        var emptyAssets = new MediaStudio.Configuration.ModelAssets(MediaStudio.Configuration.ModelManifest.LoadDefault(),
            Path.Combine(root, "meai-empty-models"));
        await Reject(() => Task.FromResult(NarrationSpeechClient.Create(config, new(), emptyAssets)), "speech.load");
        await Reject(() => Task.FromResult(NarrationSpeechClient.Create(config with { Provider = "azure-speech" }, new(), emptyAssets)), "speech.load");
        var kokoroIdentity = NarrationSpeechClient.IdentityFor(config, emptyAssets.Manifest);
        Check(kokoroIdentity.ClientType == typeof(MediaStudio.Speech.Kokoro.KokoroTextToSpeechClient).FullName
            && kokoroIdentity.Artifacts.Select(a => a.Path).Order().SequenceEqual(["onnx/model_quantized.onnx", "tokenizer.json", "voices/af_heart.bin"])
            && kokoroIdentity.Artifacts.All(a => Files.ValidHash(a.Sha256)),
            "Kokoro identity pins the manifest hashes of model, tokenizer and selected voice");
        Check(Files.Fingerprint(NarrationSpeechClient.IdentityFor(config with { Voice = "am_michael" }, emptyAssets.Manifest)) != Files.Fingerprint(kokoroIdentity),
            "A different voice changes the approved identity");
        await Reject(() => new Production(source, tools with {
            Speech = config,
            Kokoro = new KokoroConfig("", "", "", "", "", Execution: "legacy-cli")
        }).Narrate(CancellationToken.None), "speech.ambiguous");
        Check(File.ReadAllBytes(first.WavPath).SequenceEqual(wav) && Files.Hash(first.MetadataPath) == Files.Hash(second.MetadataPath),
            "Frozen generated output and deterministic metadata remain unchanged by failures");
        return count;
    }

    private static TextToSpeechResponse Response(byte[] bytes) =>
        new([new DataContent(bytes, "audio/wav")]) { ModelId = "alternate-local-test-model" };
    private static byte[] Wave()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + 48000);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(24000); writer.Write(48000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(48000);
        for (var sample = 0; sample < 24000; sample++) writer.Write((short)(Math.Sin(sample * Math.PI / 12) * 4000));
        return stream.ToArray();
    }

    private sealed class AlternateSpeechClient(Func<string, TextToSpeechOptions?, CancellationToken, Task<TextToSpeechResponse>> synthesize)
        : ITextToSpeechClient
    {
        public int Calls { get; private set; }
        public Task<TextToSpeechResponse> GetAudioAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return synthesize(text, options, cancellationToken);
        }
        public IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(string text, TextToSpeechOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("Buffered test client");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
#pragma warning restore MEAI001
