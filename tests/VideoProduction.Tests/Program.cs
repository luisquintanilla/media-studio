using System.Numerics;
using System.Text.Json;
using VideoProduction;

var count = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    count++;
}
void Reject(Action action, string code)
{
    try { action(); throw new Exception("Expected rejection: " + code); }
    catch (VideoException ex) { Check(ex.Code == code, $"Expected {code}, got {ex.Code}"); }
}
var root = Path.Combine(Path.GetTempPath(), "video-production-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    count += EditingTests.Run();
    count += TransitionTests.Run();
    count += await SpeechTests.Run(root);
    count += await ScriptCaptionTests.Run(root);
    count += TranscriptionTests.ContractAndDiscrepancy();
    count += TranscriptionTests.DigitalSilenceEvidence(root);
    count += DeliveryCaptionTests.Run();
    var fps = new Rate(30, 1);
    Check(fps.Samples(1, 48000, Rounding.Nearest) == 1600, "30fps exact sample clock");
    Check(fps.Frames(1601, 48000, Rounding.Ceiling) == 2, "ceiling retains complete speech");
    Check(fps.Frames(1601, 48000, Rounding.Floor) == 1, "explicit floor");
    var imported = new MediaInfo("h264", 1920, 1080, "yuv420p", "30/1", 667, 22.233333,
        "", 0, 0, 0, "bt709", "bt709", "bt709");
    Check(Media.VideoFrames(imported, fps) == 667, "counted video clock survives six-decimal container duration");
    Check(Media.VideoFrames(imported with { Frames = 666 }, fps) == 666,
        "container duration cannot grant a missing video frame");
    Check(Media.VideoFrames(imported with { FrameRate = "25/1", Frames = 875, Duration = 40 }, fps) == 1050,
        "video rate conversion ignores a longer container/audio duration");
    Check(Media.VideoFrames(imported with { FrameRate = "30000/1001", Frames = 1000 }, fps) == 1001,
        "fractional imported rate conversion uses an exact rational clock");
    var fractional = new Rate(30000, 1001);
    const long frames = 30_000_000;
    Check(fractional.Samples(frames, 48000, Rounding.Floor) == 48_048_000_000, "long fractional clock has no drift");
    for (long i = 0; i < 1000; i++)
    {
        var sample = fractional.Samples(i * 999_983, 48000, Rounding.Ceiling);
        var exact = (BigInteger)(i * 999_983) * 1001 * 48000;
        Check((BigInteger)sample * 30000 >= exact && (BigInteger)sample * 30000 - exact < 30000, "fractional rounding bound");
    }
    Reject(() => new Rate(0, 1).Validate(), "clock.rate");
    Reject(() => fps.Samples(-1, 48000, Rounding.Floor), "clock.range");
    Reject(() => new Span64(2, 2).Validate(), "clock.span");
    new Span64(0, 1).Validate();
    Check(true, "half-open single frame valid");
    foreach (var path in new[] { "../x", "/x", "C:/x", "assets\\x", "a//b", "a/./b", "a/../b", "a/CON.txt", "a/nul", "a/x.", "a/x ", "a/<x>" })
        Reject(() => Files.Under(root, path), path.Contains('\\') || path.StartsWith('/') || path.Contains(':') ? "path.relative" : "path.unsafe");
    Check(Files.Under(root, "assets/x.txt") == Path.Combine(root, "assets", "x.txt"), "portable relative path");
    Check(!Files.ValidId("Bad") && Files.ValidId("voice-code-1") && !Files.ValidHash(new string('z', 64)), "ID and hash shape");
    Atomic.Write(Path.Combine(root, "asset.txt"), "one");
    var asset = new Asset("audio", "asset.txt", Files.Hash(Path.Combine(root, "asset.txt")), "synthetic", "MIT");
    Files.VerifyAsset(root, asset);
    Atomic.Write(Path.Combine(root, "asset.txt"), "two");
    Reject(() => Files.VerifyAsset(root, asset), "asset.hash");
    Atomic.Write(Path.Combine(root, "asset.txt"), "one");
    var cue = new Cue(0, 48000 * 2, "Short words.", Evidence.TakeWindow);
    Captions.ValidateCue(cue, 48000);
    Reject(() => Captions.ValidateCue(cue with { Text = new string('a', 43) }, 48000), "caption.policy");
    Reject(() => Captions.ValidateCue(cue with { Text = "a\nb\nc" }, 48000), "caption.policy");
    Reject(() => Captions.ValidateCue(cue with { EndSample = 47999 }, 48000), "caption.policy");
    Reject(() => Captions.ValidateCue(cue with { EndSample = 7 * 48000 }, 48000), "caption.policy");
    Reject(() => Captions.ValidateCue(cue with { Text = "--> forged" }, 48000), "caption.text");
    Captions.ValidateCue(cue with { EndSample = 7 * 48000, PolicyException = "Human segmentation required" }, 48000);
    Check(true, "explicit policy exceptions accepted without claiming timing");
    var narration = new Narration("audio", 48000, 96000, "Short words.", [cue]);
    var scene = new Scene("title", SceneKind.Title, "Title", "Text", "Short words.",
        TimingMaster.Narration, 0, false, null, narration, []);
    var project = new Project(1, "test-project", 1920, 1080, fps, [asset], [scene]);
    Validation.Validate(project, root);
    var timeline = Validation.Resolve(project, "hash", _ => null);
    Check(timeline.TotalFrames == 60 && timeline.Scenes[0].DeliverySamples == 96000, "narration-led exact duration");
    Reject(() => Validation.Resolve(project with { Scenes = [scene with { Timing = TimingMaster.Fixed, FixedFrames = 59 }] }, "hash", _ => null), "timing.overrun");
    Reject(() => Validation.Resolve(project, "hash", _ => 59), "timing.underlength");
    Check(Validation.Resolve(project with { Scenes = [scene with { AllowHold = true }] }, "hash", _ => 59).TotalFrames == 60, "explicit hold permits short visual");
    Reject(() => Validation.Resolve(project with { Scenes = [scene with { Timing = TimingMaster.Action }] }, "hash", _ => null), "timing.action");
    Reject(() => Validation.Resolve(project with { Scenes = [scene with { Effects = [new Effect(EffectKind.Highlight, 0, 61)] }] }, "hash", _ => null), "effect.overrun");
    Reject(() => Validation.Validate(project with { Scenes = [scene, scene] }, root), "scene.duplicate");
    Reject(() => Validation.Validate(project with { Assets = [asset, asset with { Id = "other" }] }, root), "asset.path-collision");
    Reject(() => Validation.Validate(project with { Scenes = [scene with { Narration = narration with { AssetId = "missing" } }] }, root), "narration.asset");
    Reject(() => Validation.Validate(project with { Scenes = [scene with { Narration = narration with { Cues = [cue, cue] } }] }, root), "caption.range");
    var reordered = project with { Scenes = [scene with { Id = "second", Timing = TimingMaster.Fixed, FixedFrames = 90 }, scene] };
    var reorderedTimeline = Validation.Resolve(reordered, "hash", _ => null);
    var captions = Captions.Project(reordered, reorderedTimeline);
    Check(captions[0].StartMilliseconds == 0 && captions[0].EndMilliseconds == 2000
        && captions[1].StartMilliseconds == 3000 && captions[1].EndMilliseconds == 5000,
        "caption projection accounts for reorder and authored pause");
    Check(Captions.Render(captions, false).Contains("00:00:03,000 --> 00:00:05,000", StringComparison.Ordinal), "SRT clock shape");
    Check(Captions.Render(captions, true).StartsWith("WEBVTT\n\n", StringComparison.Ordinal), "VTT shape");
    var escaped = Captions.Render([new ProjectedCaption(0, 1000, "<i>&", Evidence.TakeWindow, null)], true);
    Check(escaped.Contains("&lt;i&gt;&amp;", StringComparison.Ordinal), "caption injection escaped");
    var plan = new ActionPlan(1, "project", "inputs", "worker", [], "fingerprint");
    var approval = new Approval(1, "fingerprint", "unit-test", "trusted-bundled-fixture-only");
    Planning.Authorize(plan, plan, approval);
    Reject(() => Planning.Authorize(plan, plan, approval with { Fingerprint = "other" }), "approval.missing");
    Reject(() => Planning.Authorize(plan, plan with { WorkerSha256 = "changed" }, approval), "approval.changed");
    Reject(() => Planning.Authorize(plan, plan, approval with { ApprovedBy = "" }), "approval.missing");
    var untrusted = plan with { Actions = [new ActionSpec("bad", "shell", "cmd", "hash", [], root, [], [], [], "untrusted", 1, 1)] };
    Reject(() => Planning.Authorize(untrusted, untrusted, approval), "isolation.unavailable");
    var allowedAction = untrusted.Actions[0] with { Kind = "bundled-sum", Trust = "trusted-bundled-fixture-only" };
    var approvedActionPlan = plan with { Actions = [allowedAction] };
    foreach (var changed in new[]
    {
        allowedAction with { Argv = ["changed"] },
        allowedAction with { Executable = "other" },
        allowedAction with { ExecutableSha256 = "other-hash" },
        allowedAction with { Workspace = "different" },
        allowedAction with { ReadFiles = ["extra"] },
        allowedAction with { WriteFiles = ["extra"] },
        allowedAction with { Origins = ["https://unapproved.invalid"] },
        allowedAction with { TimeoutSeconds = 200 },
        allowedAction with { MaxOutputBytes = 200 }
    })
        Reject(() => Planning.Authorize(approvedActionPlan, approvedActionPlan with { Actions = [changed] }, approval), "approval.changed");
    var shared = new Narration("audio", 24000, 96000, "Shared boundaries.",
        [new Cue(0, 48001, "One.", Evidence.AsrSegment), new Cue(48001, 96000, "Two.", Evidence.AsrSegment)]);
    var sharedProject = project with { Scenes = [scene with { Narration = shared }] };
    var sharedCues = Captions.Project(sharedProject, Validation.Resolve(sharedProject, "hash", _ => null));
    Check(sharedCues[0].EndMilliseconds == sharedCues[1].StartMilliseconds, "adjacent fractional caption boundaries never overlap");
    var manifest = SelfDescription.Manifest();
    Check(manifest.Manifest.Name == "video-production" && manifest.Manifest.Version == "0.1.0"
        && manifest.Manifest.Platforms.SequenceEqual(["windows"]) && manifest.Body.Contains("typed .NET library", StringComparison.Ordinal),
        "embedded canonical manifest contract");
    var stage = Path.Combine(root, "stage.json");
    Check(!Atomic.IsCurrent(root, stage, "one"), "missing stage is not success");
    Atomic.Commit(root, stage, "one", ["asset.txt"]);
    Check(Atomic.IsCurrent(root, stage, "one"), "committed stage resumes");
    Check(!Atomic.IsCurrent(root, stage, "two"), "dependency change invalidates stage");
    Atomic.Write(Path.Combine(root, "asset.txt"), "changed");
    Check(!Atomic.IsCurrent(root, stage, "one"), "output tamper invalidates stage");
    try { JsonSerializer.Deserialize<Project>("{}", Json.Options); throw new Exception("Missing JSON fields accepted"); }
    catch (JsonException) { Check(true, "required JSON fields"); }
    try { JsonSerializer.Deserialize<Rate>("""{"numerator":30,"denominator":1,"filtergraph":"unsafe"}""", Json.Options); throw new Exception("Unknown JSON accepted"); }
    catch (JsonException) { Check(true, "unknown JSON rejected"); }
    Atomic.Write(Path.Combine(root, "bad.wav"), "not wav");
    Reject(() => Wav.Inspect(Path.Combine(root, "bad.wav")), "wav.header");

    count += await ProcessRegressionTests.Run(root);
    var node = Files.FindExecutable("node");
    var oldNodeOptions = Environment.GetEnvironmentVariable("NODE_OPTIONS");
    try
    {
        Environment.SetEnvironmentVariable("NODE_OPTIONS", "--invalid-preload-option");
        var clean = await Processes.Run(node, ["-e", "console.log('clean')"], root, CancellationToken.None);
        Check(clean.Stdout.Trim() == "clean", "child strips inherited code-injection environment");
    }
    finally { Environment.SetEnvironmentVariable("NODE_OPTIONS", oldNodeOptions); }
    var output = await Processes.Run(node, ["-e", "console.log('real-out');console.error('real-err');process.exitCode=7"], root,
        CancellationToken.None, requireSuccess: false);
    Check(output.ExitCode == 7 && output.Stdout.Contains("real-out", StringComparison.Ordinal)
        && output.Stderr.Contains("real-err", StringComparison.Ordinal), "genuine stdout stderr exit");
    Check(output.Events.Length >= 2 && output.Events.Select(x => x.ElapsedTicks).SequenceEqual(output.Events.Select(x => x.ElapsedTicks).Order()),
        "monotonic event ordering");
    using (var cancellation = new CancellationTokenSource())
    {
        var ownedPids = Path.Combine(root, "owned-processes.json");
        var running = Processes.Run(node, ["-e", """
            const fs = require('node:fs');
            const child = require('node:child_process').spawn(process.execPath, ['-e', 'setInterval(()=>{},1000)'], {stdio:'ignore'});
            fs.writeFileSync('owned-processes.tmp', JSON.stringify([process.pid, child.pid]));
            fs.renameSync('owned-processes.tmp', 'owned-processes.json');
            setInterval(()=>{},1000);
            """], root, cancellation.Token);
        try
        {
            var readyBy = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!File.Exists(ownedPids) && !running.IsCompleted && DateTimeOffset.UtcNow < readyBy)
                await Task.Delay(20);
            Check(File.Exists(ownedPids), "actual owned process and descendant record their PIDs before cancellation");
            var pids = Json.Read<int[]>(ownedPids);
            using var owner = System.Diagnostics.Process.GetProcessById(pids[0]);
            using var descendant = System.Diagnostics.Process.GetProcessById(pids[1]);
            Check(!owner.HasExited && !descendant.HasExited, "both actual owned processes are running before cancellation");
            cancellation.Cancel();
            try { await running; throw new Exception("Cancellation did not interrupt process"); }
            catch (OperationCanceledException) { Check(true, "cancellation interrupts the actual process runner"); }
            Check(owner.HasExited && descendant.HasExited, "runner cancellation cannot return while either owned process remains alive");
            using var exitedBy = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.WhenAll(owner.WaitForExitAsync(exitedBy.Token), descendant.WaitForExitAsync(exitedBy.Token));
            Check(owner.HasExited && descendant.HasExited, "cancellation reaps the actual owned process and descendant");
        }
        finally
        {
            cancellation.Cancel();
            try { await running; }
            catch (OperationCanceledException) { }
        }
    }
    try
    {
        await Processes.Run(node, ["-e", "console.log('x'.repeat(10000))"], root, CancellationToken.None, maxOutputBytes: 100);
        throw new Exception("Output bound not enforced");
    }
    catch (VideoException ex) { Check(ex.Code == "process.output-limit", "output limit"); }
    Console.WriteLine($"PASS {count} assertions; exact clocks, contracts, captions, approval, resume, process evidence/cancellation.");
}
finally
{
    // This specific GUID-owned temporary directory contains only this test's files.
    Directory.Delete(root, recursive: true);
}
