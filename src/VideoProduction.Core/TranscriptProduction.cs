namespace VideoProduction;

public sealed record TranscriptAdoption(int SchemaVersion, string TranscriptSha256, string MasterSha256,
    string DiscrepancyDecision, string? CaptionPolicyException, string Note);

public static class TranscriptProduction
{
    public static void ValidateBinding(Project project, Scene scene, TranscriptLearning report, string root)
    {
        var narration = scene.Narration!;
        Guard.Require(report.SchemaVersion == 1
            && report.ModelSha256 == Transcription.ModelSha256 && report.Provider == Transcription.Provider
            && report.Model == Transcription.Model && report.ModelArtifact == Transcription.ModelArtifact
            && report.MasterSha256 == project.Assets.Single(a => a.Id == narration.AssetId).Sha256
            && report.MasterSampleCount == narration.SampleCount && report.MasterSampleRate == narration.SampleRate
            && report.IntendedScript == scene.Script && report.DescriptiveAccessibilityText == (scene.VisualDescription ?? "")
            && report.Discrepancy == Transcription.Compare(report.IntendedScript, report.ObservedText),
            "transcription.stale", "Narration/script/description/provider changed since STT; transcribe the selected master again.");
        Guard.Require(project.Assets.Any(a => a.Sha256 == report.DerivativeSha256)
            && project.Assets.Any(a => a.Sha256 == report.RawResultSha256)
            && report.ObservedText == string.Join(' ', report.Segments.Select(s => s.Text)),
            "transcription.evidence", "Missing derivative/raw STT evidence or inconsistent observed text.");
        var derivative = project.Assets.First(a => a.Sha256 == report.DerivativeSha256);
        var raw = project.Assets.First(a => a.Sha256 == report.RawResultSha256);
        var clock = Wav.Inspect(Files.Under(root, derivative.Path));
        Guard.Require(clock.SampleRate == report.DerivativeSampleRate && clock.Samples == report.DerivativeSampleCount
            && Transcription.ParseResult(File.ReadAllText(Files.Under(root, raw.Path)), report.Derivative, clock,
                new WavInfo(narration.SampleRate, 1, narration.SampleCount)).SequenceEqual(report.Segments),
            "transcription.evidence", "Retained raw provider JSON/derivative clock does not match reported segment evidence.");
        Guard.Require(report.Profile is null && report.ProfileEvidenceSha256 is null
            || report.Profile == Transcription.DigitalSilenceProfile && report.ProfileEvidenceSha256 is { } profileHash
                && project.Assets.Any(a => a.Sha256 == profileHash),
            "transcription.profile", "Missing or unsupported frozen transcription-profile evidence.");
        if (report.ProfileEvidenceSha256 is { } evidenceHash)
        {
            var evidence = project.Assets.First(a => a.Sha256 == evidenceHash);
            Transcription.ValidateDigitalSilence(Files.Under(root, evidence.Path), Files.Under(root, derivative.Path),
                derivative.Sha256, clock, report.Segments);
        }
    }

    public static void ValidateAdoption(Project project, Scene scene, TranscriptLearning report, string root, string reportHash)
    {
        var narration = scene.Narration!;
        var asset = project.Assets.SingleOrDefault(a => a.Id == narration.TranscriptAdoptionAssetId)
            ?? throw new VideoException("transcription.adoption", "Missing explicit STT caption adoption decision.");
        var decision = Json.Read<TranscriptAdoption>(Files.Under(root, asset.Path));
        Guard.Require(decision.SchemaVersion == 1 && decision.TranscriptSha256 == reportHash
            && decision.MasterSha256 == report.MasterSha256
            && (!report.Discrepancy.RequiresReview || !string.IsNullOrWhiteSpace(decision.DiscrepancyDecision))
            && narration.Cues.SequenceEqual(report.Segments.Select(s =>
                new Cue(s.StartSample, s.EndSample, CaptionText(s.Text), Evidence.AsrSegment, decision.CaptionPolicyException))),
            "transcription.adoption", "Adopted captions/decision do not match the hashed observed STT segments.");
    }

    public static string CaptionText(string text)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > 42) { lines.Add(current); current = ""; }
            current += (current.Length == 0 ? "" : " ") + word;
        }
        if (current.Length > 0) lines.Add(current);
        return string.Join('\n', lines);
    }
}

public sealed partial class Production
{
    public async Task<object> Transcribe(string sceneId, TranscriberConfig config, CancellationToken ct)
    {
        var project = Load();
        Validation.Validate(project, Root, allowPendingTranscripts: true);
        var scene = project.Scenes.SingleOrDefault(s => s.Id == sceneId)
            ?? throw new VideoException("scene.missing", "Unknown scene for transcription.");
        var narration = scene.Narration!;
        var master = project.Assets.Single(a => a.Id == narration.AssetId);
        Guard.Require(narration.ScriptCaptionAssetId is null, "transcription.script-mode",
            "Script-phrase captions use canonical measured takes. Use analyze-speech for non-adopting QA; import recorded/unknown narration explicitly before strict transcript adoption.");
        narration = narration with { TranscriptAssetId = null, TranscriptPending = true, TranscriptAdoptionAssetId = null };
        project = project with { Scenes = project.Scenes.Select(s => s.Id == sceneId ? s with { Narration = narration } : s).ToArray() };
        Json.Write(ProjectFile, project);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var directory = PathFor($"assets/transcripts/{sceneId[..Math.Min(sceneId.Length, 32)]}-{suffix}");
        var report = await Transcription.Learn(PathFor(master.Path), scene.Script, scene.VisualDescription ?? "",
            directory, config, ct);
        var id = $"stt-{sceneId[..Math.Min(sceneId.Length, 32)]}-{suffix}";
        var assets = project.Assets.ToList();
        foreach (var file in Directory.GetFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (name == "transcriber-stderr.txt") continue; // Private diagnostics are intentionally not project assets.
            var assetId = name == "transcript.json" ? id : id + "-" + (name switch {
                "stt-16khz.wav" => "audio",
                "transcriber-profile.json" => "profile",
                "transcriber-result.json" => "raw",
                _ => throw new VideoException("transcription.artifact", "Unexpected transcription evidence file.")
            });
            assets.Add(new Asset(assetId, Path.GetRelativePath(Root, file).Replace('\\', '/'), Files.Hash(file),
                "Actual local fixed-English Whisper Base recognition; observed speech/segment evidence, not word alignment",
                "Caller speech rights; Whisper model MIT; external runtime notices apply"));
        }
        Json.Write(ProjectFile, project with {
            Assets = assets.ToArray(),
            Scenes = project.Scenes.Select(s => s.Id == sceneId
                ? s with { Narration = narration with { TranscriptAssetId = id } } : s).ToArray()
        });
        return new
        {
            transcriptAssetId = id, report = Path.GetRelativePath(Root, Path.Combine(directory, "transcript.json")).Replace('\\', '/'),
            observedText = report.ObservedText, report.Discrepancy, adoptedAsCaptions = false,
            instruction = "Read observed segments/discrepancy, then explicitly adopt-transcript; intended script and descriptive text were not changed."
        };
    }

    public object AdoptTranscript(string sceneId, string discrepancyDecision, string? policyException)
    {
        var project = Load();
        Validation.Validate(project, Root, allowPendingTranscripts: true);
        var scene = project.Scenes.SingleOrDefault(s => s.Id == sceneId)
            ?? throw new VideoException("scene.missing", "Unknown scene.");
        var narration = scene.Narration!;
        var asset = project.Assets.SingleOrDefault(a => a.Id == narration.TranscriptAssetId)
            ?? throw new VideoException("transcription.missing", "First transcribe this scene with explicit pinned config.");
        var report = Json.Read<TranscriptLearning>(PathFor(asset.Path));
        TranscriptProduction.ValidateBinding(project, scene, report, Root);
        Guard.Require(!report.Discrepancy.RequiresReview || !string.IsNullOrWhiteSpace(discrepancyDecision),
            "transcription.discrepancy", "Observed STT differs from intended script; supply explicit --discrepancy-decision after inspecting both, or correct the take and transcribe again.");
        var cues = report.Segments.Select(s => new Cue(s.StartSample, s.EndSample, TranscriptProduction.CaptionText(s.Text),
            Evidence.AsrSegment, policyException)).ToArray();
        foreach (var cue in cues) Captions.ValidateCue(cue, narration.SampleRate);
        var decisionPath = PathFor($"assets/transcripts/adoption-{Guid.NewGuid():N}.json");
        Json.Write(decisionPath, new TranscriptAdoption(1, asset.Sha256, report.MasterSha256,
            discrepancyDecision, policyException,
            "Explicit editorial adoption of observed segment speech. NOT human release or word-boundary approval."));
        var decision = new Asset("stt-adoption-" + Guid.NewGuid().ToString("N")[..16],
            Path.GetRelativePath(Root, decisionPath).Replace('\\', '/'), Files.Hash(decisionPath),
            "Explicit STT caption adoption decision; human release remains separate", "MIT metadata");
        var updated = project with {
            Assets = project.Assets.Append(decision).ToArray(),
            Scenes = project.Scenes.Select(s => s.Id == sceneId ? s with {
                Narration = narration with {
                    Cues = cues,
                    TranscriptPending = false,
                    TranscriptAdoptionAssetId = decision.Id
                }
            } : s).ToArray()
        };
        Validation.Validate(updated, Root);
        Json.Write(ProjectFile, updated);
        return new { adopted = true, evidence = "asrSegment", intendedScriptUnchanged = true, humanReviewRequired = true };
    }
}
