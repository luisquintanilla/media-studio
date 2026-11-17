namespace VideoProduction;

public sealed record SpeechAnalysisReceipt(int SchemaVersion, string Status, string MasterSha256, string ScriptSha256,
    string ConfigurationSha256, string? TranscriptAssetId, string? ErrorCode);

public static class SpeechAnalysis
{
    public static void Validate(Project project, Scene scene, string root)
    {
        if (scene.Narration?.SpeechAnalysisAssetId is not { } id) return;
        var asset = project.Assets.SingleOrDefault(a => a.Id == id)
            ?? throw new VideoException("analysis.asset", "Missing hashed non-adopting speech analysis receipt.");
        var receipt = Json.Read<SpeechAnalysisReceipt>(Files.Under(root, asset.Path));
        Guard.Require(receipt.SchemaVersion == 1 && receipt.ScriptSha256 == Files.HashText(scene.Script)
            && receipt.MasterSha256 == project.Assets.Single(a => a.Id == scene.Narration.AssetId).Sha256
            && Files.ValidHash(receipt.ConfigurationSha256) && scene.Narration.ScriptCaptionAssetId is not null,
            "analysis.stale", "Advisory speech analysis no longer binds this script/master.");
        if (receipt.Status == "failed")
        {
            Guard.Require(!string.IsNullOrWhiteSpace(receipt.ErrorCode) && receipt.TranscriptAssetId is null,
                "analysis.receipt", "Failed QA must record its error explicitly, not report observed agreement.");
            return;
        }
        Guard.Require(receipt.Status is "findings" or "observed-agreement" && receipt.ErrorCode is null,
            "analysis.receipt", "Unknown speech QA outcome.");
        var transcriptAsset = project.Assets.SingleOrDefault(a => a.Id == receipt.TranscriptAssetId)
            ?? throw new VideoException("analysis.transcript", "Missing frozen advisory transcript.");
        var report = Json.Read<TranscriptLearning>(Files.Under(root, transcriptAsset.Path));
        TranscriptProduction.ValidateBinding(project, scene, report, root);
        Guard.Require(receipt.Status == (report.Discrepancy.RequiresReview ? "findings" : "observed-agreement"),
            "analysis.receipt", "Speech QA findings disagree with the actual retained comparison.");
    }
}

public sealed partial class Production
{
    public async Task<SpeechAnalysisReceipt> AnalyzeSpeech(string sceneId, TranscriberConfig config, CancellationToken ct)
    {
        var project = Load();
        Validation.Validate(project, Root);
        var scene = project.Scenes.SingleOrDefault(s => s.Id == sceneId)
            ?? throw new VideoException("scene.missing", "Unknown scene for advisory speech analysis.");
        var narration = scene.Narration!;
        Guard.Require(narration.ScriptCaptionAssetId is not null, "analysis.script-mode",
            "Advisory QA applies to authored script-phrase captions; recorded/unknown speech retains strict transcription/adoption.");
        var master = project.Assets.Single(a => a.Id == narration.AssetId);
        var suffix = Guid.NewGuid().ToString("N")[..16];
        var directory = PathFor($"assets/speech-analysis/{suffix}");
        var assets = project.Assets.ToList();
        var transcriptId = "qa-transcript-" + suffix;
        var status = "failed";
        string? errorCode = null;
        try
        {
            var report = await Transcription.Learn(PathFor(master.Path), scene.Script, scene.VisualDescription ?? "",
                directory, config, ct);
            status = report.Discrepancy.RequiresReview ? "findings" : "observed-agreement";
            foreach (var file in Directory.GetFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (name == "transcriber-stderr.txt") continue;
                var id = name == "transcript.json" ? transcriptId : "qa-" + suffix + "-" + (name switch {
                    "stt-16khz.wav" => "audio",
                    "transcriber-profile.json" => "profile",
                    "transcriber-result.json" => "raw",
                    _ => throw new VideoException("analysis.artifact", "Unexpected speech-analysis evidence file.")
                });
                assets.Add(new(id, Path.GetRelativePath(Root, file).Replace('\\', '/'), Files.Hash(file),
                    "Optional local ASR QA: non-adopting observations only, never canonical captions or acoustic proof",
                    "Caller speech rights; Whisper model MIT; external runtime notices apply"));
            }
        }
        catch (VideoException error) { errorCode = error.Code; }
        catch (IOException) { errorCode = "analysis.io"; }
        catch (System.ComponentModel.Win32Exception) { errorCode = "analysis.process"; }
        ct.ThrowIfCancellationRequested();
        if (errorCode is not null) status = "failed";
        Directory.CreateDirectory(directory);
        var receipt = new SpeechAnalysisReceipt(1, status, master.Sha256, Files.HashText(scene.Script),
            Files.Fingerprint(config), status == "failed" ? null : transcriptId, errorCode);
        var path = Path.Combine(directory, "analysis.json");
        Json.Write(path, receipt);
        var receiptId = "qa-analysis-" + suffix;
        assets.Add(new(receiptId, Path.GetRelativePath(Root, path).Replace('\\', '/'), Files.Hash(path),
            "Explicit non-adopting QA outcome; failed/findings/observed-agreement, not acoustic acceptance", "MIT metadata"));
        var updated = project with {
            Assets = assets.ToArray(),
            Scenes = project.Scenes.Select(s => s.Id == sceneId
                ? s with { Narration = narration with { SpeechAnalysisAssetId = receiptId } } : s).ToArray()
        };
        Validation.Validate(updated, Root);
        Json.Write(ProjectFile, updated);
        return receipt;
    }
}
