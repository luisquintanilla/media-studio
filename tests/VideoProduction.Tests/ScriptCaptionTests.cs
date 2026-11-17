using VideoProduction;

internal static class ScriptCaptionTests
{
    public static async Task<int> Run(string root)
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        void Reject(Action action, string code)
        {
            try { action(); throw new Exception("Expected " + code); }
            catch (VideoException error) { Check(error.Code == code, $"Expected {code}, got {error.Code}"); }
        }
        async Task RejectAsync(Func<Task> action, string code)
        {
            try { await action(); throw new Exception("Expected " + code); }
            catch (VideoException error) { Check(error.Code == code, $"Expected {code}, got {error.Code}"); }
        }
        foreach (var script in new[]
        {
            "Dr. Smith uses .NET 10. C# costs $3.14; U.S. APIs work.",
            "  Unicode caf\u00e9 \u2014 preserve \ud83c\udfac. \r\nThen use UIAgent<TState>!\t",
            "Keep e.g. JSON.NET and IChatClient.SendAsync together, with the original punctuation; then stop.",
            string.Join(' ', Enumerable.Repeat("technical-name", 18))
        })
        {
            var spans = ScriptCaptions.Plan(script);
            Check(string.Concat(spans.Select(s => s.SynthesisInput)) == script, "Every canonical source character is preserved exactly");
            Check(spans.All(s => script.Substring(s.SourceStart, s.SourceLength) == s.SynthesisInput),
                "Each synthesis input is the exact declared UTF16 source span");
            Check(spans.SelectMany(s => ScriptCaptions.CaptionText(s.SynthesisInput).Split('\n')).All(l => l.Length <= 42),
                "Deterministic spans wrap without splitting code/Unicode tokens");
        }
        var abbreviations = ScriptCaptions.Plan("Dr. Smith uses $3.14 and U.S. APIs. Then stop.");
        Check(abbreviations[0].SynthesisInput == "Dr. Smith uses $3.14 and U.S. APIs. ",
            "Abbreviations, acronyms and decimal numbers do not invent sentence boundaries");
        Reject(() => ScriptCaptions.Plan(new string('x', 90)), "caption.phrase");
        Reject(() => ScriptCaptions.Plan("No --> injected timeline"), "caption.script");
        Reject(() => new AuthoredNarrationPolicy(2, AuthoredNarrationPolicy.ScriptPhrases).Validate(), "narration.policy");
        var projectRoot = Path.Combine(root, "script-captions");
        Directory.CreateDirectory(projectRoot);
        const string canonical = "First phrase. Second phrase.";
        var backend = new FixtureBackend([(8000, 16000, "voice"), (8000, 24000, "voice")]);
        var result = await ScriptCaptions.Synthesize(projectRoot, Path.Combine(projectRoot, "takes"), canonical, backend, 100, default);
        Check(backend.Calls == 2, "Exactly one synthesis per planned phrase, with no resynthesis or hidden ASR");
        Check(result.Take.SampleCount == 40800 && result.Receipt.ContentSamples == 40000 && result.Receipt.EndGuardSamples == 800,
            "Actual distinct phrase sample clocks compose exactly and one scene-only guard is appended");
        Check(result.Receipt.Cues.SequenceEqual(new[]
        {
            new Cue(0, 16000, "First phrase.", Evidence.TakeWindow),
            new Cue(16000, 40000, "Second phrase.", Evidence.TakeWindow)
        }), "Captions use actual individual returned boundaries; end guard does not stretch phrase captions");
        var assets = Directory.GetFiles(projectRoot, "*", SearchOption.AllDirectories).Order()
            .Select((f, i) => new Asset("asset-" + i, Path.GetRelativePath(projectRoot, f).Replace('\\', '/'),
                Files.Hash(f), "deterministic fixture; no real inference", "MIT")).ToArray();
        var narration = new Narration(assets.Single(a => Files.Under(projectRoot, a.Path) == result.Take.WavPath).Id,
            8000, 40800, canonical, result.Receipt.Cues,
            ScriptCaptionAssetId: assets.Single(a => Files.Under(projectRoot, a.Path) == result.ReceiptPath).Id);
        var scene = new Scene("scene", SceneKind.Title, "Measured", "PCM fixture", canonical,
            TimingMaster.Narration, 0, false, null, narration, []);
        var project = new Project(1, "script-caption-fixture", 1920, 1080, new(30, 1), assets, [scene],
            NarrationPolicy: new(1, AuthoredNarrationPolicy.ScriptPhrases));
        Validation.Validate(project, projectRoot);
        var inspected = Json.Text(ScriptCaptions.Inspect(project, projectRoot));
        Check(inspected.Contains("fixture-only-not-real-inference", StringComparison.Ordinal)
            && inspected.Contains("\"qaStatus\": \"not-run\"", StringComparison.Ordinal),
            "Production inspector shows actual provider/model evidence and honestly labels absent QA not-run");
        Check(true, "Frozen script-caption project validates actual original phrase PCM, provider receipts and composed master");
        var timeline = Validation.Resolve(project, "fixture", _ => null);
        var projected = Captions.Project(project, timeline);
        Check(projected[0].StartMilliseconds == 0 && projected[0].EndMilliseconds == 2000
            && projected[1].StartMilliseconds == 2000 && projected[1].EndMilliseconds == 5000,
            "Preview/frame/delivery projection consumes the same measured cues with no invented guard timing");
        Check(Captions.Render(projected, true).Contains("00:00:02.000 --> 00:00:05.000", StringComparison.Ordinal)
            && Captions.Render(projected, false).Contains("00:00:02,000 --> 00:00:05,000", StringComparison.Ordinal),
            "VTT and SRT preserve actual PCM boundaries");
        Reject(() => Validation.Validate(project with { Scenes = [scene with { Script = canonical + " Changed." }] }, projectRoot),
            "caption.stale");
        Reject(() => Validation.Validate(project with {
            Scenes = [scene with { Narration = narration with
            { Cues = [narration.Cues[0] with { Text = "Invented" }, narration.Cues[1]] } }]
        }, projectRoot), "caption.stale");
        var receiptBytes = File.ReadAllBytes(result.ReceiptPath);
        var tampered = result.Receipt with {
            Phrases = [result.Receipt.Phrases[0],
            result.Receipt.Phrases[1] with { StartSample = 16001 }]
        };
        Json.Write(result.ReceiptPath, tampered);
        var altered = project with {
            Assets = assets.Select(a => a.Id == narration.ScriptCaptionAssetId
            ? a with { Sha256 = Files.Hash(result.ReceiptPath) } : a).ToArray()
        };
        Reject(() => Validation.Validate(altered, projectRoot), "caption.composition");
        File.WriteAllBytes(result.ReceiptPath, receiptBytes);
        var masterBytes = File.ReadAllBytes(result.Take.WavPath);
        var duplicateFormat = masterBytes[..36].Concat(masterBytes[12..36]).Concat(masterBytes[36..]).ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(duplicateFormat.AsSpan(4, 4), (uint)duplicateFormat.Length - 8);
        var duplicateFormatFile = Path.Combine(root, "caption-duplicate-format.wav");
        File.WriteAllBytes(duplicateFormatFile, duplicateFormat);
        Reject(() => Wav.PcmRange(duplicateFormatFile), "wav.data");
        var changed = masterBytes.ToArray(); changed[44] ^= 1;
        File.WriteAllBytes(result.Take.WavPath, changed);
        var masterHash = Files.Hash(result.Take.WavPath);
        Json.Write(result.ReceiptPath, result.Receipt with { Master = result.Receipt.Master with { Sha256 = masterHash } });
        altered = project with {
            Assets = assets.Select(a => a.Id == narration.AssetId ? a with { Sha256 = masterHash }
            : a.Id == narration.ScriptCaptionAssetId ? a with { Sha256 = Files.Hash(result.ReceiptPath) } : a).ToArray()
        };
        Reject(() => Validation.Validate(altered, projectRoot), "caption.composition");
        File.WriteAllBytes(result.Take.WavPath, masterBytes);
        File.WriteAllBytes(result.ReceiptPath, receiptBytes);
        var shortTail = new[]
        {
            new MeasuredPhrase(new(0, 7, "Alpha. "), 0, 16000, new("unused", "unused"), new("unused", "unused"), "unused"),
            new MeasuredPhrase(new(7, 3, "OK."), 16000, 2000, new("unused", "unused"), new("unused", "unused"), "unused")
        };
        var grouped = ScriptCaptions.BuildCues(shortTail, 8000);
        Check(grouped is [{ StartSample: 0, EndSample: 18000, Text: "Alpha. OK." }],
            "A short trailing phrase groups over known measured boundaries rather than failing a greedy partition");
        Reject(() => ScriptCaptions.BuildCues([shortTail[0] with { SampleCount = 56000 }], 8000), "caption.measured-policy");
        var overlong = new FixtureBackend([(8000, 56000, "voice")]);
        var failedRoot = Path.Combine(projectRoot, "overlong");
        await RejectAsync(() => ScriptCaptions.Synthesize(projectRoot, failedRoot, "Too long.", overlong, 0, default),
            "caption.measured-policy");
        Check(overlong.Calls == 1 && File.Exists(Path.Combine(failedRoot, "caption-policy-failure.json"))
            && File.Exists(Path.Combine(failedRoot, "narration.wav")) && !File.Exists(Path.Combine(failedRoot, "script-captions.json")),
            "Overlong measured take retains explicit failure/audio evidence without fictional subdivision or retry");
        await RejectAsync(() => ScriptCaptions.Synthesize(projectRoot, Path.Combine(projectRoot, "wrong-format"), canonical,
            new FixtureBackend([(8000, 16000, "voice"), (16000, 32000, "voice")]), 0, default), "caption.format");
        await RejectAsync(() => ScriptCaptions.Synthesize(projectRoot, Path.Combine(projectRoot, "wrong-provider"), canonical,
            new FixtureBackend([(8000, 16000, "voice"), (8000, 16000, "another")]), 0, default), "caption.provider");
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = new FixtureBackend([(8000, 16000, "voice")], cancellation);
            var directory = Path.Combine(projectRoot, "cancelled");
            try
            {
                await ScriptCaptions.Synthesize(projectRoot, directory, canonical, cancelled, 0, cancellation.Token);
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException)
            {
                Check(cancelled.Calls == 1 && !File.Exists(Path.Combine(directory, "narration.wav")),
                "Cancellation after an actual phrase preserves the take and stops before another synthesis/master commit");
            }
        }
        var legacy = Json.Text(project with {
            NarrationPolicy = null,
            Scenes = [scene with
            { Narration = narration with { ScriptCaptionAssetId = null, SpeechAnalysisAssetId = null } }]
        });
        Check(!legacy.Contains("narrationPolicy", StringComparison.Ordinal) && !legacy.Contains("scriptCaptionAssetId", StringComparison.Ordinal)
            && !legacy.Contains("speechAnalysisAssetId", StringComparison.Ordinal), "Omitted policy/receipts do not alter legacy serialization or fingerprints");
        var projectFile = Path.Combine(projectRoot, "project.json");
        Json.Write(projectFile, project);
        var production = new Production(projectFile, new Tools(projectRoot, "unused", "unused", "unused", null));
        await RejectAsync(() => production.Transcribe("scene", new("unused", "unused", [], "unused", "unused", "unused", "unused"), default),
            "transcription.script-mode");
        var before = File.ReadAllBytes(projectFile);
        var qa = await production.AnalyzeSpeech("scene",
            new("unused", "unused", [], "unused", "unused", "unused", "unused", TimeoutSeconds: 0), default);
        var analyzed = production.Load();
        Check(qa.Status == "failed" && qa.ErrorCode == "transcription.bounds" && qa.TranscriptAssetId is null,
            "Optional QA failure is explicitly recorded, not silently reported as passed or adopted");
        Check(Files.Fingerprint(analyzed.Scenes[0].Narration! with { SpeechAnalysisAssetId = null }) == Files.Fingerprint(narration)
            && analyzed.Scenes[0].Script == canonical && !analyzed.Scenes[0].Narration!.TranscriptPending
            && analyzed.Scenes[0].Narration!.TranscriptAssetId is null,
            "Failed optional QA preserves canonical script, exact cues, master and strict transcript/adoption state");
        Check(before.Length > 0 && analyzed.Scenes[0].Narration!.SpeechAnalysisAssetId is not null,
            "Explicit analysis receipt is frozen while not-run remains an absent receipt");
        Validation.Validate(analyzed, projectRoot);
        return count;
    }

    private sealed class FixtureBackend((int Rate, int Samples, string Voice)[] takes,
        CancellationTokenSource? cancelAfterTake = null) : INarrationBackend
    {
        public int Calls { get; private set; }
        public Task<NarrationTake> Synthesize(string text, string outputDirectory, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var spec = takes[Calls++];
            Directory.CreateDirectory(outputDirectory);
            var wav = Path.Combine(outputDirectory, "narration.wav");
            using (var output = File.Create(wav))
            using (var writer = new BinaryWriter(output))
            {
                Wav.WriteHeader(writer, spec.Rate, spec.Samples);
                for (var i = 0; i < spec.Samples; i++) writer.Write((short)(Calls * 100 + i % 31));
            }
            var hash = Files.Hash(wav);
            var metadata = Path.Combine(outputDirectory, "speech.json");
            Json.Write(metadata, new
            {
                schemaVersion = 1, displayText = text, sampleRate = spec.Rate, sampleCount = (long)spec.Samples,
                wavSha256 = hash, modelId = "fixture-only-not-real-inference",
                execution = new
                {
                    client = new { provider = "deterministic-fixture" },
                    request = new { voice = spec.Voice, language = "en-us", speed = 1 }
                }
            });
            cancelAfterTake?.Cancel();
            return Task.FromResult(new NarrationTake(wav, hash, spec.Rate, spec.Samples, text, metadata));
        }
    }
}
