using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace VideoProduction;

public sealed partial class Production(string projectFile, Tools tools)
{
    public string Root { get; } = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
    public string ProjectFile { get; } = Path.GetFullPath(projectFile);
    private string PathFor(string relative) => Files.Under(Root, relative);
    public Project Load() => Json.Read<Project>(ProjectFile);

    public ActionPlan Plan(string? privateAuthFile = null)
    {
        var plan = Planning.Create(Load(), Root, Files.Hash(ProjectFile), tools, privateAuthFile);
        Json.Write(PathFor("build/action-plan.json"), plan);
        return plan;
    }

    public Approval Approve(string by, string? privateAuthFile = null)
    {
        var plan = Json.Read<ActionPlan>(PathFor("build/action-plan.json"));
        var expected = Planning.Create(Load(), Root, Files.Hash(ProjectFile), tools, privateAuthFile);
        Guard.Require(Json.Text(expected) == Json.Text(plan), "approval.changed", "Regenerate the changed plan before approval.");
        var approval = new Approval(1, plan.Fingerprint, by, JellyfinCapture.Scope(Load()));
        Planning.Authorize(expected, plan, approval);
        Json.Write(PathFor("review/approval.json"), approval);
        return approval;
    }

    public async Task Acquire(CancellationToken ct, string? privateAuthFile = null)
    {
        var project = Load();
        using var authLease = privateAuthFile is null ? null
            : new FileStream(privateAuthFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        var expected = Planning.Create(project, Root, Files.Hash(ProjectFile), tools, privateAuthFile);
        Planning.Authorize(expected, Json.Read<ActionPlan>(PathFor("build/action-plan.json")),
            Json.Read<Approval>(PathFor("review/approval.json")));
        foreach (var action in expected.Actions)
        {
            ct.ThrowIfCancellationRequested();
            var fingerprint = Files.Fingerprint(new { action, expected.WorkerSha256 });
            var record = PathFor($"build/stages/acquire-{action.Id}.json");
            if (Atomic.IsCurrent(Root, record, fingerprint)) continue;
            var finalRelative = $"assets/takes/{action.Id}";
            var final = PathFor(finalRelative);
            Guard.Require(!Directory.Exists(final), "take.immutable", $"Incomplete/changed take exists: {finalRelative}. Preserve it and author a new scene ID, or explicitly remove the failed take.");
            var staging = PathFor($"build/staging/{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            if (action.Kind == "bundled-counter")
            {
                var font = project.Assets.Single(a => a.Id == "font");
                Json.Write(action.Argv[^1], new { outputDirectory = staging, fontPath = PathFor(font.Path), fontSha256 = font.Sha256 });
                await Processes.Run(action.Executable, action.Argv, action.Workspace, ct,
                    action.TimeoutSeconds, action.MaxOutputBytes);
                var probe = await Media.Probe(tools.Ffprobe, Path.Combine(staging, "browser.webm"), ct);
                Guard.Require(probe.Width == 1920 && probe.Height == 1080 && probe.Frames > 50
                    && probe.FrameRate == "25/1", "capture.profile", "Actual browser take does not match 1080p/native25fps profile.");
                Json.Write(Path.Combine(staging, "probe.json"), probe);
                using var landmarks = JsonDocument.Parse(File.ReadAllText(Path.Combine(staging, "landmarks.json")));
                var captureEnd = landmarks.RootElement.GetProperty("ledger").EnumerateArray().Last()
                    .GetProperty("milliseconds").GetDouble() / 1000;
                var clock = await Editing.ProbeClock(tools, Path.Combine(staging, "browser.webm"), ct);
                Json.Write(Path.Combine(staging, "source-clock.json"), clock);
                await CaptureAnchors.Bind(tools, staging, clock, ct);
                var anchors = Json.Read<AnchorLedger>(Path.Combine(staging, "anchors.json"));
                var before = anchors.Anchors.Single(a => a.Id == "before");
                var after = anchors.Anchors.Single(a => a.Id == "after");
                Guard.Require(before.ObservationSourceFrame < after.ObservationSourceFrame
                    && after.EndSourceFrame - after.StartSourceFrame >= 25,
                    "capture.clock", "Encoded source does not retain both actual asserted states and at least one second of final state.");
                var samples = await Processes.Run(tools.Ffmpeg,
                    ["-v", "error", "-protocol_whitelist", "file,pipe", "-i", Path.Combine(staging, "browser.webm"),
                        "-vf", "fps=5", "-an", "-f", "framemd5", "-"], Root, ct);
                var hashes = samples.Stdout.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .Select(l => l.Split(',')[^1].Trim()).ToArray();
                Guard.Require(hashes.Distinct().Count() >= 6, "capture.frames", "Actual browser capture lacks distinct sampled frames.");
                Json.Write(Path.Combine(staging, "quality.json"), new
                {
                    sampledAtFps = 5, sampledFrames = hashes.Length,
                    distinctFrames = hashes.Distinct().Count(), monotonicCaptureEndSeconds = captureEnd, actualDuration = probe.Duration,
                    note = "Actual asserted states are screenshot/ROI matched to decoded source. Monotonic navigation duration is a separate clock; recorder startup is not assumed fixed. Not automatic legibility or exact action-PTS alignment."
                });
            }
            else if (action.Kind == "reviewed-jellyfin")
            {
                Guard.Require(privateAuthFile is not null && File.Exists(privateAuthFile), "capture.auth",
                    "Supply --private-auth FILE outside the project. Disposable dedicated-user credentials are stdin only, never project assets.");
                var authPath = Path.GetFullPath(privateAuthFile!);
                Guard.Require(!authPath.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "capture.private-path", "Private credentials must live outside the source project/export root.");
                var scene = project.Scenes.Single(s => s.Id == action.Id);
                Json.Write(action.Argv[^1], new { outputDirectory = staging, profile = scene.Capture });
                await Processes.Run(action.Executable, action.Argv, action.Workspace, ct,
                    action.TimeoutSeconds, action.MaxOutputBytes, standardInput: File.ReadAllText(authPath));
                var visual = Path.Combine(staging, "browser.webm");
                var clock = await Editing.ProbeClock(tools, visual, ct);
                Guard.Require(clock.Width == 1920 && clock.Height == 1080 && clock.Fps == new Rate(25, 1),
                    "capture.profile", "Actual Jellyfin source profile differs from pinned 1080p25 recording.");
                Json.Write(Path.Combine(staging, "source-clock.json"), clock);
                // The worker stores genuine screenshots and stable DOM intervals. Bind their observed
                // frame identities to decoded source frames before allowing editorial annotations.
                await CaptureAnchors.Bind(tools, staging, clock, ct);
                Json.Write(Path.Combine(staging, "probe.json"), await Media.Probe(tools.Ffprobe, visual, ct));
                ExportPrivacy.CheckTake(staging);
            }
            else
            {
                var result = await Processes.Run(action.Executable, action.Argv, action.Workspace, ct,
                    action.TimeoutSeconds, action.MaxOutputBytes, requireSuccess: false);
                Json.Write(Path.Combine(staging, "process.json"), result);
                Atomic.Write(Path.Combine(staging, "stdout.txt"), result.Stdout);
                Atomic.Write(Path.Combine(staging, "stderr.txt"), result.Stderr);
                Guard.Require(result.ExitCode == 0 && result.Stdout.Contains("sum: 15", StringComparison.Ordinal),
                    "capture.assertion", "Actual CLI sum assertion failed; take is not committed.");
            }
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            Directory.Move(staging, final);
            var outputs = Directory.GetFiles(final).Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/')).ToArray();
            Atomic.Commit(Root, record, fingerprint, outputs);
        }
    }

    public async Task Narrate(CancellationToken ct)
    {
        var project = Load();
        project.NarrationPolicy?.Validate();
        Guard.Require(tools.Speech is not null || tools.Kokoro?.Execution == "legacy-cli", "speech.unconfigured",
            "Configure speech for the MEAI-first local provider, or explicitly choose kokoro.execution:'legacy-cli'. No silent fallback or provisioning.");
        Guard.Require(tools.Speech is null || tools.Kokoro is null, "speech.ambiguous",
            "Choose MEAI speech or explicit legacy Kokoro CLI, not both configurations.");
        Guard.Require(tools.Speech?.EndGuardMilliseconds is null or >= 0 and <= 1000,
            "narration.end-guard", "Configured narration end guard must be 0..1000 milliseconds; zero preserves the original provider take.");
        using var speech = tools.Speech is { } config ? NarrationSpeechClient.Create(config) : null;
        INarrationBackend backend = speech is not null
            ? speech.CreateBackend(new SpeechRequest(tools.Speech!.Voice, tools.Speech.Language, tools.Speech.Speed))
            : new KokoroBackend(tools.Kokoro!);
        var scenes = project.Scenes.ToArray();
        var assets = project.Assets.ToList();
        for (var i = 0; i < scenes.Length; i++)
        {
            if (scenes[i].Narration is not null) continue;
            var scene = scenes[i];
            var takeDirectory = PathFor($"assets/narration/{scene.Id}-{Guid.NewGuid():N}");
            if (project.NarrationPolicy is not null)
            {
                var measured = await ScriptCaptions.Synthesize(Root, takeDirectory, scene.Script, backend,
                    tools.Speech?.EndGuardMilliseconds ?? 0, ct);
                var prefix = "phrase-" + Guid.NewGuid().ToString("N")[..16];
                var files = Directory.EnumerateFiles(takeDirectory, "*", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.Ordinal).ToArray();
                var added = files.Select((file, index) => new Asset($"{prefix}-{index:D3}",
                    Path.GetRelativePath(Root, file).Replace('\\', '/'), Files.Hash(file),
                    "Script-phrases-v1: unchanged provider phrase takes, actual concatenated PCM, measured cues and explicit scene end guard; TakeWindow, not word alignment or acoustic approval",
                    "Original model/voice/provider notices apply; authored text rights require review")).ToArray();
                assets.AddRange(added);
                var audio = added.Single(a => PathFor(a.Path) == measured.Take.WavPath);
                var receipt = added.Single(a => PathFor(a.Path) == measured.ReceiptPath);
                scenes[i] = scene with {
                    Narration = new Narration(audio.Id, measured.Take.SampleRate,
                    measured.Take.SampleCount, scene.Script, measured.Receipt.Cues, ScriptCaptionAssetId: receipt.Id)
                };
                project = project with { Assets = assets.ToArray(), Scenes = scenes.ToArray() };
                Json.Write(ProjectFile, project);
                continue;
            }
            var take = await backend.Synthesize(scene.Script, takeDirectory, ct);
            var id = $"voice-{scene.Id[..Math.Min(32, scene.Id.Length)]}-{Guid.NewGuid().ToString("N")[..16]}";
            if (tools.Speech is { EndGuardMilliseconds: > 0 } guardedSpeech)
            {
                assets.Add(new Asset($"{id}-provider-master", Path.GetRelativePath(Root, take.WavPath).Replace('\\', '/'),
                    take.WavSha256, "Unmodified original MEAI provider PCM before the explicitly configured end guard",
                    "Kokoro model/voice attribution retained"));
                assets.Add(new Asset($"{id}-provider-evidence", Path.GetRelativePath(Root, take.MetadataPath).Replace('\\', '/'),
                    Files.Hash(take.MetadataPath), "Original provider sample/chunk/provenance evidence, unchanged",
                    "MIT metadata; model/voice attribution retained"));
                take = NarrationEndGuard.Apply(take, guardedSpeech.EndGuardMilliseconds, ct);
            }
            assets.Add(new Asset(id, Path.GetRelativePath(Root, take.WavPath).Replace('\\', '/'),
                take.WavSha256, tools.Speech is null
                    ? "Explicit legacy direct Kokoro CLI inference; adjacent kokoro.json preserves original provenance"
                    : "MEAI ITextToSpeechClient actual local inference; adjacent speech.json binds client/provider/artifact/sample evidence",
                "Kokoro model/voice Apache-2.0; frontend external GPL dependency, not bundled"));
            assets.Add(new Asset($"{id}-evidence", Path.GetRelativePath(Root, take.MetadataPath).Replace('\\', '/'),
                Files.Hash(take.MetadataPath), "Exact sample ledger and local synthesis provenance; no word timing claims", "MIT metadata; model/voice attribution retained"));
            var seconds = (double)take.SampleCount / take.SampleRate;
            var text = WrapCaption(take.DisplayText);
            var cue = new Cue(0, take.SampleCount, text, Evidence.TakeWindow,
                seconds is >= 1 and <= 6 && text.Replace("\n", "", StringComparison.Ordinal).Length / seconds <= 20
                    ? null : "Whole-take timing only; human caption segmentation/audition required before release.");
            scenes[i] = scene with { Narration = new Narration(id, take.SampleRate, take.SampleCount, take.DisplayText, [cue]) };
            project = project with { Assets = assets.ToArray(), Scenes = scenes.ToArray() };
            Json.Write(ProjectFile, project);
        }
        Validation.Validate(project, Root);
    }

    public void ImportNarration(string sceneId, string wav, Cue[] cues, string displayText)
    {
        var project = Load();
        var scene = project.Scenes.SingleOrDefault(s => s.Id == sceneId)
            ?? throw new VideoException("scene.missing", "Unknown scene ID.");
        Guard.Require(Files.ValidId(sceneId), "scene.id", "Invalid scene ID.");
        var assetId = $"voice-{sceneId[..Math.Min(32, sceneId.Length)]}-{Files.Hash(wav)[..16]}";
        var narration = NarrationImport.Import(Root, wav, assetId, displayText, cues, out var asset);
        var updated = project with {
            Assets = project.Assets.Where(a => a.Id != assetId).Append(asset).ToArray(),
            Scenes = project.Scenes.Select(s => s.Id == sceneId ? scene with { Narration = narration } : s).ToArray()
        };
        Validation.Validate(updated, Root);
        Json.Write(ProjectFile, updated);
    }

    public async Task<Timeline> Resolve(CancellationToken ct)
    {
        var project = Load();
        Validation.Validate(project, Root);
        var lengths = new Dictionary<string, long?>();
        foreach (var scene in project.Scenes)
        {
            var narration = scene.Narration!;
            var wav = Wav.Inspect(PathFor(project.Assets.Single(a => a.Id == narration.AssetId).Path));
            Guard.Require(wav.Samples == narration.SampleCount && wav.SampleRate == narration.SampleRate,
                "narration.clock", $"Declared narration clock differs from actual WAV: {scene.Id}");
            if (scene.Kind == SceneKind.Browser)
            {
                VerifyFrozenTake(scene.Id);
                var probe = await Media.Probe(tools.Ffprobe, PathFor($"assets/takes/{scene.Id}/browser.webm"), ct);
                lengths[scene.Id] = (long)Math.Floor(probe.Duration * project.Fps.Numerator / project.Fps.Denominator + 0.00001);
            }
            else if (scene.Kind == SceneKind.Terminal)
            {
                VerifyFrozenTake(scene.Id);
                var result = Json.Read<ProcessResult>(PathFor($"assets/takes/{scene.Id}/process.json"));
                Guard.Require(result.ExitCode == 0, "take.exit", "Frozen CLI take was not successful.");
                lengths[scene.Id] = null;
            }
            else if (scene.Kind == SceneKind.Media)
            {
                var probe = await Media.Probe(tools.Ffprobe, PathFor(project.Assets.Single(a => a.Id == scene.VisualAssetId).Path), ct);
                Guard.Require(probe.Frames <= 1 || probe.ColorSpace == "bt709"
                    && probe.ColorTransfer == "bt709" && probe.ColorPrimaries == "bt709",
                    "visual.color", "Imported video must be explicitly tagged SDR BT.709; HDR/untagged video requires an authored normalization step outside this profile.");
                lengths[scene.Id] = probe.Frames > 1 ? Media.VideoFrames(probe, project.Fps) : null;
            }
            else lengths[scene.Id] = null;
            if (scene.Edit is { } edit)
            {
                var visual = scene.Kind == SceneKind.Browser ? PathFor($"assets/takes/{scene.Id}/browser.webm")
                    : PathFor(project.Assets.Single(a => a.Id == scene.VisualAssetId).Path);
                var clock = await Editing.ProbeClock(tools, visual, ct);
                Guard.Require(clock.SourceSha256 == edit.SourceSha256 && clock.Fps == edit.SourceFps
                    && edit.EndFrame <= clock.PresentationTimestamps.LongLength, "edit.stale-source", "Source selection hash/clock/range differs from actual take.");
                lengths[scene.Id] = checked(Editing.SelectedFrames(edit) + (edit.TailHold?.Frames ?? 0));
            }
        }
        var timeline = Validation.Resolve(project, Files.Hash(ProjectFile), scene => lengths[scene.Id]);
        foreach (var scene in project.Scenes.Where(s => s.Edit is not null))
        {
            var edit = scene.Edit!;
            var visual = scene.Kind == SceneKind.Browser ? PathFor($"assets/takes/{scene.Id}/browser.webm")
                : PathFor(project.Assets.Single(a => a.Id == scene.VisualAssetId).Path);
            var clock = await Editing.ProbeClock(tools, visual, ct);
            var count = timeline.Scenes.Single(s => s.Id == scene.Id).FrameCount;
            var map = Editing.Map(edit, clock, count);
            if (edit.AnchorAssetId is { } id)
                Editing.ValidateAnchors(edit, Json.Read<AnchorLedger>(PathFor(project.Assets.Single(a => a.Id == id).Path)), clock, count);
            Json.Write(PathFor($"build/source-maps/{scene.Id}.json"), new
            {
                edit, clock, map,
                disclosure = "Non-destructive authored source selection. AuthoredTailHold flags explicitly repeated final selected frames, NOT native elapsed time. Original take and raw event/wait ledger preserved; no time stretch."
            });
        }
        Json.Write(PathFor("build/timeline.lock.json"), timeline);
        Json.Write(PathFor("build/transitions.json"), Transitions.Maps(project, timeline));
        var captions = Captions.Project(project, timeline);
        Json.Write(PathFor("build/captions.json"), captions);
        Atomic.Write(PathFor("build/captions.srt"), Captions.Render(captions, false));
        Atomic.Write(PathFor("build/captions.vtt"), Captions.Render(captions, true));
        var chapters = Editing.Chapters(project, timeline);
        Json.Write(PathFor("build/chapters.json"), chapters);
        Atomic.Write(PathFor("build/chapters.ffmetadata"), Editing.ChapterMetadata(chapters));
        Json.Write(PathFor("build/descriptions.json"), project.Scenes.Where(s => !string.IsNullOrWhiteSpace(s.VisualDescription))
            .Select(s => new { s.Id, Description = s.VisualDescription, kind = "visual-description-not-spoken-transcript" }).ToArray());
        return timeline;
    }

    private void VerifyFrozenTake(string id)
    {
        var recordPath = PathFor($"build/stages/acquire-{id}.json");
        Guard.Require(File.Exists(recordPath), "take.missing", $"Missing finalized acquisition record: {id}");
        var record = Json.Read<StageRecord>(recordPath);
        Guard.Require(Atomic.IsCurrent(Root, recordPath, record.Fingerprint),
            "take.integrity", $"Acquired take changed or incomplete: {id}");
        if (Load().Scenes.Single(s => s.Id == id).Capture is { } profile)
        {
            using var evidence = JsonDocument.Parse(File.ReadAllText(PathFor($"assets/takes/{id}/native-evidence.json")));
            Guard.Require(evidence.RootElement.TryGetProperty("profile", out var recorded)
                && Json.Text(recorded.Deserialize<JellyfinProfile>(Json.Options)) == Json.Text(profile),
                "take.profile", "Frozen native take belongs to a different capture profile; preserve it and acquire a newly approved scene.");
        }
    }

    public async Task<string> Render(bool preview, CancellationToken ct)
    {
        var timeline = await Resolve(ct);
        var project = Load();
        var workerHash = Planning.WorkerFingerprint(tools);
        var profile = preview ? "preview" : "candidate";
        var toolchain = await Doctor(ct);
        var fingerprint = Files.Fingerprint(new
        {
            timeline.Fingerprint, workerHash, toolchain,
            profile, renderer = Files.Hash(typeof(Production).Assembly.Location)
        });
        var outputRelative = $"output/{profile}/{fingerprint}";
        var output = PathFor(outputRelative);
        var record = PathFor($"build/stages/render-{profile}.json");
        if (Atomic.IsCurrent(Root, record, fingerprint)) return outputRelative;
        Guard.Require(!Directory.Exists(output), "render.immutable", "Candidate directory already exists without a matching valid stage record.");
        var work = PathFor($"build/render/{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var sceneFiles = new List<string>();
        var font = project.Assets.SingleOrDefault(a => a.Id == "font")
            ?? throw new VideoException("font.missing", "Frozen project requires an explicitly licensed font asset named font.");
        var localFont = Path.Combine(work, "font.ttf");
        File.Copy(PathFor(font.Path), localFont);
        var filterFont = Path.GetRelativePath(Root, localFont).Replace('\\', '/');
        foreach (var resolved in timeline.Scenes)
        {
            ct.ThrowIfCancellationRequested();
            var scene = project.Scenes.Single(s => s.Id == resolved.Id);
            var sceneFingerprint = Files.Fingerprint(new
            {
                scene, resolved.FrameCount, workerHash, preview,
                renderer = Files.Hash(typeof(Production).Assembly.Location),
                audio = project.Assets.Single(a => a.Id == resolved.NarrationAssetId).Sha256,
                visual = scene.VisualAssetId is null ? "" : project.Assets.Single(a => a.Id == scene.VisualAssetId).Sha256,
                take = scene.Kind is SceneKind.Browser or SceneKind.Terminal
                    ? Files.Hash(PathFor($"build/stages/acquire-{scene.Id}.json")) : "", font.Sha256
            });
            var intermediateRelative = $"build/scenes/{sceneFingerprint}.mkv";
            var intermediate = PathFor(intermediateRelative);
            var sceneRecord = PathFor($"build/stages/scene-{scene.Id}-{profile}.json");
            if (Atomic.IsCurrent(Root, sceneRecord, sceneFingerprint)) { sceneFiles.Add(intermediate); continue; }
            var sceneDirectory = Path.Combine(work, scene.Id);
            Directory.CreateDirectory(sceneDirectory);
            var input = new List<string>();
            var inputVideo = false;
            if (scene.Kind is SceneKind.Browser or SceneKind.Media)
            {
                var visual = scene.Kind == SceneKind.Browser ? PathFor($"assets/takes/{scene.Id}/browser.webm")
                    : PathFor(project.Assets.Single(a => a.Id == scene.VisualAssetId).Path);
                var probe = await Media.Probe(tools.Ffprobe, visual, ct);
                inputVideo = probe.Frames > 1;
                if (probe.Frames <= 1) input.AddRange(["-loop", "1", "-framerate", "30"]);
                input.AddRange(["-i", visual]);
                if (scene.Edit is { } edit)
                {
                    var clock = await Editing.ProbeClock(tools, visual, ct);
                    var map = Editing.Map(edit, clock, resolved.FrameCount);
                    var rawDirectory = Path.Combine(sceneDirectory, "source-frames");
                    var framesDirectory = Path.Combine(sceneDirectory, "edited-frames");
                    Directory.CreateDirectory(rawDirectory);
                    await Ffmpeg(["-y", "-i", visual, "-vf",
                        $"trim=start_frame={edit.StartFrame}:end_frame={edit.EndFrame},setpts=PTS-STARTPTS",
                        "-fps_mode", "passthrough", "-start_number", "0", Path.Combine(rawDirectory, "%06d.png")], ct);
                    var ledger = edit.AnchorAssetId is { } id
                        ? Json.Read<AnchorLedger>(PathFor(project.Assets.Single(a => a.Id == id).Path))
                        : new AnchorLedger(1, clock.SourceSha256, clock.Fps, clock.Width, clock.Height, [], "no spatial annotations");
                    Editing.ValidateAnchors(edit, ledger, clock, resolved.FrameCount);
                    var config = Path.Combine(sceneDirectory, "footage.json");
                    Json.Write(config, new
                    {
                        edit, ledger, map, inputDirectory = rawDirectory, outputDirectory = framesDirectory,
                        fontPath = PathFor(font.Path), fontSha256 = font.Sha256, preview
                    });
                    await Processes.Run(tools.Node, [Planning.Worker(tools), "footage", config], Root, ct, 1800);
                    input.Clear();
                    input.AddRange(["-framerate", "30", "-start_number", "0", "-i", Path.Combine(framesDirectory, "%06d.png")]);
                    inputVideo = false;
                }
            }
            else
            {
                if (scene.Kind == SceneKind.Terminal)
                    scene = scene with { Text = Json.Read<ProcessResult>(PathFor($"assets/takes/{scene.Id}/process.json")).Stdout };
                var framesDirectory = Path.Combine(sceneDirectory, "frames");
                var config = Path.Combine(sceneDirectory, "frames.json");
                Json.Write(config, new
                {
                    scene, frames = resolved.FrameCount, outputDirectory = framesDirectory,
                    fontPath = PathFor(font.Path), fontSha256 = font.Sha256, preview
                });
                await Processes.Run(tools.Node, [Planning.Worker(tools), "frames", config], Root, ct, 600);
                input.AddRange(["-framerate", "30", "-start_number", "0", "-i", Path.Combine(framesDirectory, "%06d.png")]);
            }
            var audio = PathFor(project.Assets.Single(a => a.Id == resolved.NarrationAssetId).Path);
            input.AddRange(["-i", audio]);
            var inputMatrix = scene.Kind == SceneKind.Browser && scene.Edit is null ? "bt601" : "bt709";
            var videoFilter = "setpts=PTS-STARTPTS,fps=30,scale=1920:1080:force_original_aspect_ratio=decrease"
                + $":in_color_matrix={inputMatrix}:out_color_matrix=bt709:in_range={(inputVideo ? "tv" : "pc")}:out_range=tv"
                + ",pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1";
            if (scene.AllowHold) videoFilter += $",tpad=stop_mode=clone:stop_duration={Media.Number((double)resolved.FrameCount / 30)}";
            videoFilter += $",trim=end_frame={resolved.FrameCount},setpts=N/(30*TB),format=yuv420p";
            if (!inputVideo || scene.Kind == SceneKind.Browser)
                videoFilter += ",colorspace=iall=bt709:itrc=iec61966-2-1:all=bt709:irange=tv:range=tv";
            if (preview && scene.Kind is SceneKind.Browser or SceneKind.Media)
                videoFilter += ",drawbox=x=0:y=0:w=iw:h=45:color=yellow@0.8:t=fill"
                    + $",drawtext=fontfile={filterFont}:text='REVIEW-ONLY PREVIEW - NOT RELEASE':fontcolor=black:fontsize=28:x=20:y=6";
            var temp = Path.Combine(sceneDirectory, "scene.mp4");
            await Ffmpeg(["-y", .. input, "-map", "0:v:0", "-map", "1:a:0", "-vf", videoFilter,
                "-af", $"atrim=start_sample={resolved.AudioStartSample}:end_sample={resolved.AudioStartSample + resolved.AudioSamples},asetpts=N/SR/TB,aresample=48000,apad=whole_len={resolved.DeliverySamples},atrim=end_sample={resolved.DeliverySamples},asetpts=N/SR/TB",
                "-frames:v", resolved.FrameCount.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "veryfast",
                "-crf", "0", "-pix_fmt", "yuv420p", "-color_range", "tv", "-colorspace", "bt709", "-color_trc", "bt709",
                "-color_primaries", "bt709", "-c:a", "pcm_s16le", "-f", "matroska", temp], ct);
            var probeResult = await Media.Probe(tools.Ffprobe, temp, ct);
            Guard.Require(probeResult.Frames == resolved.FrameCount, "render.frames", "Intermediate frame count mismatch.");
            Directory.CreateDirectory(Path.GetDirectoryName(intermediate)!);
            File.Move(temp, intermediate, overwrite: true);
            Atomic.Commit(Root, sceneRecord, sceneFingerprint, [intermediateRelative]);
            sceneFiles.Add(intermediate);
        }
        var joined = Path.Combine(work, "joined.mkv");
        var segments = await InsertTransitions(project, timeline, sceneFiles, work, ct);
        var joinInputs = segments.Files.SelectMany(p => new[] { "-i", p }).ToArray();
        var joinGraph = string.Join(';', Enumerable.Range(0, segments.Files.Length).Select(i =>
            $"[{i}:v]settb=1/30,setpts=N[v{i}];[{i}:a]{segments.AudioFilters[i]}[a{i}]"))
            + ";" + string.Concat(Enumerable.Range(0, segments.Files.Length).Select(i => $"[v{i}][a{i}]"))
            + $"concat=n={segments.Files.Length}:v=1:a=1[v][a]";
        await Ffmpeg(["-y", .. joinInputs, "-filter_complex", joinGraph, "-map", "[v]", "-map", "[a]",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "0", "-c:a", "pcm_s16le", joined], ct);
        var loudness = await Media.Loudness(tools, joined, ct);
        var measure = loudness.Measurement;
        var norm = $"loudnorm=I=-16:TP=-1.5:LRA=11:measured_I={measure.GetProperty("input_i").GetString()}"
            + $":measured_TP={measure.GetProperty("input_tp").GetString()}:measured_LRA={measure.GetProperty("input_lra").GetString()}"
            + $":measured_thresh={measure.GetProperty("input_thresh").GetString()}:offset={measure.GetProperty("target_offset").GetString()}:linear=true";
        var final = Path.Combine(work, "movie.mp4");
        await Ffmpeg(["-y", "-i", joined, "-f", "ffmetadata", "-i", PathFor("build/chapters.ffmetadata"),
            "-map", "0:v:0", "-map", "0:a:0", "-map_metadata", "1", "-map_chapters", "1",
            "-vf", "settb=1/30,setpts=N", "-r", "30", "-fps_mode", "cfr", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
            "-pix_fmt", "yuv420p", "-color_range", "tv", "-colorspace", "bt709", "-color_trc", "bt709", "-color_primaries", "bt709",
            "-af", norm + ",aresample=48000,asetpts=N/SR/TB", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", final], ct);
        var check = await CheckFile(final, timeline, ct);
        var delivery = Path.Combine(work, "delivery");
        Directory.CreateDirectory(delivery);
        File.Move(final, Path.Combine(delivery, "movie.mp4"));
        File.Copy(PathFor("build/captions.srt"), Path.Combine(delivery, "captions.srt"));
        File.Copy(PathFor("build/captions.vtt"), Path.Combine(delivery, "captions.vtt"));
        foreach (var name in new[] { "chapters.json", "descriptions.json", "transitions.json" })
            File.Copy(PathFor("build/" + name), Path.Combine(delivery, name));
        if (Directory.Exists(PathFor("build/source-maps")))
        {
            Directory.CreateDirectory(Path.Combine(delivery, "source-maps"));
            foreach (var sourceMap in Directory.GetFiles(PathFor("build/source-maps")))
                File.Copy(sourceMap, Path.Combine(delivery, "source-maps", Path.GetFileName(sourceMap)));
        }
        Json.Write(Path.Combine(delivery, "checks.json"), check);
        Json.Write(Path.Combine(delivery, "toolchain.lock.json"), toolchain);
        Json.Write(Path.Combine(delivery, "review.json"), new Review(1, Files.Hash(Path.Combine(delivery, "movie.mp4")),
            "", false, false, false, false, false, false, false));
        Json.Write(Path.Combine(delivery, "provenance.json"), new
        {
            timeline, projectHash = Files.Hash(ProjectFile),
            profile, captionEvidence = Captions.Project(project, timeline), machineChecksOnly = true,
            nativeBrowserCadence = "25fps; duration-preserving repeated-frame conversion to 30fps; no unique-30fps claim"
        });
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Directory.Move(delivery, output);
        var outputs = Directory.GetFiles(output, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p) != "review.json")
            .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/')).ToArray();
        Atomic.Commit(Root, record, fingerprint, outputs);
        Directory.Delete(work, recursive: true);
        return outputRelative;
    }

    private Task<ProcessResult> Ffmpeg(string[] args, CancellationToken ct)
    {
        var safe = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "warning" };
        foreach (var arg in args)
        {
            if (arg == "-i") safe.AddRange(["-protocol_whitelist", "file,pipe"]);
            safe.Add(arg);
        }
        return Processes.Run(tools.Ffmpeg, safe, Root, ct, 600);
    }

    public async Task<object> Doctor(CancellationToken ct)
    {
        var browser = await Processes.Run(tools.Node, [Planning.Worker(tools), "info"], Root, ct, 60);
        using var browserInfo = JsonDocument.Parse(browser.Stdout);
        var ffmpeg = await Processes.Run(tools.Ffmpeg, ["-version"], Root, ct);
        var node = await Processes.Run(tools.Node, ["--version"], Root, ct);
        return new
        {
            platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            dotnet = Environment.Version.ToString(), node = node.Stdout.Trim(), nodeSha256 = Files.Hash(tools.Node),
            ffmpeg = ffmpeg.Stdout, ffmpegSha256 = Files.Hash(tools.Ffmpeg), ffprobeSha256 = Files.Hash(tools.Ffprobe),
            browser = browserInfo.RootElement.Clone(), workerSha256 = Planning.WorkerFingerprint(tools),
            isolation = "trusted synthetic fixtures or typed reviewed loopback Jellyfin only; process/worktree is NOT a sandbox; untrusted acquisition refused",
            networking = "capture scoped approved loopback APIs/pinned web assets; render routes denied; no uploads",
            kokoroConfigured = tools.Kokoro is not null,
            speechConfigured = tools.Speech is not null,
            transcription = "Explicit separate transcribe config; Windows x64 fixed-English Whisper Base only. Doctor never provisions or invokes models. Unsupported platforms fail preflight."
        };
    }

    public async Task<object> CheckFile(string movie, Timeline timeline, CancellationToken ct)
    {
        var probe = await Media.Probe(tools.Ffprobe, movie, ct);
        Guard.Require(Media.HasFastStart(movie) && probe.Codec == "h264" && probe.Width == 1920 && probe.Height == 1080
            && probe.PixelFormat == "yuv420p" && probe.FrameRate == "30/1" && probe.Frames == timeline.TotalFrames
            && probe.AudioCodec == "aac" && probe.AudioRate == 48000 && probe.AudioChannels == 1
            && Math.Abs(probe.Duration - (double)timeline.TotalFrames / 30) <= 1.0 / 30 + 1024.0 / 48000
            && Math.Abs(probe.StartTime) < 0.001 && probe.ColorSpace == "bt709"
            && probe.ColorTransfer == "bt709" && probe.ColorPrimaries == "bt709",
            "delivery.profile", "Final encoded streams/clock/color do not match required delivery profile.");
        await Ffmpeg(["-v", "error", "-xerror", "-i", movie, "-f", "null", "-"], ct);
        var framesResult = await Processes.Run(tools.Ffprobe,
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_frames", "-show_entries", "frame=media_type,best_effort_timestamp_time,nb_samples", "-of", "json", movie],
            Root, ct, 300, 20_000_000);
        using var decoded = JsonDocument.Parse(framesResult.Stdout);
        long videoIndex = 0, audioSamples = 0;
        foreach (var frame in decoded.RootElement.GetProperty("frames").EnumerateArray())
        {
            if (frame.GetProperty("media_type").GetString() == "video")
            {
                var pts = double.Parse(frame.GetProperty("best_effort_timestamp_time").GetString()!, CultureInfo.InvariantCulture);
                Guard.Require(Math.Abs(pts - (double)videoIndex / 30) < 0.0001, "delivery.pts", "Final decoded video PTS is not contiguous normalized 30fps.");
                videoIndex++;
            }
            else audioSamples += frame.GetProperty("nb_samples").GetInt64();
        }
        var expectedSamples = timeline.Fps.Samples(timeline.TotalFrames, 48000, Rounding.Nearest);
        Guard.Require(videoIndex == timeline.TotalFrames && audioSamples >= expectedSamples && audioSamples <= expectedSamples + 1024,
            "delivery.samples", "Decoded audio length must cover the exact timeline within one documented AAC frame of padding.");
        if (Load().DeliveryCaptions is { } binding)
            Guard.Require(binding.DecoderFfmpegSha256 == Files.Hash(tools.Ffmpeg)
                && audioSamples == binding.Narration.SampleCount && await DecodedPcmHash(movie, ct) == binding.DecodedPcmSha256,
                "delivery-transcript.audio", "Encoded delivery decode differs from bound STT wholemaster clock/PCM; re-transcribe/rebind, never shift frozen captions.");
        var expectedChapters = Editing.Chapters(Load(), timeline);
        var chapterResult = await Processes.Run(tools.Ffprobe, ["-v", "error", "-show_chapters", "-of", "json", movie], Root, ct);
        using var chapterJson = JsonDocument.Parse(chapterResult.Stdout);
        var actualChapters = chapterJson.RootElement.GetProperty("chapters").EnumerateArray().ToArray();
        Guard.Require(actualChapters.Length == expectedChapters.Length, "delivery.chapters", "Encoded chapter count differs from authored chapters.");
        for (var i = 0; i < expectedChapters.Length; i++)
        {
            var chapter = actualChapters[i];
            Guard.Require(Math.Abs(double.Parse(chapter.GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture)
                - expectedChapters[i].StartFrame / 30.0) < 0.0011
                && Math.Abs(double.Parse(chapter.GetProperty("end_time").GetString()!, CultureInfo.InvariantCulture)
                - expectedChapters[i].EndFrame / 30.0) < 0.0011
                && chapter.GetProperty("tags").GetProperty("title").GetString() == expectedChapters[i].Title,
                "delivery.chapters", "Encoded chapter timing/title differs from authored frame times.");
        }
        var loudness = await Media.Loudness(tools, movie, ct);
        Guard.Require(Math.Abs(loudness.Integrated + 16) <= 1 && loudness.Peak <= -1,
            "delivery.loudness", $"Delivery measured {loudness.Integrated}LUFS, {loudness.Peak}dBTP; target -16+/-1 and <=-1.");
        var diagnostics = await Processes.Run(tools.Ffmpeg,
            ["-hide_banner", "-nostdin", "-protocol_whitelist", "file,pipe", "-i", movie, "-vf", "blackdetect=d=0.2:pix_th=0.1,freezedetect=n=-50dB:d=2",
                "-af", "silencedetect=n=-45dB:d=0.5", "-f", "null", "-"], Root, ct, 600);
        return new
        {
            ok = true, movieSha256 = Files.Hash(movie), probe,
            loudness = new { loudness.Integrated, loudness.Peak }, deliveryDecoded = true,
            decodedAudioSamples = audioSamples, expectedAudioSamples = expectedSamples, aacPaddingAllowanceSamples = 1024,
            fastStart = true,
            chaptersVerified = expectedChapters.Length,
            diagnostics = diagnostics.Stderr.Split('\n').Where(l => l.Contains("black_", StringComparison.Ordinal)
                || l.Contains("freeze_", StringComparison.Ordinal) || l.Contains("silence_", StringComparison.Ordinal)).ToArray(),
            policy = "Static title/code/diagram and authored holds intentionally freeze; detectors are evidence, not automatic deletion.",
            humanReviewRequired = true
        };
    }

    public string Release(string candidateRelative, string reviewFile)
    {
        Validation.Validate(Load(), Root);
        Guard.Require(candidateRelative.StartsWith("output/candidate/", StringComparison.Ordinal),
            "release.preview", "Only a machine-checked candidate may be reviewed; preview is never a release.");
        var candidate = PathFor(candidateRelative);
        var movie = Path.Combine(candidate, "movie.mp4");
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(candidate, "provenance.json")));
        Guard.Require(provenance.RootElement.GetProperty("projectHash").GetString() == Files.Hash(ProjectFile),
            "release.stale", "Project changed since this exact candidate was built.");
        var review = Json.Read<Review>(reviewFile);
        Guard.Require(review.SchemaVersion == 1 && review.MovieSha256 == Files.Hash(movie)
            && !string.IsNullOrWhiteSpace(review.Reviewer) && review.Claims && review.Voice && review.Pace
            && review.Privacy && review.Captions && review.Attribution && review.ViewingSize,
            "release.review", "Exact movie hash and all seven human review checks must be approved by a named reviewer.");
        var stage = Json.Read<StageRecord>(PathFor("build/stages/render-candidate.json"));
        Guard.Require(Atomic.IsCurrent(Root, PathFor("build/stages/render-candidate.json"), stage.Fingerprint)
            && stage.Outputs.Any(o => o.Path == candidateRelative + "/movie.mp4"),
            "release.stale", "Candidate or its machine-check artifacts changed; rebuild and review.");
        var relative = $"output/release/{review.MovieSha256}";
        var output = PathFor(relative);
        Guard.Require(!Directory.Exists(output), "release.immutable", "Release already exists.");
        Directory.CreateDirectory(output);
        foreach (var file in Directory.GetFiles(candidate, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(output, Path.GetRelativePath(candidate, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        Json.Write(Path.Combine(output, "accepted-review.json"), review);
        return relative;
    }

    public string Export(string destination)
    {
        var project = Load();
        Validation.Validate(project, Root);
        foreach (var scene in project.Scenes.Where(s => s.Kind is SceneKind.Browser or SceneKind.Terminal)) VerifyFrozenTake(scene.Id);
        var timeline = Json.Read<Timeline>(PathFor("build/timeline.lock.json"));
        Guard.Require(timeline.ProjectSha256 == Files.Hash(ProjectFile), "export.stale", "Resolve changed project before export.");
        Guard.Require(!File.Exists(destination) && !Directory.Exists(destination), "export.exists", "Bundle destination must be new.");
        var stage = PathFor($"build/export/{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        void Copy(string source, string relative)
        {
            var target = Path.Combine(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
                Guard.Require(Files.Hash(target) == Files.Hash(source), "export.duplicate", "Conflicting bytes share a bundle path.");
            else File.Copy(source, target);
        }
        Copy(ProjectFile, Path.Combine("project", "project.json"));
        ExportPrivacy.CheckJson(File.ReadAllText(ProjectFile));
        foreach (var asset in project.Assets)
        {
            ExportPrivacy.CheckAsset(PathFor(asset.Path));
            Copy(PathFor(asset.Path), Path.Combine("project", asset.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (asset.Id == "font")
            {
                var license = Path.Combine(Path.GetDirectoryName(PathFor(asset.Path))!, "OFL.txt");
                Guard.Require(File.Exists(license), "export.font-license", "Frozen font license OFL.txt must accompany font asset.");
                Copy(license, Path.Combine("project", Path.GetDirectoryName(asset.Path)!, "OFL.txt"));
            }
        }
        foreach (var scene in project.Scenes.Where(s => s.Kind is SceneKind.Browser or SceneKind.Terminal))
        {
            var relative = $"assets/takes/{scene.Id}";
            var directory = PathFor(relative);
            ExportPrivacy.CheckTake(directory);
            foreach (var file in Directory.GetFiles(directory))
                Copy(file, Path.Combine("project", relative.Replace('/', Path.DirectorySeparatorChar), Path.GetFileName(file)));
            var record = $"build/stages/acquire-{scene.Id}.json";
            Copy(PathFor(record), Path.Combine("project", record.Replace('/', Path.DirectorySeparatorChar)));
        }
        Copy(PathFor("build/timeline.lock.json"), Path.Combine("project", "build", "timeline.lock.json"));
        foreach (var relative in new[] { "src", "workers", "schemas", "docs", "tests", "tools", "skills" })
        {
            var source = Path.Combine(tools.SourceRoot, relative);
            if (!Directory.Exists(source)) continue;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var parts = Path.GetRelativePath(source, file).Split(Path.DirectorySeparatorChar);
                if (parts.Contains("bin") || parts.Contains("obj")) continue;
                Guard.Require(!File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)
                    && !new[] { ".exe", ".dll", ".onnx", ".so", ".dylib", ".har" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                    && !new[] { ".npmrc", ".env", "storageState.json", "auth.json" }.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase),
                    "export.runtime-private", "Native/private dependency file in renderer source tree cannot be bundled.");
                Copy(file, Path.Combine("renderer", relative, Path.GetRelativePath(source, file)));
            }
        }
        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "LICENSE", "README.md", "SOURCE-ORIGINS.md",
            "THIRD-PARTY-NOTICES.md", "SMART_TOOL.md", "smart-tool.json", "NuGet.Config", "MediaStudio.slnx",
            ".github\\skills\\media-explanation\\SKILL.md", ".gitattributes" })
            Copy(Path.Combine(tools.SourceRoot, name), Path.Combine("renderer", name));
        Atomic.Write(Path.Combine(stage, "RERENDER.txt"),
            "Offline source bundle v1\nPrerequisites: pinned .NET11 SDK from renderer/global.json, Node24, FFmpeg+ffprobe with libx264/AAC, preinstalled Playwright1.61 Chromium revision1228.\n"
            + "Build restores the locked nuget.org packages pinned in renderer/Directory.Packages.props and packages.lock.json (dotnet restore --locked-mode); no other acquisition is authorized.\n"
            + "This is a production-CLI/frozen-project export, not the separate full native Media Studio source delivery.\n"
            + "Worker JS dependencies and font are included. Native runtimes/browser/FFmpeg/eSpeak/model weights are NOT bundled.\n"
            + "Run from extracted root:\n"
            + "dotnet run --project renderer\\src\\VideoProduction.Cli -- render --project project\\project.json --source renderer\n"
            + "No Kokoro/ASR/model/network/reacquisition is needed to rerender frozen takes. No byte-identical cross-toolchain promise.\n"
            + "review.json is an unapproved human checklist; this is NOT a human-accepted release.\n");
        var inventory = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Select(p =>
            new ArtifactHash(Path.GetRelativePath(stage, p).Replace('\\', '/'), Files.Hash(p))).OrderBy(x => x.Path).ToArray();
        Json.Write(Path.Combine(stage, "bundle-manifest.json"), new { schemaVersion = 1, project.Id, files = inventory });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        ZipFile.CreateFromDirectory(stage, destination, CompressionLevel.Fastest, includeBaseDirectory: false);
        Directory.Delete(stage, recursive: true);
        return destination;
    }

    public static string WrapCaption(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string> { "" };
        foreach (var word in words)
        {
            if (lines[^1].Length > 0 && lines[^1].Length + word.Length + 1 > 42) lines.Add("");
            lines[^1] += (lines[^1].Length == 0 ? "" : " ") + word;
        }
        return string.Join("\n", lines);
    }
}
