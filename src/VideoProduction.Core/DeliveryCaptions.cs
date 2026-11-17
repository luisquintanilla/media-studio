using System.Text.Json;

namespace VideoProduction;

public sealed record DeliveryCaptionBinding(string SourceCompositionSha256, string TimelineCompositionSha256,
    Narration Narration, string IntendedScript, string VisualDescription, string SourceMovieSha256,
    string DecodedPcmSha256, string DecoderFfmpegSha256, long DeliverySamples, string ClockEvidence);

public static class DeliveryCaptions
{
    public static string SourceComposition(Project project, string root) => Files.Fingerprint(new
    {
        version = 1, project.Width, project.Height, project.Fps, project.Chapters,
        scenes = project.Scenes.Select(scene => new
        {
            scene,
            audio = project.Assets.Single(a => a.Id == scene.Narration!.AssetId).Sha256,
            visual = scene.VisualAssetId is { } id ? project.Assets.Single(a => a.Id == id).Sha256 : "",
            anchors = scene.Edit?.AnchorAssetId is { } anchor ? project.Assets.Single(a => a.Id == anchor).Sha256 : "",
            take = scene.Kind is SceneKind.Browser or SceneKind.Terminal
                ? Files.Hash(Files.Under(root, $"build/stages/acquire-{scene.Id}.json")) : ""
        }).ToArray()
    });

    public static string TimelineComposition(string source, Timeline timeline) => Files.Fingerprint(new
    {
        source, timeline.Fps, timeline.SampleRate, timeline.TotalFrames, timeline.Scenes
    });

    public static Scene SpeechScene(DeliveryCaptionBinding binding) => new("delivery-speech", SceneKind.Title,
        "Whole selected delivery speech", "", binding.IntendedScript, TimingMaster.Narration, 0, false,
        null, binding.Narration, [], VisualDescription: binding.VisualDescription);

    public static void Validate(Project project, string root)
    {
        if (project.DeliveryCaptions is not { } binding) return;
        Guard.Require(binding.SourceCompositionSha256 == SourceComposition(project, root)
            && Files.ValidHash(binding.SourceMovieSha256) && Files.ValidHash(binding.DecodedPcmSha256)
            && Files.ValidHash(binding.DecoderFfmpegSha256) && Files.ValidHash(binding.TimelineCompositionSha256)
            && binding.DeliverySamples > 0 && !string.IsNullOrWhiteSpace(binding.ClockEvidence),
            "delivery-transcript.stale", "Ordered scene/audio source/selection/pause/edit composition changed; rebind a newly checked selected delivery master.");
        var narration = binding.Narration;
        var master = project.Assets.SingleOrDefault(a => a.Id == narration.AssetId)
            ?? throw new VideoException("delivery-transcript.master", "Missing frozen selected delivery master.");
        var info = Wav.Inspect(Files.Under(root, master.Path));
        Guard.Require(info.SampleRate == 48000 && info.Channels == 1 && info.Samples == narration.SampleCount
            && narration.SampleRate == 48000 && info.Samples >= binding.DeliverySamples
            && info.Samples <= binding.DeliverySamples + 1024 && !narration.TranscriptPending,
            "delivery-transcript.clock", "Wholemaster must be exact 48kHz mono decoded delivery PCM; only documented final AAC padding is permitted.");
        var transcript = project.Assets.SingleOrDefault(a => a.Id == narration.TranscriptAssetId)
            ?? throw new VideoException("delivery-transcript.report", "Missing selectedmaster STT evidence.");
        var report = Json.Read<TranscriptLearning>(Files.Under(root, transcript.Path));
        var scene = SpeechScene(binding);
        TranscriptProduction.ValidateBinding(project, scene, report, root);
        TranscriptProduction.ValidateAdoption(project, scene, report, root, transcript.Sha256);
        Guard.Require(narration.Cues.Length > 0 && narration.Cues.All(c => c.StartSample >= 0
            && c.EndSample > c.StartSample && c.EndSample <= binding.DeliverySamples),
            "delivery-transcript.padding", "Observed cues cannot extend into AAC padding beyond the selected visual timeline.");
        foreach (var cue in narration.Cues) Captions.ValidateCue(cue, narration.SampleRate);
    }

    public static ProjectedCaption[] Project(Project project, Timeline timeline)
    {
        var binding = project.DeliveryCaptions!;
        Guard.Require(binding.TimelineCompositionSha256 == TimelineComposition(binding.SourceCompositionSha256, timeline)
            && binding.DeliverySamples == timeline.Fps.Samples(timeline.TotalFrames, 48000, Rounding.Nearest),
            "delivery-transcript.timeline", "Resolved source clocks/offsets/pauses/duration changed; frozen wholemaster captions are stale.");
        return binding.Narration.Cues.Select(c => new ProjectedCaption((c.StartSample + 24) / 48,
            (c.EndSample + 24) / 48, c.Text, c.Evidence, c.PolicyException)).ToArray();
    }
}

public sealed partial class Production
{
    private async Task<string> DecodedPcmHash(string file, CancellationToken ct)
    {
        var result = await Ffmpeg(["-v", "error", "-i", file, "-map", "0:a:0", "-vn", "-ar", "48000",
            "-ac", "1", "-c:a", "pcm_s16le", "-f", "hash", "-hash", "sha256", "-"], ct);
        var value = result.Stdout.Trim();
        Guard.Require(value.StartsWith("SHA256=", StringComparison.Ordinal) && Files.ValidHash(value[7..]),
            "delivery-transcript.pcm", "Could not hash actual decoded 48kHz PCM.");
        return value[7..];
    }

    public async Task<object> BindDeliveryTranscript(string transcriptProjectFile, string movie, CancellationToken ct)
    {
        var timeline = await Resolve(ct);
        var project = Load();
        movie = Path.GetFullPath(movie);
        Guard.Require(movie.StartsWith(PathFor("output/candidate") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "delivery-transcript.candidate", "Bind the actual checked candidate under this source project.");
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(movie)!, "provenance.json")));
        Guard.Require(provenance.RootElement.GetProperty("projectHash").GetString() == Files.Hash(ProjectFile),
            "delivery-transcript.candidate", "Candidate was not rendered from the current exact source project.");
        await CheckFile(movie, timeline, ct);
        var sourceRoot = Path.GetDirectoryName(Path.GetFullPath(transcriptProjectFile))!;
        var speech = Json.Read<Project>(transcriptProjectFile);
        Validation.Validate(speech, sourceRoot);
        Guard.Require(speech.Scenes.Length == 1 && speech.DeliveryCaptions is null && speech.Scenes[0].AudioSelection is null,
            "delivery-transcript.input", "Supply one full untrimmed selectedmaster scene, successfully transcribed/adopted.");
        var scene = speech.Scenes[0];
        var narration = scene.Narration!;
        Guard.Require(narration.TranscriptAssetId is not null && narration.TranscriptAdoptionAssetId is not null,
            "delivery-transcript.adoption", "Selectedmaster scene must have actual explicitly adopted STT, not script-derived cues.");
        var master = speech.Assets.Single(a => a.Id == narration.AssetId);
        var masterPath = Files.Under(sourceRoot, master.Path);
        var pcm = await DecodedPcmHash(movie, ct);
        Guard.Require(pcm == await DecodedPcmHash(masterPath, ct), "delivery-transcript.pcm",
            "Selectedmaster PCM does not equal the actual decoded candidate audio; never substitute source WAV identity for lossy AAC.");
        var assets = project.Assets.ToList();
        var ids = new Dictionary<string, string>();
        foreach (var asset in speech.Assets.Where(a => a.Id != "font"))
        {
            ExportPrivacy.CheckAsset(Files.Under(sourceRoot, asset.Path));
            var id = $"delivery-{asset.Id[..Math.Min(asset.Id.Length, 30)]}-{asset.Sha256[..12]}";
            var relative = $"assets/delivery/{asset.Sha256[..16]}/{Path.GetFileName(asset.Path)}";
            var target = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) File.Copy(Files.Under(sourceRoot, asset.Path), target);
            Guard.Require(Files.Hash(target) == asset.Sha256, "delivery-transcript.asset", "Frozen selectedmaster evidence hash changed.");
            if (assets.All(a => a.Id != id)) assets.Add(asset with { Id = id, Path = relative });
            ids.Add(asset.Id, id);
        }
        narration = narration with {
            AssetId = ids[narration.AssetId],
            TranscriptAssetId = ids[narration.TranscriptAssetId!],
            TranscriptAdoptionAssetId = ids[narration.TranscriptAdoptionAssetId!]
        };
        var composition = DeliveryCaptions.SourceComposition(project, Root);
        var binding = new DeliveryCaptionBinding(composition, DeliveryCaptions.TimelineComposition(composition, timeline),
            narration, scene.Script, scene.VisualDescription ?? "", Files.Hash(movie), pcm, Files.Hash(tools.Ffmpeg),
            timeline.Fps.Samples(timeline.TotalFrames, 48000, Rounding.Nearest),
            "48kHz mono PCM16 decoded from exact checked AAC candidate with FFmpeg skip-sample/edit-list semantics; compare decoded PCM on every rerender. Not original pre-encode waveform identity. At most1024final AAC-padding samples excluded from captions.");
        var updated = project with { Assets = assets.ToArray(), DeliveryCaptions = binding };
        Validation.Validate(updated, Root);
        Json.Write(ProjectFile, updated);
        await Resolve(ct);
        return new
        {
            bound = true, binding.SourceCompositionSha256, binding.SourceMovieSha256, binding.DecodedPcmSha256,
            acousticCaptionSource = "actual full selected delivery master", humanReviewRequired = true
        };
    }
}
