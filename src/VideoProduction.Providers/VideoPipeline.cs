using VideoProduction;
using Microsoft.Extensions.AI;

namespace VideoProduction.Providers;

public sealed record VideoArtifact(string Path, string Sha256, string Kind);
public interface IVideoJobPipeline
{
    void EnsureConfigured(VideoRequest request);
    Task<VideoArtifact[]> Execute(string jobRoot, VideoRequest request, Action<string> progress, CancellationToken ct);
}
public sealed record VideoRequest(string Prompt, VideoConstraints Constraints, VideoOutline? Plan = null);

public sealed class ProductionVideoPipeline(IVideoPlanner planner, GenerationConfiguration? config,
    IImageGenerator? imageGenerator = null) : IVideoJobPipeline
{
    public void EnsureConfigured(VideoRequest request)
    {
        ValidatePrerequisites(config);
        if (request.Plan is null) planner.EnsureConfigured();
        else ChatVideoPlanner.Validate(request.Plan, request.Constraints);
        ImportedVisuals.Validate(config!.ImportedVisuals, request.Plan?.Scenes.Length);
        Guard.Require(config.ImportedVisuals is null || request.Plan is not null
            && request.Plan.Scenes.All(s => s.IllustrationPrompt is null),
            "generation.import", "Do not combine imported visual bindings with generated illustrations.");
        Guard.Require(request.Plan?.Scenes.Any(s => s.IllustrationPrompt is not null) != true
            || imageGenerator is not null || config.Illustration is not null || config.RoutedIllustration is not null,
            "image.unconfigured", "Illustration prompts need an explicitly configured image provider; no backend or fallback is implicit.");
    }

    public static void ValidatePinnedAssets(GenerationConfiguration setup)
    {
        foreach (var (path, hash, kind) in new[]
        {
            (setup.FontFile, setup.FontSha256, "font"),
            (setup.FontLicenseFile, setup.FontLicenseSha256, "license")
        })
        {
            var source = PrivateFiles.LocalPath(path);
            Guard.Require(File.Exists(source), $"generation.{kind}-missing",
                $"The configured {kind} file is missing. Restore the exact pinned asset; no substitute was selected.");
            Guard.Require(Files.ValidHash(hash) && Files.Hash(source) == hash, $"generation.{kind}-pin",
                $"The configured {kind} bytes do not match their SHA256 pin. No asset was substituted or repinned.");
        }
    }

    public static void ValidatePrerequisites(GenerationConfiguration? setup)
    {
        Guard.Require(setup is not null, "generation.unconfigured",
            "Video generation needs explicitly provisioned tools, font/license and MEAI speech configuration.");
        ValidatePinnedAssets(setup!);
        setup!.NarrationPolicy?.Validate();
        Guard.Require(setup.NarrationPolicy?.AnalyzeSpeech != true || setup.TranscriberConfigFile is not null,
            "generation.analysis-config", "Explicit speech QA requires a pinned transcriber configuration; default-off script captions do not.");
        Guard.Require(File.Exists(PrivateFiles.LocalPath(setup!.ToolsFile)), "generation.tools-missing",
            "The configured production tools file is missing.");
        var tools = ResolveTools(PrivateFiles.Read<Tools>(setup.ToolsFile));
        Guard.Require(tools.Kokoro is null && (setup.FrozenNarration is not null || tools.Speech is not null), "generation.speech",
            "Generation requires the configured MEAI speech path; no legacy fallback is used.");
        if (setup.FrozenNarration is { } frozen)
        {
            RequiredFile(frozen.ProjectFile, "frozen-project");
            Guard.Require(Files.ValidHash(frozen.ProjectSha256) && Files.Hash(frozen.ProjectFile) == frozen.ProjectSha256,
                "generation.frozen-pin", "The explicitly selected frozen narration project differs from its pin.");
            var frozenProject = Json.Read<Project>(frozen.ProjectFile);
            Validation.Validate(frozenProject, Path.GetDirectoryName(frozen.ProjectFile)!);
            Guard.Require(frozenProject.Scenes.All(s => s.Narration is not null), "generation.frozen-narration",
                "Explicit frozen narration must supply verified measured narration for every scene.");
        }
        else
        {
            var speech = tools.Speech!;
            if (speech.Provider == NarrationSpeechClient.Kokoro)
            {
                var assetId = string.Equals(speech.Quality, "fp32", StringComparison.OrdinalIgnoreCase)
                    ? MediaStudio.Speech.Kokoro.KokoroAssetPaths.Fp32AssetId : MediaStudio.Speech.Kokoro.KokoroAssetPaths.QuantizedAssetId;
                Guard.Require(new MediaStudio.Configuration.ModelAssets(MediaStudio.Configuration.ModelManifest.LoadDefault()).GetPath(assetId) is not null,
                    "generation.speech-assets-missing",
                    $"Kokoro model assets '{assetId}' are missing. Run 'assets ensure --id {assetId}'; no model was downloaded.");
                Guard.Require(MediaStudio.Configuration.LocalRuntimeDetector.FindEspeakNg() is not null, "generation.speech-espeak-missing",
                    "espeak-ng is required for Kokoro phonemization; install espeak-ng and put it on PATH.");
            }
        }
        if ((setup.NarrationPolicy is null || setup.NarrationPolicy.AnalyzeSpeech)
            && setup.TranscriberConfigFile is { } transcriberFile)
        {
            RequiredFile(transcriberFile, "transcriber-config");
            var transcriber = PrivateFiles.Read<TranscriberConfig>(transcriberFile);
            RequiredFile(transcriber.Executable, "transcriber-executable");
            RequiredFile(transcriber.ModelPath, "transcriber-model");
            foreach (var dependency in transcriber.Dependencies)
                RequiredFile(Files.Under(Path.GetDirectoryName(transcriber.Executable)!, dependency.Path), "transcriber-dependency");
            _ = Files.FindExecutable(transcriber.Ffmpeg);
        }
    }

    private static void RequiredFile(string path, string kind) => Guard.Require(
        File.Exists(PrivateFiles.LocalPath(path)), $"generation.{kind}-missing",
        $"A configured {kind} dependency is missing. Provision the original dependency; no fallback was started.");

    public async Task<VideoArtifact[]> Execute(string jobRoot, VideoRequest request, Action<string> progress, CancellationToken ct)
    {
        EnsureConfigured(request);
        var setup = config!;
        progress("planning");
        var planned = request.Plan is { } provided ? new PlannedVideo(provided, "caller-supplied-plan")
            : planner is IDurableVideoPlanner durable
                ? await durable.PlanBound(jobRoot, request.Prompt, request.Constraints, ct)
                : await planner.Plan(request.Prompt, request.Constraints, ct);
        ChatVideoPlanner.Validate(planned.Outline, request.Constraints);
        using var configuredImages = imageGenerator is not null || planned.Outline.Scenes.All(s => s.IllustrationPrompt is null) ? null
            : setup.RoutedIllustration is { } routedSetup ? await IllustrationProviders.CreateRoutedAsync(routedSetup, setup, jobRoot, cancellationToken: ct)
            : setup.Illustration is { } illustrationSetup ? IllustrationProviders.Create(illustrationSetup, setup, jobRoot) : null;
        var images = imageGenerator ?? configuredImages;
        Guard.Require(planned.Outline.Scenes.All(s => s.IllustrationPrompt is null) || images is not null,
            "image.unconfigured", "Illustrations need an explicitly configured image adapter, not a placeholder or fallback.");
        planned = planned with { OriginalPrompt = request.Prompt, Constraints = request.Constraints };
        Json.Write(Path.Combine(jobRoot, "planning.json"), planned);
        var tools = ResolveTools(PrivateFiles.Read<Tools>(setup.ToolsFile));
        Guard.Require(tools.Kokoro is null && (setup.FrozenNarration is not null || tools.Speech is not null), "generation.speech",
            "MCP generation requires the explicit current MEAI speech path; no legacy CLI fallback.");
        var projectRoot = Path.Combine(jobRoot, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "assets"));
        ValidatePinnedAssets(setup);
        foreach (var (path, hash, name) in new[]
        {
            (setup.FontFile, setup.FontSha256, "font.ttf"),
            (setup.FontLicenseFile, setup.FontLicenseSha256, "OFL.txt")
        })
        {
            var source = PrivateFiles.LocalPath(path);
            File.Copy(source, Path.Combine(projectRoot, "assets", name), overwrite: false);
        }
        var planningAsset = Path.Combine(projectRoot, "assets", "planning.json");
        File.Copy(Path.Combine(jobRoot, "planning.json"), planningAsset);
        var scenes = planned.Outline.Scenes.Select((scene, index) => new Scene($"scene-{index + 1:D2}",
            scene.Kind switch { TemplateKind.Title => SceneKind.Title, TemplateKind.Code => SceneKind.Code, _ => SceneKind.Diagram },
            scene.Title, scene.Text, scene.Script, TimingMaster.Narration, 0, false, null, null, [], Visual: scene.Visual, Motion: scene.Motion)).ToArray();
        var project = new Project(1, "prompt-video", 1920, 1080, new Rate(30, 1),
            [new Asset("font", "assets/font.ttf", setup.FontSha256, "Explicitly configured font; adjacent OFL.txt", "SIL OFL; reviewed configured notice"),
                new Asset("planning-evidence", "assets/planning.json", Files.Hash(planningAsset),
                    planned.PlanningMode == "caller-supplied-plan" ? "Explicit caller-supplied plan; not a server model invocation"
                        : "Actual configured IChatClient output; configured/returned model identities retained",
                    "Caller/model-authored content; rights and factual review required")],
            scenes, scenes.Select(s => new Chapter(s.Id, s.Id, s.Title)).ToArray(), NarrationPolicy: setup.NarrationPolicy);
        if (setup.FrozenNarration is { } frozen)
            project = ReuseNarration(project, projectRoot, frozen);
        if (planned.Outline.Scenes.Any(s => s.IllustrationPrompt is not null))
        {
            progress("illustrating");
            for (var index = 0; index < planned.Outline.Scenes.Length; index++)
            {
                if (planned.Outline.Scenes[index].IllustrationPrompt is not { } illustration) continue;
                var response = await images!.GenerateAsync(new ImageGenerationRequest(illustration),
                    new ImageGenerationOptions {
                        Count = 1, MediaType = "image/png",
                        ImageSize = new System.Drawing.Size(1920, 1080), ResponseFormat = ImageGenerationResponseFormat.Data
                    }, ct);
                Guard.Require(response.Contents is [DataContent { MediaType: "image/png" }]
                    && response.RawRepresentation is SvgImageEvidence or NativeImageEvidence, "image.contract",
                    "Composition accepts one validated PNG with actual local-SVG or explicitly selected native-image provenance.");
                var png = ((DataContent)response.Contents[0]).Data.ToArray();
                ChatSvgImageGenerator.ValidatePng(png);
                var evidence = response.RawRepresentation!;
                var imageHash = evidence switch {
                    SvgImageEvidence svg => svg.PngSha256,
                    NativeImageEvidence native => native.PngSha256,
                    _ => throw new VideoException("image.contract", "Image provenance is missing.")
                };
                var provenance = evidence is NativeImageEvidence
                    ? "Explicit native image model; locally fitted without distortion and with a caption-safe margin"
                    : "IChatClient-authored validated SVG rasterized locally; not a native image model";
                var imagePath = $"assets/illustration-{index + 1:D2}.png";
                var evidencePath = $"assets/illustration-{index + 1:D2}.json";
                File.WriteAllBytes(Files.Under(projectRoot, imagePath), png);
                Guard.Require(Files.Hash(Files.Under(projectRoot, imagePath)) == imageHash,
                    "image.evidence", "Image adapter evidence differs from returned PNG.");
                Json.Write(Files.Under(projectRoot, evidencePath), evidence);
                var imageId = $"illustration-{index + 1:D2}";
                project = project with {
                    Assets = [.. project.Assets,
                    new Asset(imageId, imagePath, imageHash, provenance, "Generated illustration; rights/factual review required"),
                    new Asset(imageId + "-evidence", evidencePath, Files.Hash(Files.Under(projectRoot, evidencePath)),
                        "Actual selected-provider/raster evidence; model identity retained", "Generated illustration provenance")],
                    Scenes = project.Scenes.Select((s, i) => i == index ? s with {
                        Kind = SceneKind.Media,
                        VisualAssetId = imageId,
                        VisualDescription = setup.FrozenNarration is null
                                ? provenance + ", not a capture: " + illustration : s.VisualDescription
                    } : s).ToArray()
                };
            }
        }
        var projectFile = Path.Combine(projectRoot, "project.json");
        project = ImportedVisuals.Apply(project, projectRoot, setup.ImportedVisuals);
        Json.Write(projectFile, project);
        var production = new Production(projectFile, tools);
        if (setup.FrozenNarration is null)
        {
            progress("narrating");
            await production.Narrate(ct);
        }
        if (setup.FrozenNarration is null && setup.NarrationPolicy is null && setup.TranscriberConfigFile is { } transcriberFile)
        {
            var transcriber = PrivateFiles.Read<TranscriberConfig>(transcriberFile);
            progress("transcribing");
            foreach (var scene in scenes)
            {
                await production.Transcribe(scene.Id, transcriber, ct);
                var transcribed = production.Load();
                var transcriptId = transcribed.Scenes.Single(s => s.Id == scene.Id).Narration!.TranscriptAssetId;
                var asset = transcribed.Assets.Single(a => a.Id == transcriptId);
                var report = Json.Read<TranscriptLearning>(Files.Under(projectRoot, asset.Path));
                var identity = TranscriptIdentity(jobRoot, request);
                var decisions = setup.TranscriptDecisionFile is { } decisionFile
                    ? PrivateFiles.Read<BoundTranscriptDecision[]>(decisionFile) : [];
                var decision = TranscriptDecision(identity.RequestHash, identity.IdempotencyHash, scene.Id, report, decisions);
                production.AdoptTranscript(scene.Id, decision,
                    "Observed whole ASR segments retained; human caption reading/segmentation review required.");
            }
        }
        if (setup.FrozenNarration is null && setup.NarrationPolicy?.AnalyzeSpeech == true)
        {
            progress("analyzing-speech");
            var transcriber = PrivateFiles.Read<TranscriberConfig>(setup.TranscriberConfigFile!);
            foreach (var scene in scenes) await production.AnalyzeSpeech(scene.Id, transcriber, ct);
        }
        var resolved = await production.Resolve(ct);
        Guard.Require(resolved.TotalFrames <= request.Constraints.MaxDurationSeconds * 30L,
            "generation.duration", "Actual narration exceeds the requested duration; no speech is clipped, stretched or silently shortened.");
        Validation.Validate(production.Load(), projectRoot);
        ct.ThrowIfCancellationRequested();
        progress("rendering");
        var output = await production.Render(preview: false, ct);
        ct.ThrowIfCancellationRequested();
        progress("exporting");
        var bundle = production.Export(Path.Combine(jobRoot, "source.zip"));
        var result = new List<VideoArtifact>();
        foreach (var (path, kind) in new[]
        {
            (projectFile, "project"), (Path.Combine(jobRoot, "planning.json"), "planning-evidence"),
            (Path.Combine(projectRoot, output, "movie.mp4"), "candidate-mp4"),
            (Path.Combine(projectRoot, output, "captions.srt"), "captions-srt"),
            (Path.Combine(projectRoot, output, "checks.json"), "checks"),
            (Path.Combine(projectRoot, output, "captions.vtt"), "captions-vtt"), (bundle, "source-zip")
        })
            result.Add(new VideoArtifact(Path.GetRelativePath(jobRoot, path).Replace('\\', '/'), Files.Hash(path), kind));
        return result.ToArray();
    }

    public static Tools ResolveTools(Tools tools)
    {
        var resolved = tools with {
            SourceRoot = PrivateFiles.LocalPath(tools.SourceRoot),
            Node = Files.FindExecutable(tools.Node),
            Ffmpeg = Files.FindExecutable(tools.Ffmpeg),
            Ffprobe = Files.FindExecutable(tools.Ffprobe)
        };
        Guard.Require(Directory.Exists(resolved.SourceRoot), "tool.source", "The explicitly configured renderer source root is missing.");
        return resolved;
    }

    private static Project ReuseNarration(Project authored, string destination, FrozenProjectConfiguration frozen)
    {
        var sourceFile = PrivateFiles.LocalPath(frozen.ProjectFile);
        Guard.Require(Files.ValidHash(frozen.ProjectSha256) && Files.Hash(sourceFile) == frozen.ProjectSha256,
            "generation.frozen", "Operator-pinned frozen project differs from its exact hash.");
        var sourceRoot = Path.GetDirectoryName(sourceFile)!;
        var source = Json.Read<Project>(sourceFile);
        Validation.Validate(source, sourceRoot);
        Guard.Require(source.Width == authored.Width && source.Height == authored.Height && source.Fps == authored.Fps
            && source.Scenes.Length == authored.Scenes.Length && source.DeliveryCaptions is null
            && source.Assets.Single(a => a.Id == "font").Sha256 == authored.Assets.Single(a => a.Id == "font").Sha256,
            "generation.frozen", "Frozen narration requires the same scene count/clock and scene-local caption evidence.");
        for (var i = 0; i < authored.Scenes.Length; i++)
        {
            var from = source.Scenes[i];
            var to = authored.Scenes[i];
            Guard.Require(from.Id == to.Id && from.Kind == to.Kind && from.Script == to.Script
                && from.Title == to.Title && from.Text == to.Text && from.Narration is { TranscriptPending: false }
                && from.Capture is null && from.Edit is null && from.AudioSelection is null && from.TransitionIn is null
                && from.Timing == TimingMaster.Narration,
                "generation.frozen", "Frozen narration may not silently change scene script/identity/timing or import capture actions.");
        }
        var extra = source.Assets.Where(a => a.Id is not "font" and not "planning-evidence").ToArray();
        foreach (var asset in extra)
        {
            Files.VerifyAsset(sourceRoot, asset);
            ExportPrivacy.CheckAsset(Files.Under(sourceRoot, asset.Path));
            var target = Files.Under(destination, asset.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Files.Under(sourceRoot, asset.Path), target, overwrite: false);
        }
        var evidence = Path.Combine(destination, "assets", "frozen-narration.json");
        Json.Write(evidence, new
        {
            mode = "operator-pinned-frozen-narration", sourceProjectSha256 = frozen.ProjectSha256,
            newSpeechOrAsrInvoked = false, scenes = source.Scenes.Select(s => new { s.Id, s.Narration }).ToArray()
        });
        return authored with {
            Assets = [.. authored.Assets, .. extra,
            new Asset("frozen-narration-evidence", "assets/frozen-narration.json", Files.Hash(evidence),
                "Exact existing audio/transcript/adoption assets copied and validated; no new synthesis/ASR", "Original frozen asset rights/notices retained")],
            Scenes = authored.Scenes.Select((s, i) => s with { Narration = source.Scenes[i].Narration }).ToArray(),
            NarrationPolicy = source.NarrationPolicy
        };
    }

    public static string TranscriptDecision(string requestHash, string idempotencyHash, string sceneId,
        TranscriptLearning report, BoundTranscriptDecision[] decisions)
    {
        var matches = decisions.Where(d => d.RequestSha256 == requestHash && d.IdempotencyHash == idempotencyHash
            && d.SceneId == sceneId && d.MasterSha256 == report.MasterSha256 && d.DerivativeSha256 == report.DerivativeSha256
            && d.SegmentsSha256 == Files.Fingerprint(report.Segments) && d.ModelSha256 == report.ModelSha256
            && d.ArtifactsSha256 == Files.Fingerprint(report.TranscriberArtifacts)
            && d.IntendedScript == report.IntendedScript && d.ObservedText == report.ObservedText
            && !string.IsNullOrWhiteSpace(d.Reason)).ToArray();
        Guard.Require(matches.Length <= 1, "transcription.decision", "Ambiguous trusted operator transcript decisions.");
        return matches.SingleOrDefault()?.Reason ?? "";
    }

    private static (string RequestHash, string IdempotencyHash) TranscriptIdentity(string root, VideoRequest request)
    {
        if (File.Exists(Path.Combine(root, "attempt.json")))
        {
            var attempt = Json.Read<FileAttempt>(Path.Combine(root, "attempt.json"));
            Guard.Require(attempt.SchemaVersion == 1 && attempt.RequestSha256 == Files.Fingerprint(request),
                "attempt.identity", "Transcription decision does not bind this exact production request.");
            return (attempt.RequestSha256, attempt.IdempotencyHash);
        }
        var legacy = Json.Read<VideoJob>(Path.Combine(root, "job.json"));
        Guard.Require(legacy.RequestHash == Files.Fingerprint(request), "jobs.identity", "Legacy transcription request identity changed.");
        return (legacy.RequestHash, legacy.IdempotencyHash);
    }
}
