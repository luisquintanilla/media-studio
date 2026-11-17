namespace VideoProduction;

public static class Validation
{
    public static void Validate(Project project, string root, bool allowPendingTranscripts = false)
    {
        project.NarrationPolicy?.Validate();
        Guard.Require(project.SchemaVersion == 1 && Files.ValidId(project.Id), "project.version", "Expected schemaVersion 1 and stable lowercase project ID.");
        Guard.Require(project.Width == 1920 && project.Height == 1080 && project.Fps == new Rate(30, 1),
            "project.profile", "MVP delivery profile is 1920x1080 at 30/1 fps.");
        Guard.Require(project.Scenes.Length is > 0 and <= 100, "project.scenes", "Expected 1..100 scenes.");
        Guard.Require(project.Assets.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == project.Assets.Length,
            "asset.duplicate", "Asset IDs must be unique ignoring case.");
        Guard.Require(project.Assets.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == project.Assets.Length,
            "asset.path-collision", "Asset paths must be unique ignoring case.");
        foreach (var asset in project.Assets) Files.VerifyAsset(root, asset);
        Guard.Require(project.Scenes.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == project.Scenes.Length,
            "scene.duplicate", "Scene IDs must be unique ignoring case.");
        foreach (var scene in project.Scenes)
        {
            Visuals.Validate(scene.Visual, scene.Kind, scene.Text);
            MotionVisuals.Validate(scene.Motion, scene.Kind,
                scene.Visual is not null || scene.VisualAssetId is not null || scene.Effects.Length > 0);
            if (scene.TransitionIn is { } transition)
            {
                Guard.Require(scene != project.Scenes[0], "transition.fields", "First scene cannot have an incoming bridge.");
                Transitions.Validate(transition);
            }
            Guard.Require(Files.ValidId(scene.Id) && scene.Title.Length is > 0 and <= 100 && scene.Text.Length <= 10000
                && !string.IsNullOrWhiteSpace(scene.Script) && scene.Script.Length <= 10000,
                "scene.fields", $"Invalid fields: {scene.Id}");
            Guard.Require(scene.FixedFrames >= 0 && scene.FixedFrames <= 18_000, "scene.duration", "Scene duration exceeds ten-minute bound.");
            Guard.Require(scene.Narration is not null, "narration.required", $"Narration required: {scene.Id}");
            var narration = scene.Narration!;
            Guard.Require(project.Assets.Any(a => a.Id == narration.AssetId) && narration.SampleRate > 0 && narration.SampleCount > 0,
                "narration.asset", $"Invalid narration reference: {scene.Id}");
            Guard.Require(narration.Cues.Length > 0, "caption.missing", $"Explicit transcript evidence required: {scene.Id}");
            Guard.Require(allowPendingTranscripts || project.DeliveryCaptions is not null || !narration.TranscriptPending, "transcription.pending",
                $"STT was requested but has not been successfully adopted: {scene.Id}. Inspect the failed/pending evidence; transcribe again or explicitly import replacement narration.");
            if (narration.TranscriptAssetId is { } transcript)
            {
                var transcriptAsset = project.Assets.SingleOrDefault(a => a.Id == transcript)
                    ?? throw new VideoException("transcription.asset", "Missing hashed transcript evidence.");
                var report = Json.Read<TranscriptLearning>(Files.Under(root, transcriptAsset.Path));
                if (!allowPendingTranscripts && project.DeliveryCaptions is null) TranscriptProduction.ValidateBinding(project, scene, report, root);
                if (!narration.TranscriptPending && project.DeliveryCaptions is null)
                    TranscriptProduction.ValidateAdoption(project, scene, report, root, transcriptAsset.Sha256);
            }
            DeliveryCaptions.Validate(project, root);
            long end = 0;
            foreach (var cue in narration.Cues)
            {
                Guard.Require(cue.StartSample >= end && cue.EndSample > cue.StartSample && cue.EndSample <= narration.SampleCount,
                    "caption.range", $"Overlapping/out-of-range cue: {scene.Id}");
                Captions.ValidateCue(cue, narration.SampleRate);
                end = cue.EndSample;
            }
            ScriptCaptions.Validate(project, scene, root);
            SpeechAnalysis.Validate(project, scene, root);
            if (scene.VisualAssetId is not null)
                Guard.Require(project.Assets.Any(a => a.Id == scene.VisualAssetId), "visual.asset", $"Missing visual reference: {scene.Id}");
            Guard.Require(scene.Kind != SceneKind.Media || scene.VisualAssetId is not null,
                "visual.required", $"Media scene needs a visual asset: {scene.Id}");
            if (scene.Kind == SceneKind.Media)
                Guard.Require(new[] { ".png", ".jpg", ".jpeg", ".mp4", ".webm", ".mkv" }
                    .Contains(Path.GetExtension(project.Assets.Single(a => a.Id == scene.VisualAssetId).Path), StringComparer.OrdinalIgnoreCase),
                    "visual.format", "Imported visuals support raster PNG/JPEG and MP4/WebM/Matroska only, not SVG/HTML/playlists.");
            foreach (var effect in scene.Effects)
                Guard.Require(effect.StartFrame >= 0 && effect.EndFrame > effect.StartFrame && effect.Text.Length <= 100,
                    "effect.range", $"Invalid typed effect: {scene.Id}");
            Guard.Require(scene.Kind is not (SceneKind.Browser or SceneKind.Media) || scene.Effects.Length == 0,
                "effect.unsupported", "Typed template effects are not supported on recorded/imported footage in v1.");
            Editing.Validate(scene);
            Guard.Require(scene.VisualDescription is null || scene.VisualDescription.Length <= 10000,
                "description.length", "Visual description exceeds limit.");
            if (scene.Capture is { } capture)
            {
                Guard.Require(scene.Kind == SceneKind.Browser, "capture.kind", "Jellyfin profile applies to browser scenes only.");
                JellyfinCapture.Validate(capture);
            }
            if (scene.Edit?.AnchorAssetId is { } anchors)
                Guard.Require(project.Assets.Any(a => a.Id == anchors), "edit.anchor-asset", "Missing hashed anchor ledger asset.");
        }
    }

    public static Timeline Resolve(Project project, string projectHash, Func<Scene, long?> visualFrames)
    {
        long cursor = 0;
        var scenes = new List<ResolvedScene>();
        foreach (var scene in project.Scenes)
        {
            if (scene.TransitionIn is { } transition)
            {
                Guard.Require(scenes.Count > 0, "transition.fields", "First scene cannot have an incoming bridge.");
                Transitions.Validate(transition);
            }
            var narration = scene.Narration ?? throw new VideoException("narration.required", scene.Id);
            var audioLength = Editing.AudioLength(scene);
            var speechFrames = project.Fps.Frames(audioLength, narration.SampleRate, Rounding.Ceiling);
            var available = visualFrames(scene);
            var frames = scene.Timing switch {
                TimingMaster.Narration => speechFrames,
                TimingMaster.Fixed => scene.FixedFrames,
                TimingMaster.Action => available ?? throw new VideoException("timing.action", $"No acquired visual duration: {scene.Id}"),
                _ => throw new VideoException("timing.master", "Unknown timing master.")
            };
            Guard.Require(frames >= speechFrames && frames > 0 && frames <= 18_000,
                "timing.overrun", $"Narration exceeds authored visual duration or duration bound: {scene.Id}");
            Guard.Require(available is null || available >= frames || scene.AllowHold,
                "timing.underlength", $"Visual take too short; author an explicit hold: {scene.Id}");
            if (scene.Edit?.TailHold is not null)
                Guard.Require(available == frames, "edit.tail-duration",
                    "An explicit edited tail must render the full selected duration plus exactly its declared hold; use action timing or matching fixed duration.");
            Guard.Require(scene.Effects.All(e => e.EndFrame <= frames), "effect.overrun", $"Effect exceeds scene: {scene.Id}");
            Visuals.Validate(scene.Visual, scene.Kind, scene.Text, frames);
            MotionVisuals.Validate(scene.Motion, scene.Kind, scene.Visual is not null || scene.VisualAssetId is not null, frames);
            var deliverySamples = project.Fps.Samples(frames, 48000, Rounding.Nearest);
            var bridge = scene.TransitionIn?.Frames ?? 0;
            cursor = checked(cursor + bridge);
            scenes.Add(new ResolvedScene(scene.Id, scene.Kind, cursor, frames, narration.AssetId,
                audioLength, deliverySamples, scene.VisualAssetId, scene.AudioSelection?.StartSample ?? 0, bridge));
            cursor = checked(cursor + frames);
        }
        var timeline = new Timeline(1, projectHash, Files.Fingerprint(new { project, scenes }), project.Fps, 48000, cursor, scenes.ToArray());
        _ = Editing.Chapters(project, timeline);
        return timeline;
    }
}
