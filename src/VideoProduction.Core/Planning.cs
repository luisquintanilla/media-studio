namespace VideoProduction;

public static class Planning
{
    public static string Worker(Tools tools) => Path.Combine(tools.SourceRoot, "workers", "browser", "worker.mjs");
    public static string WorkerDirectory(Tools tools) => Path.GetDirectoryName(Worker(tools))!;

    public static string WorkerFingerprint(Tools tools)
    {
        var directory = WorkerDirectory(tools);
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + ".cache" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderBy(p => Path.GetRelativePath(directory, p), StringComparer.Ordinal)
            .Select(p => new ArtifactHash(Path.GetRelativePath(directory, p).Replace('\\', '/'), Files.Hash(p))).ToArray();
        var browser = Processes.Run(tools.Node, [Worker(tools), "identity"], directory, CancellationToken.None, 30)
            .GetAwaiter().GetResult().Stdout.Trim();
        return Files.Fingerprint(new
        {
            files, browser, node = Files.Hash(tools.Node),
            ffmpeg = Files.Hash(tools.Ffmpeg), ffprobe = Files.Hash(tools.Ffprobe)
        });
    }

    public static ActionPlan Create(Project project, string root, string projectHash, Tools tools, string? privateAuthFile = null)
    {
        Validation.Validate(project, root);
        ArtifactHash? privateInput = null;
        if (project.Scenes.Any(s => s.Capture is not null))
        {
            Guard.Require(privateAuthFile is not null && File.Exists(privateAuthFile), "capture.auth",
                "Plan/approve/run require the same explicit --private-auth file outside the project; its path/hash are approval-bound, not exported.");
            var path = Path.GetFullPath(privateAuthFile!);
            Guard.Require(!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint),
                "capture.private-path", "Private credentials must be a regular file outside the project.");
            privateInput = new ArtifactHash(path, Files.Hash(path));
        }
        var workerHash = WorkerFingerprint(tools);
        var actions = new List<ActionSpec>();
        foreach (var scene in project.Scenes)
        {
            if (scene.Kind == SceneKind.Browser)
            {
                if (scene.Capture is { } capture)
                    JellyfinCapture.Validate(capture);
                actions.Add(new ActionSpec(scene.Id, scene.Capture is null ? "bundled-counter" : "reviewed-jellyfin",
                    tools.Node, Files.Hash(tools.Node),
                    [Worker(tools), scene.Capture is null ? "capture" : "jellyfin", Files.Under(root, $"build/acquisition/{scene.Id}.json")],
                    Path.GetFullPath(root), scene.Capture is null ? ["bundled:fixture.mjs"]
                        : ["profile:" + Files.Fingerprint(scene.Capture), privateInput!.Path],
                    [$"assets/takes/{scene.Id}"], scene.Capture is null
                        ? ["http://127.0.0.1:ephemeral (worker-owned fixture only)"] : [scene.Capture.Origin],
                    scene.Capture is null ? "trusted-bundled-fixture-only" : "reviewed-jellyfin-loopback",
                    scene.Capture is null ? 60 : 180, 1_000_000));
            }
            if (scene.Kind == SceneKind.Terminal)
                actions.Add(new ActionSpec(scene.Id, "bundled-sum", tools.Node, Files.Hash(tools.Node),
                    [Path.Combine(WorkerDirectory(tools), "sum-demo.mjs")],
                    Path.GetFullPath(root), ["bundled:sum-demo.mjs"], [$"assets/takes/{scene.Id}"],
                    [], "trusted-bundled-fixture-only", 15, 100_000));
        }
        var inputs = Files.Fingerprint(new { project, root = Path.GetFullPath(root), workerHash, privateInput });
        var fingerprint = Files.Fingerprint(new { projectHash, inputs, workerHash, actions });
        return new ActionPlan(1, projectHash, inputs, workerHash, actions.ToArray(), fingerprint, privateInput);
    }

    public static void Authorize(ActionPlan expected, ActionPlan supplied, Approval approval)
    {
        Guard.Require(Json.Text(expected) == Json.Text(supplied), "approval.changed", "Action graph/tool/workspace/input changed; create a fresh plan and approve it.");
        var scope = expected.Actions.Any(a => a.Trust == "reviewed-jellyfin-loopback")
            ? "reviewed-jellyfin-loopback" : "trusted-bundled-fixture-only";
        Guard.Require(approval.SchemaVersion == 1 && approval.Fingerprint == expected.Fingerprint
            && !string.IsNullOrWhiteSpace(approval.ApprovedBy) && approval.Scope == scope,
            "approval.missing", "Explicit scoped approval must match this exact action graph.");
        Guard.Require(expected.Actions.All(a => a.Trust is "trusted-bundled-fixture-only" or "reviewed-jellyfin-loopback"), "isolation.unavailable",
            "No untrusted host execution profile exists; use an independently reviewed container/VM adapter.");
    }
}
