using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VideoProduction;

namespace VideoProduction.Providers;

public sealed record VideoJob(string Id, string IdempotencyHash, string RequestHash, VideoRequest Request,
    string Status, string Stage, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool CancellationRequested,
    VideoArtifact[] Artifacts, string? ErrorCode = null);
public sealed record VideoJobView(string JobId, string WorkDirectory, string Status, string Stage, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, bool CancellationRequested, VideoArtifact[] Artifacts, string? ErrorCode,
    VideoJobFailure? Failure = null, bool HumanReviewRequired = true, bool AutomaticRestartResume = false);
public sealed record VideoJobFailure(string Summary, bool OperatorReviewRequired);

public sealed class VideoJobs : BackgroundService
{
    private readonly object gate = new();
    private readonly Dictionary<string, VideoJob> jobs = [];
    private readonly Dictionary<string, string> keys = [];
    private readonly LinkedList<string> queue = [];
    private readonly SemaphoreSlim ready = new(0, 1);
    private readonly IVideoJobPipeline pipeline;
    private readonly ILogger<VideoJobs> logger;
    private readonly string root;
    private readonly FileStream processLock;
    private readonly int capacity;
    private CancellationTokenSource? active;
    private string? activeId;
    private bool stopping;

    public VideoJobs(VideoJobConfiguration config, IVideoJobPipeline pipeline, ILogger<VideoJobs> logger)
    {
        this.pipeline = pipeline;
        this.logger = logger;
        capacity = config.MaxPendingJobs;
        Guard.Require(capacity is >= 1 and <= 32, "jobs.capacity", "Pending job bound must be1..32.");
        root = PrivateFiles.LocalPath(config.StateRoot);
        Guard.Require(root != Path.GetPathRoot(root)
            && !string.Equals(root, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase),
            "jobs.root", "Choose a dedicated private job-state subdirectory.");
        if (!Directory.Exists(root)) Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "video-mcp-state.json");
        Guard.Require(File.Exists(marker) || !Directory.EnumerateFileSystemEntries(root).Any(),
            "jobs.root", "Job-state directory must be new/empty or an existing video MCP state directory.");
        PrivateFiles.Protect(root, directory: true);
        try { processLock = new FileStream(Path.Combine(root, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new VideoException("jobs.in-use", "Only one host may own this job-state directory."); }
        try
        {
            if (File.Exists(marker))
                Guard.Require(Json.Read<StateMarker>(marker) == new StateMarker(1, "video-production-mcp"),
                    "jobs.root", "Unsupported job-state directory format.");
            else Json.Write(marker, new StateMarker(1, "video-production-mcp"));
            var records = Path.Combine(root, "jobs");
            Directory.CreateDirectory(records);
            foreach (var directory in Directory.EnumerateDirectories(records))
            {
                ValidateId(Path.GetFileName(directory));
                var record = Path.Combine(directory, "job.json");
                if (!File.Exists(record))
                {
                    logger.LogWarning("Retained an uncommitted job directory without job.json for manual inspection; no job was accepted or resumed from it.");
                    continue;
                }
                var job = Json.Read<VideoJob>(record);
                ValidateId(job.Id);
                Guard.Require(Path.GetFileName(directory) == job.Id && Files.ValidHash(job.IdempotencyHash)
                    && Files.ValidHash(job.RequestHash) && !jobs.ContainsKey(job.Id) && !keys.ContainsKey(job.IdempotencyHash),
                    "jobs.state", "Persisted job identity is invalid or duplicated.");
                if (job.Status is "queued" or "running" or "cancelling")
                {
                    job = job with { Status = "interrupted", UpdatedAt = DateTimeOffset.UtcNow, ErrorCode = "jobs.interrupted" };
                    Save(job);
                }
                Guard.Require(job.Status is "completed" or "cancelled" or "failed" or "interrupted",
                    "jobs.state", "Persisted job state is invalid.");
                jobs.Add(job.Id, job);
                keys.Add(job.IdempotencyHash, job.Id);
            }
        }
        catch { processLock.Dispose(); throw; }
    }

    public VideoJobView Submit(string prompt, string idempotencyKey, VideoConstraints constraints, VideoOutline? plan = null)
    {
        ValidateSubmission(prompt, idempotencyKey, constraints, plan);
        var request = new VideoRequest(prompt, constraints, plan);
        var key = Files.HashText(idempotencyKey);
        var fingerprint = Files.Fingerprint(request);
        lock (gate)
        {
            if (keys.TryGetValue(key, out var existing))
            {
                var job = jobs[existing];
                Guard.Require(job.RequestHash == fingerprint, "jobs.idempotency-conflict", "That idempotency key belongs to different prompt/constraints.");
                return View(job);
            }
            Guard.Require(!stopping, "jobs.stopping", "Host is stopping; no new job was accepted.");
            pipeline.EnsureConfigured(request);
            Guard.Require(jobs.Values.Count(j => j.Status is "queued" or "running" or "cancelling") < capacity,
                "jobs.capacity", "The bounded generation queue is full; retry later with the same idempotency key.");
            var now = DateTimeOffset.UtcNow;
            var created = new VideoJob("job-" + Guid.NewGuid().ToString("N"), key, fingerprint, request,
                "queued", "queued", now, now, false, []);
            Directory.CreateDirectory(JobRoot(created.Id));
            Save(created);
            jobs.Add(created.Id, created);
            keys.Add(key, created.Id);
            queue.AddLast(created.Id);
            if (ready.CurrentCount == 0) ready.Release();
            return View(created);
        }
    }

    public static void ValidateSubmission(string prompt, string idempotencyKey, VideoConstraints constraints, VideoOutline? plan)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(prompt) && prompt.Length <= 6000 && !prompt.Contains('\0'),
            "jobs.prompt", "Prompt must be nonblank, at most6000 UTF16 characters and contain no NUL.");
        Guard.Require(idempotencyKey is { Length: >= 1 and <= 128 }
            && idempotencyKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'),
            "jobs.idempotency", "Supply a stable1..128character ASCII idempotency key; use a new key for an intentional new attempt.");
        Guard.Require(constraints.MaxScenes is >= 1 and <= 12 && constraints.MaxDurationSeconds is >= 5 and <= 300,
            "jobs.constraints", "This profile supports1..12scenes and5..300seconds.");
        if (plan is not null) ChatVideoPlanner.Validate(plan, constraints);
    }

    public VideoJobView Get(string id)
    {
        lock (gate) return View(Find(id));
    }

    public VideoJobView Cancel(string id)
    {
        lock (gate)
        {
            var job = Find(id);
            if (job.Status is "queued" or "running" or "cancelling")
            {
                job = job with {
                    CancellationRequested = true,
                    Status = job.Status == "queued" ? "cancelled" : "cancelling",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                Update(job);
                queue.Remove(id);
                if (id == activeId) active!.Cancel();
            }
            return View(job);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ready.WaitAsync(stoppingToken);
                string id;
                CancellationTokenSource cancellation;
                VideoJob job;
                lock (gate)
                {
                    if (queue.First is null) continue;
                    id = queue.First.Value;
                    queue.RemoveFirst();
                    if (queue.Count > 0 && ready.CurrentCount == 0) ready.Release();
                    job = jobs[id];
                    if (job.Status != "queued") continue;
                    active = cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    activeId = id;
                    job = job with { Status = "running", Stage = "starting", UpdatedAt = DateTimeOffset.UtcNow };
                    Update(job);
                }
                try
                {
                    var artifacts = await pipeline.Execute(JobRoot(id), job.Request, stage => Progress(id, stage), cancellation.Token);
                    foreach (var artifact in artifacts)
                        Guard.Require(Files.ValidHash(artifact.Sha256)
                            && Files.Hash(Files.Under(JobRoot(id), artifact.Path)) == artifact.Sha256,
                            "jobs.artifact", "Generation returned an invalid artifact reference.");
                    lock (gate)
                    {
                        job = jobs[id];
                        Update(job with {
                            Status = job.CancellationRequested ? "cancelled" : stopping ? "interrupted" : "completed",
                            Stage = "finished",
                            Artifacts = artifacts,
                            UpdatedAt = DateTimeOffset.UtcNow
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (gate)
                    {
                        job = jobs[id];
                        Update(job with {
                            Status = job.CancellationRequested ? "cancelled" : stoppingToken.IsCancellationRequested ? "interrupted" : "failed",
                            UpdatedAt = DateTimeOffset.UtcNow,
                            ErrorCode = job.CancellationRequested ? null
                                : stoppingToken.IsCancellationRequested ? "jobs.interrupted" : "generation.unexpected-cancellation"
                        });
                    }
                }
                catch (Exception error)
                {
                    var code = error is VideoException video ? video.Code : "generation.failed";
                    string stage;
                    lock (gate)
                    {
                        job = jobs[id];
                        stage = job.Stage;
                        Update(job with {
                            Status = job.CancellationRequested ? "cancelled" : stopping ? "interrupted" : "failed",
                            UpdatedAt = DateTimeOffset.UtcNow,
                            ErrorCode = job.CancellationRequested ? null : stopping ? "jobs.interrupted" : code
                        });
                    }
                    Json.Write(Path.Combine(JobRoot(id), "failure-diagnostic.json"), new
                    {
                        code, stage, exceptionType = error.GetType().FullName,
                        privateProcessError = code == "process.failed" ? error.Message : null,
                        frames = new System.Diagnostics.StackTrace(error, fNeedFileInfo: true).GetFrames().Take(20)
                            .Select(frame => new
                            {
                                type = frame.GetMethod()?.DeclaringType?.FullName,
                                method = frame.GetMethod()?.Name, line = frame.GetFileLineNumber()
                            }).ToArray()
                    });
                    logger.LogWarning("Video job {JobId} stopped at {Stage} with {Code}; raw provider/config data omitted.", id, stage, code);
                }
                finally
                {
                    lock (gate) { activeId = null; active = null; }
                    cancellation.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            stopping = true;
            foreach (var job in jobs.Values.Where(j => j.Status == "queued").ToArray())
                Update(job with { Status = "interrupted", ErrorCode = "jobs.interrupted", UpdatedAt = DateTimeOffset.UtcNow });
            queue.Clear();
        }
        await base.StopAsync(cancellationToken);
    }

    private void Progress(string id, string stage)
    {
        lock (gate) Update(jobs[id] with { Stage = stage, UpdatedAt = DateTimeOffset.UtcNow });
    }
    private string JobRoot(string id) => Files.Under(root, $"jobs/{id}");
    private void Save(VideoJob job) => Json.Write(Path.Combine(JobRoot(job.Id), "job.json"), job);
    private void Update(VideoJob job) { Save(job); jobs[job.Id] = job; }
    private VideoJob Find(string id)
    {
        ValidateId(id);
        return jobs.GetValueOrDefault(id) ?? throw new VideoException("jobs.missing", "No job with that ID exists in this configured host.");
    }
    private static void ValidateId(string id) => Guard.Require(Regex.IsMatch(id, @"\Ajob-[a-f0-9]{32}\z"),
        "jobs.id", "Job ID must be a handle returned by this host.");
    private static VideoJobView View(VideoJob job) => new(job.Id, $"jobs/{job.Id}", job.Status, job.Stage, job.CreatedAt, job.UpdatedAt,
        job.CancellationRequested, job.Artifacts.Select(a => a with { Path = $"jobs/{job.Id}/{a.Path}" }).ToArray(), job.ErrorCode,
        job.ErrorCode is null ? null : Failure(job.ErrorCode));
    private static VideoJobFailure Failure(string code) => code switch {
        "transcription.discrepancy" => new("Actual ASR differs from intended narration. The disputed transcript was not adopted and generation stopped. An operator must review retained audio and actual transcript, then explicitly bind any editorial decision to that exact evidence. No model approval/global bypass is available; a new attempt needs a new idempotency key.", true),
        "generation.duration" => new("Actual narration exceeds the requested duration. No speech was truncated or stretched. Revise the plan or explicitly change the duration, then submit an intentional new key.", false),
        "planner.output" => new("Planner output violated the bounded inert-plan schema or constraints. Correct the caller plan or explicitly configured planner and use a new attempt key.", false),
        "planner.provider" => new("The explicitly configured server chat provider failed. An operator must check that provider's private configuration/connectivity; no alternate model was substituted.", false),
        "jobs.interrupted" => new("The host stopped before completion. Existing files were preserved; automatic restart/resume is not implemented. An intentional new attempt requires a new key.", false),
        string routing when routing.StartsWith("routing.", StringComparison.Ordinal) =>
            new("Opt-in local storyboard routing stopped at the reported error. Operational, input and protocol failures are not uncertainty; no alternate generator was substituted. Inspect the retained private evidence before a new attempt.", false),
        _ => new("Local generation failed at the reported stage. An operator must inspect retained evidence and private diagnostics before a new attempt; this is not a completed or approved video. Raw exception/configuration data is intentionally omitted.", false)
    };
    public override void Dispose() { base.Dispose(); processLock.Dispose(); }
    private sealed record StateMarker(int SchemaVersion, string Application);
}
