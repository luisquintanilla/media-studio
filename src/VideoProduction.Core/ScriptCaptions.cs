using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VideoProduction;

public sealed record AuthoredNarrationPolicy(int SchemaVersion, string Mode, bool AnalyzeSpeech = false)
{
    public const string ScriptPhrases = "script-phrases-v1";
    public void Validate() => Guard.Require(SchemaVersion == 1 && Mode == ScriptPhrases,
        "narration.policy", "Use the explicit schemaVersion1 script-phrases-v1 policy; omission retains legacy narration/ASR behavior.");
}

public sealed record ScriptPhrase(int SourceStart, int SourceLength, string SynthesisInput);
public sealed record MeasuredPhrase(ScriptPhrase Source, long StartSample, long SampleCount,
    ArtifactHash Wav, ArtifactHash ProviderReceipt, string ProviderIdentitySha256);
public sealed record ScriptCaptionReceipt(int SchemaVersion, string PlanningPolicy, string CanonicalScript,
    string ScriptSha256, int SampleRate, long ContentSamples, long EndGuardSamples,
    ArtifactHash Composition, ArtifactHash Master, MeasuredPhrase[] Phrases, Cue[] Cues);
public sealed record ScriptCompositionReceipt(int SchemaVersion, string Mode, int SampleRate, long SampleCount,
    string CanonicalScript, string ScriptSha256, ArtifactHash Master, MeasuredPhrase[] Phrases);
public sealed record PhraseNarrationResult(NarrationTake Take, string ReceiptPath, ScriptCaptionReceipt Receipt);

public static class ScriptCaptions
{
    public const string PlanningPolicy = "natural-script-spans-v1";
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
        { "mr.", "mrs.", "ms.", "dr.", "prof.", "sr.", "jr.", "e.g.", "i.e.", "vs.", "etc.", "fig.", "no." };

    public static ScriptPhrase[] Plan(string script)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(script) && script.Length <= 10000
            && !script.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c)) && !script.Contains("-->", StringComparison.Ordinal),
            "caption.script", "Expected a nonblank canonical script of at most10000 characters without control data.");
        var phrases = new List<ScriptPhrase>();
        var start = 0;
        while (start < script.Length)
        {
            var lastWord = -1;
            var lastClause = -1;
            var end = script.Length;
            for (var i = start; i < script.Length; i++)
            {
                if (!char.IsWhiteSpace(script[i]) || i == start || char.IsWhiteSpace(script[i - 1])) continue;
                var boundary = i;
                while (boundary < script.Length && char.IsWhiteSpace(script[boundary])) boundary++;
                var content = script[start..i];
                if (!Fits(content))
                {
                    end = lastClause > start ? lastClause : lastWord;
                    Guard.Require(end > start, "caption.phrase", "An indivisible phrase/word exceeds the caption width; revise the script explicitly.");
                    break;
                }
                lastWord = boundary;
                var punctuation = i - 1;
                while (punctuation > start && "\"'\u201d\u2019)]}".Contains(script[punctuation])) punctuation--;
                if (script[punctuation] is ',' or ';' or ':' or '\u2014') lastClause = boundary;
                if (script[punctuation] is '!' or '?' || script[punctuation] == '.' && SentenceEnd(script, start, punctuation))
                {
                    end = boundary;
                    break;
                }
            }
            if (end == script.Length && !Fits(script[start..end]))
            {
                end = lastClause > start ? lastClause : lastWord;
                Guard.Require(end > start, "caption.phrase", "An indivisible phrase/word exceeds the caption width; revise the script explicitly.");
            }
            var text = script[start..end];
            var wrapped = CaptionText(text);
            Guard.Require(wrapped.Split('\n').Length <= 2 && wrapped.Split('\n').All(l => l.Length <= 42),
                "caption.phrase", "A planned phrase cannot meet the two-line42-column policy; revise the script explicitly.");
            phrases.Add(new(start, end - start, text));
            Guard.Require(phrases.Count <= 128, "caption.phrase-count", "A scene supports at most128 measured phrase takes.");
            start = end;
        }
        return phrases.ToArray();
    }

    private static bool SentenceEnd(string script, int start, int punctuation)
    {
        var begin = punctuation;
        while (begin > start && !char.IsWhiteSpace(script[begin - 1])) begin--;
        var token = script[begin..(punctuation + 1)].TrimStart('(', '[', '"', '\'');
        return !Abbreviations.Contains(token) && !Regex.IsMatch(token, @"^(?:[A-Za-z]\.)+$", RegexOptions.CultureInvariant);
    }

    public static string CaptionText(string text) => TranscriptProduction.CaptionText(text);
    private static bool Fits(string text)
    {
        var lines = CaptionText(text).Split('\n');
        return lines.Length <= 2 && lines.All(l => l.Length <= 42);
    }

    public static Cue[] BuildCues(MeasuredPhrase[] phrases, int sampleRate)
    {
        Guard.Require(phrases.Length > 0 && sampleRate is >= 8000 and <= 192000, "caption.phrases", "Measured nonempty phrase clocks are required.");
        long cursor = 0;
        foreach (var phrase in phrases)
        {
            Guard.Require(phrase.StartSample == cursor && phrase.SampleCount > 0, "caption.phrases", "Phrase windows must be positive and contiguous.");
            cursor = checked(cursor + phrase.SampleCount);
        }
        var choices = new Cue?[phrases.Length];
        var next = new int[phrases.Length];
        for (var first = phrases.Length - 1; first >= 0; first--)
        {
            var text = "";
            for (var last = first; last < phrases.Length; last++)
            {
                var phrase = phrases[last];
                text += phrase.Source.SynthesisInput;
                var cue = new Cue(phrases[first].StartSample, checked(phrase.StartSample + phrase.SampleCount),
                    CaptionText(text), Evidence.TakeWindow);
                var lines = cue.Text.Split('\n');
                var seconds = (double)(cue.EndSample - cue.StartSample) / sampleRate;
                if (seconds > 6 || lines.Length > 2 || lines.Any(l => l.Length > 42)) break;
                if (seconds >= 1 && cue.Text.Replace("\n", "", StringComparison.Ordinal).Length / seconds <= 20
                    && (last + 1 == phrases.Length || choices[last + 1] is not null))
                {
                    Captions.ValidateCue(cue, sampleRate);
                    choices[first] = cue;
                    next[first] = last + 1;
                    break;
                }
            }
        }
        Guard.Require(choices[0] is not null, "caption.measured-policy",
            "Actual whole-phrase windows cannot satisfy1..6seconds/two lines42columns/20characters-per-second. Retained PCM was not subdivided, redistributed or synthesized again.");
        var result = new List<Cue>();
        for (var index = 0; index < phrases.Length; index = next[index]) result.Add(choices[index]!);
        return result.ToArray();
    }

    public static async Task<PhraseNarrationResult> Synthesize(string root, string directory, string script,
        INarrationBackend backend, int endGuardMilliseconds, CancellationToken ct)
    {
        var spans = Plan(script);
        Guard.Require(endGuardMilliseconds is >= 0 and <= 1000, "narration.end-guard", "Scene end guard must be0..1000milliseconds.");
        Guard.Require(!Directory.Exists(directory) && !File.Exists(directory), "narration.immutable", "Choose a new phrase scene directory.");
        Directory.CreateDirectory(directory);
        var phrases = new List<MeasuredPhrase>();
        long samples = 0;
        var sampleRate = 0;
        string? identity = null;
        foreach (var span in spans)
        {
            ct.ThrowIfCancellationRequested();
            var take = await backend.Synthesize(span.SynthesisInput, Path.Combine(directory, "phrases", phrases.Count.ToString("D3")), ct);
            ct.ThrowIfCancellationRequested();
            var clock = Wav.Inspect(take.WavPath);
            Guard.Require(take.DisplayText == span.SynthesisInput && take.WavSha256 == Files.Hash(take.WavPath)
                && take.SampleRate == clock.SampleRate && take.SampleCount == clock.Samples,
                "caption.take-binding", "Phrase text, actual PCM and provider take clock must agree.");
            sampleRate = sampleRate == 0 ? clock.SampleRate : sampleRate;
            Guard.Require(clock.SampleRate == sampleRate, "caption.format", "Every phrase must use the same actual mono PCM16 sample rate.");
            var providerIdentity = ProviderIdentity(take.MetadataPath, span.SynthesisInput, clock, take.WavSha256);
            identity ??= providerIdentity;
            Guard.Require(identity == providerIdentity, "caption.provider", "Phrase provider/model/voice/language/speed identity changed within the scene.");
            phrases.Add(new(span, samples, clock.Samples, Relative(root, take.WavPath), Relative(root, take.MetadataPath), providerIdentity));
            samples = checked(samples + clock.Samples);
            Guard.Require(samples <= 600L * sampleRate, "caption.duration", "Measured scene composition exceeds ten minutes.");
        }
        var master = Path.Combine(directory, "narration.wav");
        Compose(root, master, sampleRate, samples, phrases, ct);
        var compositionPath = Path.Combine(directory, "composition.json");
        Json.Write(compositionPath, new ScriptCompositionReceipt(1, "unchanged-phrase-pcm-concatenation",
            sampleRate, samples, script, Files.HashText(script), Relative(root, master), phrases.ToArray()));
        var original = new NarrationTake(master, Files.Hash(master), sampleRate, samples, script, compositionPath);
        var final = endGuardMilliseconds == 0 ? original : NarrationEndGuard.Apply(original, endGuardMilliseconds, ct);
        var receiptPath = Path.Combine(directory, "script-captions.json");
        Cue[] cues;
        try { cues = BuildCues(phrases.ToArray(), sampleRate); }
        catch (VideoException error) when (error.Code == "caption.measured-policy")
        {
            Json.Write(Path.Combine(directory, "caption-policy-failure.json"), new
            {
                schemaVersion = 1, error.Code, error.Message, canonicalScript = script,
                sampleRate, measuredPhrases = phrases, composedMaster = Relative(root, final.WavPath),
                noTimingRedistribution = true, noRetry = true
            });
            throw;
        }
        ct.ThrowIfCancellationRequested();
        var receipt = new ScriptCaptionReceipt(1, PlanningPolicy, script, Files.HashText(script), sampleRate,
            samples, final.SampleCount - samples, Relative(root, compositionPath), Relative(root, final.WavPath), phrases.ToArray(), cues);
        Json.Write(receiptPath, receipt);
        return new(final, receiptPath, receipt);
    }

    private static ArtifactHash Relative(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        Guard.Require(Files.Under(root, relative) == Path.GetFullPath(file), "caption.path", "Phrase evidence must stay inside its project.");
        return new(relative, Files.Hash(file));
    }

    private static void Compose(string root, string target, int sampleRate, long samples,
        IEnumerable<MeasuredPhrase> phrases, CancellationToken ct)
    {
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        Wav.WriteHeader(writer, sampleRate, samples);
        var buffer = new byte[65536];
        foreach (var phrase in phrases)
        {
            var path = Files.Under(root, phrase.Wav.Path);
            var pcm = Wav.PcmRange(path);
            using var input = File.OpenRead(path);
            input.Position = pcm.Offset;
            for (long copied = 0; copied < pcm.Bytes;)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, pcm.Bytes - copied);
                input.ReadExactly(buffer.AsSpan(0, count));
                writer.Write(buffer, 0, count);
                copied += count;
            }
            Guard.Require(Files.Hash(path) == phrase.Wav.Sha256, "caption.take-binding", "Phrase PCM changed during composition.");
        }
    }

    private static string ProviderIdentity(string path, string input, WavInfo clock, string wavHash)
    {
        var metadata = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Guard.Require(metadata["displayText"]?.GetValue<string>() == input
            && metadata["wavSha256"]?.GetValue<string>() == wavHash
            && metadata["sampleRate"]?.GetValue<int>() == clock.SampleRate
            && metadata["sampleCount"]?.GetValue<long>() == clock.Samples,
            "caption.provider-evidence", "Provider metadata does not bind this exact phrase text/audio/sample clock.");
        var execution = metadata["execution"];
        var provenance = metadata["providerEvidence"]?["provenance"] ?? metadata["provenance"];
        Guard.Require(execution?["client"] is not null || provenance is not null,
            "caption.provider-evidence", "Phrase evidence must identify the actual speech provider; no synthetic provider identity is inferred.");
        return Files.Fingerprint(new
        {
            modelId = metadata["modelId"] ?? provenance?["modelId"],
            client = execution?["client"], request = execution?["request"],
            abstractionAssembly = execution?["abstractionAssembly"], provenance
        });
    }

    public static void Validate(Project project, Scene scene, string root)
    {
        if (scene.Narration?.ScriptCaptionAssetId is not { } id) return;
        var narration = scene.Narration;
        var receiptAsset = project.Assets.SingleOrDefault(a => a.Id == id)
            ?? throw new VideoException("caption.receipt", "Missing hashed script-caption receipt.");
        var receipt = Json.Read<ScriptCaptionReceipt>(Files.Under(root, receiptAsset.Path));
        var master = project.Assets.Single(a => a.Id == narration.AssetId);
        Guard.Require(receipt.SchemaVersion == 1 && receipt.PlanningPolicy == PlanningPolicy
            && receipt.CanonicalScript == scene.Script && receipt.ScriptSha256 == Files.HashText(scene.Script)
            && narration.DisplayText == scene.Script && receipt.SampleRate == narration.SampleRate
            && receipt.ContentSamples > 0 && receipt.EndGuardSamples >= 0 && receipt.EndGuardSamples <= receipt.SampleRate
            && receipt.ContentSamples + receipt.EndGuardSamples == narration.SampleCount
            && receipt.Master == new ArtifactHash(master.Path, master.Sha256)
            && !narration.TranscriptPending && narration.TranscriptAssetId is null && narration.TranscriptAdoptionAssetId is null
            && receipt.Cues.SequenceEqual(narration.Cues) && receipt.Cues.All(c => c.Evidence == Evidence.TakeWindow && c.PolicyException is null)
            && Plan(scene.Script).SequenceEqual(receipt.Phrases.Select(p => p.Source)),
            "caption.stale", "Script caption policy, spans, text, clock, cues or caption source changed since measured synthesis.");
        void Asset(ArtifactHash file) => Guard.Require(project.Assets.Any(a => a.Path == file.Path && a.Sha256 == file.Sha256),
            "caption.evidence", "A bound phrase/master/composition file is absent from the frozen asset manifest.");
        Asset(receipt.Composition);
        var composition = Json.Read<ScriptCompositionReceipt>(Files.Under(root, receipt.Composition.Path));
        Asset(composition.Master);
        var originalClock = Wav.Inspect(Files.Under(root, composition.Master.Path));
        Guard.Require(composition.SchemaVersion == 1 && composition.Mode == "unchanged-phrase-pcm-concatenation"
            && composition.CanonicalScript == receipt.CanonicalScript && composition.ScriptSha256 == receipt.ScriptSha256
            && composition.SampleRate == receipt.SampleRate && composition.SampleCount == receipt.ContentSamples
            && originalClock.SampleRate == receipt.SampleRate && originalClock.Samples == receipt.ContentSamples
            && composition.Phrases.SequenceEqual(receipt.Phrases),
            "caption.composition", "Original scene composition evidence changed.");
        long cursor = 0;
        string? identity = null;
        using var composed = File.OpenRead(Files.Under(root, receipt.Master.Path));
        using var originalMaster = File.OpenRead(Files.Under(root, composition.Master.Path));
        var masterClock = Wav.Inspect(composed.Name);
        Guard.Require(masterClock.SampleRate == receipt.SampleRate && masterClock.Samples == narration.SampleCount,
            "caption.clock", "Actual scene master differs from the script-caption clock.");
        composed.Position = Wav.PcmRange(composed.Name).Offset;
        originalMaster.Position = Wav.PcmRange(originalMaster.Name).Offset;
        var expected = new byte[65536];
        var actual = new byte[65536];
        var original = new byte[65536];
        foreach (var phrase in receipt.Phrases)
        {
            Asset(phrase.Wav); Asset(phrase.ProviderReceipt);
            var file = Files.Under(root, phrase.Wav.Path);
            var clock = Wav.Inspect(file);
            var provider = ProviderIdentity(Files.Under(root, phrase.ProviderReceipt.Path),
                phrase.Source.SynthesisInput, clock, phrase.Wav.Sha256);
            identity ??= provider;
            Guard.Require(provider == identity && provider == phrase.ProviderIdentitySha256
                && phrase.StartSample == cursor && phrase.SampleCount == clock.Samples && clock.SampleRate == receipt.SampleRate,
                "caption.phrase-binding", "Measured phrase range, provider identity or actual clock changed.");
            var pcm = Wav.PcmRange(file);
            using var input = File.OpenRead(file);
            input.Position = pcm.Offset;
            for (long read = 0; read < pcm.Bytes;)
            {
                var count = (int)Math.Min(expected.Length, pcm.Bytes - read);
                input.ReadExactly(expected.AsSpan(0, count));
                composed.ReadExactly(actual.AsSpan(0, count));
                originalMaster.ReadExactly(original.AsSpan(0, count));
                Guard.Require(expected.AsSpan(0, count).SequenceEqual(actual.AsSpan(0, count))
                    && expected.AsSpan(0, count).SequenceEqual(original.AsSpan(0, count)),
                    "caption.composition", "The scene master does not retain each phrase's original PCM byte-for-byte.");
                read += count;
            }
            cursor = checked(cursor + phrase.SampleCount);
        }
        Guard.Require(cursor == receipt.ContentSamples && BuildCues(receipt.Phrases, receipt.SampleRate).SequenceEqual(receipt.Cues),
            "caption.composition", "Phrase coverage or measured cue grouping changed.");
        for (long remaining = checked(receipt.EndGuardSamples * 2); remaining > 0;)
        {
            var count = (int)Math.Min(actual.Length, remaining);
            composed.ReadExactly(actual.AsSpan(0, count));
            Guard.Require(actual.AsSpan(0, count).IndexOfAnyExcept((byte)0) < 0,
                "caption.end-guard", "The explicit scene end guard must contain only real digital silence.");
            remaining -= count;
        }
    }

    public static object Inspect(Project project, string root) => project.Scenes.Select(scene =>
    {
        var narration = scene.Narration!;
        T Read<T>(string id)
        {
            var asset = project.Assets.Single(a => a.Id == id);
            Files.VerifyAsset(root, asset);
            return Json.Read<T>(Files.Under(root, asset.Path));
        }
        var receipt = narration.ScriptCaptionAssetId is { } captionId ? Read<ScriptCaptionReceipt>(captionId) : null;
        var analysis = narration.SpeechAnalysisAssetId is { } analysisId ? Read<SpeechAnalysisReceipt>(analysisId) : null;
        JsonNode? provider = null;
        if (receipt?.Phrases.FirstOrDefault() is { } phrase)
        {
            var evidence = project.Assets.Single(a => a.Path == phrase.ProviderReceipt.Path && a.Sha256 == phrase.ProviderReceipt.Sha256);
            Files.VerifyAsset(root, evidence);
            provider = JsonNode.Parse(File.ReadAllText(Files.Under(root, evidence.Path)));
        }
        return new
        {
            scene.Id,
            captionSource = receipt is null ? "legacy-or-explicit-import" : AuthoredNarrationPolicy.ScriptPhrases,
            timingEvidence = narration.Cues.Select(c => c.Evidence).Distinct().ToArray(),
            provider = provider?["execution"]?["client"]?["provider"],
            clientType = provider?["execution"]?["client"]?["clientType"],
            modelId = provider?["modelId"] ?? provider?["provenance"]?["modelId"],
            request = provider?["execution"]?["request"],
            captionReceipt = receipt,
            qaStatus = analysis?.Status ?? "not-run",
            qaReceipt = analysis,
            qualification = "Measured TakeWindow or explicitly imported evidence, not word alignment, pronunciation/prosody verification or human acceptance. ASR observations never replace script-phrase captions."
        };
    }).ToArray();
}
