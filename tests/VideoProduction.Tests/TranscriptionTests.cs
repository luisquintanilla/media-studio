using System.Text.Json;
using VideoProduction;

public static class TranscriptionTests
{
    public static int DigitalSilenceEvidence(string root)
    {
        var count = 0;
        var wav = Path.Combine(root, "stt-16khz.wav");
        using (var writer = new BinaryWriter(File.Create(wav)))
        {
            writer.Write("RIFF"u8); writer.Write(36 + 64000); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000);
            writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(64000);
            for (var sample = 0; sample < 32000; sample++) writer.Write((short)(sample < 16000 ? 1 : 0));
        }
        var hash = Files.Hash(wav);
        var audio = Wav.Inspect(wav);
        var diagnostics = Path.Combine(root, "profile.json");
        var speech = new TranscriptSegmentEvidence(0, 1000, 0, 48000, "Observed.", Evidence.AsrSegment);
        var raw = new SilenceObservation(1, " Observed. ", 0, 10000000, "speech", "Bounded speech", null);
        var marker = new SilenceObservation(2, " [BLANK_AUDIO] ", 15000000, 30000000,
            "verifiedDigitalSilence", "All intersecting actual samples are exactly zero", new(24000, 32000, 8000));
        var valid = new SilenceDiagnostics("1.0", Transcription.DigitalSilenceProfile, "accepted", null,
            new("stt-16khz.wav", hash, 16000, 1, 32000, 20000000, "pcm16"),
            new(Transcription.Model, Transcription.Provider, "synthetic-test-version", Transcription.ModelSha256),
            [raw, marker], [new(1, 0, 10000000, "Observed.")]);
        void Validate(SilenceDiagnostics value)
        {
            Json.Write(diagnostics, value);
            Transcription.ValidateDigitalSilence(diagnostics, wav, value.Input.Sha256, audio, [speech]);
        }
        void Reject(SilenceDiagnostics value)
        {
            try { Validate(value); throw new Exception("Invalid silence evidence accepted"); }
            catch (VideoException ex) when (ex.Code == "transcription.profile") { count++; }
        }
        Validate(valid); count++;
        Validate(valid with { RawObservations = [raw] }); count++;
        Reject(valid with { Profile = "unknown" });
        Reject(valid with { Status = "rejected" });
        Reject(valid with { Input = valid.Input with { SampleCount = 32001 } });
        Reject(valid with { Input = valid.Input with { Sha256 = new string('0', 64) } });
        Reject(valid with { RawObservations = [raw, marker with { Text = "Words [BLANK_AUDIO]" }] });
        Reject(valid with { RawObservations = [raw, marker with { Text = "[MUSIC]" }] });
        Reject(valid with { RawObservations = [raw, marker with { Index = 3 }] });
        Reject(valid with { RawObservations = [raw, marker with { StartTicks = 20000000 }] });
        Reject(valid with { RawObservations = [raw, marker with { StartTicks = 40000000, EndTicks = 50000000 }] });
        Reject(valid with { RawObservations = [raw with { EndTicks = 30000000 }, marker] });
        Reject(valid with { EffectiveSegments = [] });
        Reject(valid with { EffectiveSegments = [new(1, 0, 9990000, "Observed.")] });
        Reject(valid with { RawObservations = [raw, marker with { Intersection = new(24000, 32000, 0) }] });
        Reject(valid with { RawObservations = [marker with { Index = 1 }] });
        using (var stream = File.OpenWrite(wav))
        {
            stream.Position = 44 + 24000 * 2;
            stream.WriteByte(1);
        }
        Reject(valid with { Input = valid.Input with { Sha256 = Files.Hash(wav) } });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            Transcription.ValidateDigitalSilence(diagnostics, wav, Files.Hash(wav), audio, [speech], cancelled.Token);
            throw new Exception("Cancelled profile validation continued");
        }
        catch (OperationCanceledException) { count++; }
        return count;
    }

    public static int ContractAndDiscrepancy()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            count++;
        }
        void Reject(string json, string code)
        {
            try
            {
                Transcription.ParseResult(json, "stt-16khz.wav", new WavInfo(16000, 1, 32000), new WavInfo(22050, 1, 44100));
                throw new Exception("Expected " + code);
            }
            catch (VideoException ex) { Check(ex.Code == code, $"Expected {code}, got {ex.Code}"); }
        }
        const string valid = """
            [{"source":"stt-16khz.wav","model":"openai/whisper-base","provider":"Whisper.net/whisper.cpp",
              "segments":[{"start":"00:00:00.001","end":"00:00:01.001","text":"Observed speech."}]}]
            """;
        var result = Transcription.ParseResult(valid, "stt-16khz.wav",
            new WavInfo(16000, 1, 32000), new WavInfo(22050, 1, 44100));
        Check(result is [{
            StartMilliseconds: 1, EndMilliseconds: 1001, StartSample: 22, EndSample: 22073,
            Text: "Observed speech.", Evidence: Evidence.AsrSegment
        }], "exact rational outward mapping; actual segment evidence");
        var adjacent = Transcription.ParseResult("""
            [{"source":"stt-16khz.wav","model":"openai/whisper-base","provider":"Whisper.net/whisper.cpp",
              "segments":[{"start":"00:00:00.001","end":"00:00:01.001","text":"One."},
                          {"start":"00:00:01.001","end":"00:00:02.000","text":"Two."}]}]
            """, "stt-16khz.wav", new WavInfo(16000, 1, 32000), new WavInfo(22050, 1, 44100));
        Check(adjacent[0].EndSample == 22073 && adjacent[1].StartSample == 22073
            && adjacent[1].EndSample == 44100, "shared fractional boundary neither overlaps nor gaps");
        Reject(valid.Replace("\"segments\":", "\"extra\":true,\"segments\":", StringComparison.Ordinal), "transcription.contract");
        Reject(valid.Replace("\"text\":", "\"wordAlignment\":[],\"text\":", StringComparison.Ordinal), "transcription.contract");
        Reject(valid.Replace("\"text\":", "\"text\":\"duplicate\",\"text\":", StringComparison.Ordinal), "transcription.contract");
        Reject(valid.Replace("\"source\":\"stt-16khz.wav\",", "", StringComparison.Ordinal), "transcription.contract");
        Reject(valid.Replace("stt-16khz.wav", "other.wav", StringComparison.Ordinal), "transcription.identity");
        Reject(valid.Replace("whisper-base", "whisper-large", StringComparison.Ordinal), "transcription.identity");
        Reject(valid.Replace("Whisper.net/whisper.cpp", "unknown", StringComparison.Ordinal), "transcription.identity");
        Reject(valid.Replace("\"00:00:00.001\"", "0.001", StringComparison.Ordinal), "transcription.contract");
        Reject(valid.Replace("00:00:00.001", "00:60:00.001", StringComparison.Ordinal), "transcription.contract");
        Reject(valid.Replace("00:00:00.001", "00:00:00,001", StringComparison.Ordinal), "transcription.contract");
        Reject(valid.Replace("00:00:00.001", "99999999999999999999:00:00.000", StringComparison.Ordinal), "transcription.contract");
        Reject(valid.Replace("00:00:01.001", "00:00:00.001", StringComparison.Ordinal), "transcription.segment");
        Reject(valid.Replace("00:00:01.001", "00:00:02.001", StringComparison.Ordinal), "transcription.segment");
        Reject(valid.Replace("Observed speech.", " ", StringComparison.Ordinal), "transcription.segment");
        Reject(valid.Replace("""{"start":"00:00:00.001","end":"00:00:01.001","text":"Observed speech."}""", "", StringComparison.Ordinal),
            "transcription.no-speech");
        Reject(valid.Replace("\"segments\":[", "\"segments\":null,\"unused\":[", StringComparison.Ordinal), "transcription.contract");
        Reject(valid[..^1] + "," + valid[1..], "transcription.contract");
        Reject("not JSON", "transcription.contract");
        Reject("""
            [{"source":"stt-16khz.wav","model":"openai/whisper-base","provider":"Whisper.net/whisper.cpp",
              "segments":[{"start":"00:00:00.000","end":"00:00:01.000","text":"One."},
                          {"start":"00:00:00.999","end":"00:00:02.000","text":"Two."}]}]
            """, "transcription.segment");
        var match = Transcription.Compare("  Technical\n speech. ", "technical speech.");
        Check(!match.RequiresReview && match.IntendedNormalized == "TECHNICAL SPEECH."
            && match.ObservedNormalized == "TECHNICAL SPEECH.", "only explicit case/whitespace normalization");
        var discrepancy = Transcription.Compare("Intended script.", "Observed speech.");
        Check(discrepancy.RequiresReview && discrepancy.IntendedNormalized == "INTENDED SCRIPT."
            && discrepancy.ObservedNormalized == "OBSERVED SPEECH.", "discrepancy retains both texts");
        Check(Transcription.Compare("speech", "speech.").RequiresReview, "punctuation differences require review");
        Check(Transcription.Compare("", "speech").RequiresReview, "unknown script is not inferred from speech");
        return count;
    }

    public static async Task<int> PreflightControls(TranscriberConfig config, string scratchRoot, CancellationToken ct)
    {
        Directory.CreateDirectory(scratchRoot);
        var count = 0;
        async Task Reject(TranscriberConfig changed, string code)
        {
            var output = Path.Combine(scratchRoot, "rejected-" + Guid.NewGuid().ToString("N"));
            try
            {
                await Transcription.Learn(Path.Combine(scratchRoot, "never-read.wav"), "", "", output, changed, ct);
                throw new Exception("Expected " + code);
            }
            catch (VideoException ex)
            {
                if (ex.Code != code || Directory.Exists(output))
                    throw new Exception($"Expected preflight {code} without output; got {ex.Code}");
                count++;
            }
        }
        await Reject(config with { ModelSha256 = new string('0', 64) }, "transcription.model-profile");
        await Reject(config with { ModelPath = Path.Combine(scratchRoot, "unapproved.bin") }, "transcription.model-profile");
        await Reject(config with { TimeoutSeconds = 0 }, "transcription.bounds");
        await Reject(config with { MaxOutputBytes = 100 }, "transcription.bounds");
        await Reject(config with { MaxAudioSeconds = 1801 }, "transcription.bounds");
        await Reject(config with { Profile = "unknown" }, "transcription.profile");
        await Reject(config with { Executable = Path.Combine(scratchRoot, "missing.exe") }, "transcription.executable");
        await Reject(config with { ExecutableSha256 = new string('0', 64) }, "transcription.executable");
        await Reject(config with { Dependencies = [] }, "transcription.artifacts");
        await Reject(config with { Dependencies = [.. config.Dependencies, config.Dependencies[0]] }, "transcription.artifacts");
        await Reject(config with {
            Dependencies = config.Dependencies.Select((x, i) => i == 0 ? x with { Sha256 = new string('0', 64) } : x).ToArray()
        }, "transcription.artifacts");
        await Reject(config with { FfmpegSha256 = new string('0', 64) }, "transcription.ffmpeg");
        return count;
    }

    public static async Task<TranscriptLearning[]> RealTtsAndImportedWav(TranscriberConfig config,
        KokoroConfig kokoro, string scratchRoot, CancellationToken ct)
    {
        Directory.CreateDirectory(scratchRoot);
        const string script = "The API returns JSON. Run dotnet build, then inspect the UTF eight transcript. "
            + "Keep the original WAV and compare the observed speech with the intended script.";
        const string description = "A terminal shows a successful build beside a transcript comparison.";
        var take = await new KokoroBackend(kokoro).Synthesize(script, Path.Combine(scratchRoot, "technical-tts"), ct);
        var technical = await Transcription.Learn(take.WavPath, script, description,
            Path.Combine(scratchRoot, "technical-stt"), config, ct);
        AssertEvidence(technical, take.WavPath, script, description);

        var importRoot = Path.Combine(scratchRoot, "synthetic-import-project");
        Directory.CreateDirectory(importRoot);
        // This imported WAV is deliberately synthetic, not a claim of human-recorded speech.
        var imported = NarrationImport.Import(importRoot, take.WavPath, "synthetic-import",
            "Deliberately mismatched intended script.", [], out var asset);
        var importedPath = Files.Under(importRoot, asset.Path);
        var importedReport = await Transcription.Learn(importedPath, imported.DisplayText,
            "Synthetic imported technical narration fixture; no human speaker.",
            Path.Combine(scratchRoot, "imported-stt"), config, ct);
        AssertEvidence(importedReport, importedPath, imported.DisplayText,
            "Synthetic imported technical narration fixture; no human speaker.");
        if (!importedReport.Discrepancy.RequiresReview || Files.Hash(take.WavPath) != take.WavSha256
            || importedReport.MasterSha256 != technical.MasterSha256)
            throw new Exception("Imported mismatch/master-preservation evidence failed.");
        Json.Write(Path.Combine(scratchRoot, "integration-evidence.json"), new
        {
            fixture = "Local Kokoro technical TTS plus explicitly synthetic imported WAV; not human-recorded.",
            technical,
            imported = importedReport,
            noWordAlignmentClaimed = true,
            originalTtsMasterPreserved = Files.Hash(take.WavPath) == take.WavSha256
        });
        return [technical, importedReport];
    }

    private static void AssertEvidence(TranscriptLearning report, string master, string script, string description)
    {
        if (report.MasterSha256 != Files.Hash(master) || report.Segments.Length == 0
            || report.Segments.Any(x => x.Evidence != Evidence.AsrSegment || x.EndSample > report.MasterSampleCount)
            || report.DerivativeSampleRate != 16000 || !Files.ValidHash(report.DerivativeSha256)
            || report.ModelSha256 != Transcription.ModelSha256 || report.Provider != Transcription.Provider
            || report.Model != Transcription.Model || report.ModelArtifact != Transcription.ModelArtifact
            || report.IntendedScript != script || report.DescriptiveAccessibilityText != description
            || report.ObservedText != string.Join(' ', report.Segments.Select(x => x.Text))
            || report.Discrepancy != Transcription.Compare(script, report.ObservedText))
            throw new Exception("Actual transcription identity/clock/text evidence mismatch.");
        var roundtrip = JsonSerializer.Deserialize<TranscriptLearning>(Json.Text(report), Json.Options)!;
        if (roundtrip.MasterSha256 != report.MasterSha256 || roundtrip.ObservedText != report.ObservedText)
            throw new Exception("Transcript evidence JSON roundtrip failed.");
    }
}
