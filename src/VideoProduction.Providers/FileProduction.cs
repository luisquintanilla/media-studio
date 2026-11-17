using System.Diagnostics;
using MediaStudio.Audio;

namespace VideoProduction.Providers;

public sealed record FileProductionConfiguration(int SchemaVersion, string StateRoot, GenerationConfiguration Generation);
public enum FileProductionExitCode { Success = 0, Failed = 1, Conflict = 73, Interrupted = 74, Busy = 75, Cancelled = 130 }
public sealed record DraftSnapshot(string DraftId, long Revision, string ContentSha256);
public sealed record ReviewedVideoRequest(int SchemaVersion, string IdempotencyKey, VideoRequest Request, DraftSnapshot? Draft = null);
public sealed record ReviewedAudioRequest(int SchemaVersion, string IdempotencyKey, AudioProductionRequest Request, AudioApproval Approval);
public sealed record ProducerIdentity(int ProcessId, long StartedUtcTicks);
public sealed record FileAttempt(int SchemaVersion, string Id, string Token, string IdempotencyHash,
    string RequestSha256, string ReviewedRequestSha256, ProducerIdentity Producer, string Status, string Stage,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool CancellationRequested = false, string? ErrorCode = null,
    string MediaKind = "video");
public sealed record FileAttemptManifest(int SchemaVersion, string AttemptId, string Token, string ReviewedRequestSha256,
    DateTimeOffset CompletedAt, VideoArtifact[] Artifacts, bool HumanReviewRequired = true);
public sealed record FileAttemptStatus(int SchemaVersion, string AttemptId, string Status, string Stage,
    bool Live, string Liveness, bool CancellationRequested, VideoArtifact[] Artifacts, string? ErrorCode,
    bool Replay = false, bool HumanReviewRequired = true, bool AutomaticRestartResume = false);
public sealed record FileCancellation(int SchemaVersion, string AttemptId, string Token,
    string ReviewedRequestSha256, DateTimeOffset RequestedAt);
internal sealed record AttemptIndex(int SchemaVersion, string AttemptId, string IdempotencyHash, string ReviewedRequestSha256);

internal static class FileTransactions
{
    public static FileStream? TryLock(string path)
    {
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11) { return null; }
    }

    public static FileStream Lock(string path, CancellationToken ct = default)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (TryLock(path) is { } held) return held;
            Guard.Require(elapsed.Elapsed < TimeSpan.FromSeconds(5), "attempt.transaction-busy",
                "Another short file transaction is in progress. No new work was accepted.");
            ct.WaitHandle.WaitOne(20);
        }
    }
}

public sealed partial class FileProductionStore
{
    internal string Root { get; }
    private sealed record Marker(int SchemaVersion, string Format);

    public FileProductionStore(string root)
    {
        Root = PrivateFiles.LocalPath(root).TrimEnd(Path.DirectorySeparatorChar);
        Guard.Require(Root != Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar)
            && !string.Equals(Root, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase),
            "attempt.root", "Choose a dedicated new private file-production state directory.");
        var marker = Path.Combine(Root, "video-file-state.json");
        if (File.Exists(marker))
            Guard.Require(Json.Read<Marker>(marker) == new Marker(1, "video-file-production"),
                "attempt.root", "Unsupported file-production storage format; no migration is performed.");
        Guard.Require(!Directory.Exists(Root) || File.Exists(marker)
            || Directory.EnumerateFileSystemEntries(Root).All(p => Path.GetFileName(p) == "admission.lock"),
            "attempt.root", "Only new/empty or explicit video-file-state-v1 storage is accepted. Legacy jobs/drafts remain read-only and are not migrated.");
        Directory.CreateDirectory(Root);
        using var transaction = Admission();
        if (File.Exists(marker))
            Guard.Require(Json.Read<Marker>(marker) == new Marker(1, "video-file-production"),
                "attempt.root", "Unsupported file-production storage format.");
        else
        {
            Guard.Require(Directory.EnumerateFileSystemEntries(Root).All(p => Path.GetFileName(p) == "admission.lock"),
                "attempt.root", "Unrecognized files in new production storage.");
            Json.Write(marker, new Marker(1, "video-file-production"));
        }
        PrivateFiles.Protect(Root, directory: true);
        foreach (var name in new[] { "attempts", "keys", "drafts" }) Directory.CreateDirectory(PathFor(name));
    }

    internal string PathFor(string relative) => Files.Under(Root, relative);
    internal FileStream Admission(CancellationToken ct = default) => FileTransactions.Lock(PathFor("admission.lock"), ct);
    internal static void ValidateId(string id) => Guard.Require(
        System.Text.RegularExpressions.Regex.IsMatch(id, @"\Aattempt-[a-f0-9]{32}\z"),
        "attempt.id", "Expected an explicit attempt identity, not a path or process ID.");
    internal string AttemptRoot(string id) { ValidateId(id); return PathFor("attempts/" + id); }
    private FileAttempt ReadAttempt(string id)
    {
        var attempt = Json.Read<FileAttempt>(Path.Combine(AttemptRoot(id), "attempt.json"));
        Guard.Require(attempt.SchemaVersion == 1 && attempt.Id == id && Files.ValidHash(attempt.Token)
            && Files.ValidHash(attempt.IdempotencyHash) && Files.ValidHash(attempt.RequestSha256)
            && Files.ValidHash(attempt.ReviewedRequestSha256) && attempt.Producer.ProcessId > 0 && attempt.Producer.StartedUtcTicks > 0
            && attempt.Status is "running" or "cancelling" or "completed" or "failed" or "cancelled" or "interrupted",
            "attempt.state", "Invalid versioned attempt record.");
        if (attempt.MediaKind == "audio")
        {
            var audio = Json.Read<ReviewedAudioRequest>(Path.Combine(AttemptRoot(id), "request.json"));
            Guard.Require(audio.SchemaVersion == 1 && Files.Fingerprint(audio) == attempt.ReviewedRequestSha256
                && Files.Fingerprint(audio.Request) == attempt.RequestSha256
                && Files.HashText(audio.IdempotencyKey) == attempt.IdempotencyHash
                && audio.Approval.RequestSha256 == attempt.RequestSha256,
                "attempt.identity", "Immutable audio request/approval/attempt identity changed.");
            return attempt;
        }
        if (attempt.MediaKind == "media")
        {
            var media = Json.Read<MediaReviewedRequest>(Path.Combine(AttemptRoot(id), "request.json"));
            Guard.Require(media.SchemaVersion == 1 && Files.Fingerprint(media) == attempt.ReviewedRequestSha256
                && Files.Fingerprint(media.Project) == attempt.RequestSha256
                && Files.HashText(MediaProjectStore.SubmissionKey(media.Project)) == attempt.IdempotencyHash,
                "attempt.identity", "Immutable media revision/output-set approval identity changed.");
            return attempt;
        }
        Guard.Require(attempt.MediaKind == "video", "attempt.kind", "Unknown attempt media kind.");
        var reviewed = Json.Read<ReviewedVideoRequest>(Path.Combine(AttemptRoot(id), "request.json"));
        Guard.Require(reviewed.SchemaVersion == 1 && reviewed.Request.Plan is not null
            && Files.Fingerprint(reviewed) == attempt.ReviewedRequestSha256
            && Files.Fingerprint(reviewed.Request) == attempt.RequestSha256
            && Files.HashText(reviewed.IdempotencyKey) == attempt.IdempotencyHash,
            "attempt.identity", "Immutable reviewed request/attempt identity changed.");
        return attempt;
    }
    public FileAttempt Attempt(string id) => ReadAttempt(id);
    private void Save(FileAttempt attempt) => Json.Write(Path.Combine(AttemptRoot(attempt.Id), "attempt.json"), attempt);
    internal static bool Terminal(string status) => status is "completed" or "failed" or "cancelled" or "interrupted";
    internal FileAttempt? Lookup(string key)
    {
        var hash = Files.HashText(key);
        var path = PathFor($"keys/{hash}.json");
        if (!File.Exists(path)) return null;
        var index = Json.Read<AttemptIndex>(path);
        var record = ReadAttempt(index.AttemptId);
        Guard.Require(index.SchemaVersion == 1 && index.IdempotencyHash == hash
            && record.IdempotencyHash == hash && record.ReviewedRequestSha256 == index.ReviewedRequestSha256,
            "attempt.index", "Committed admission index differs from its immutable attempt.");
        return record;
    }
    internal ReviewedVideoRequest Reviewed(FileAttempt attempt) =>
        Json.Read<ReviewedVideoRequest>(Path.Combine(AttemptRoot(attempt.Id), "request.json"));

    private FileAttemptManifest? Manifest(FileAttempt attempt)
    {
        var path = Path.Combine(AttemptRoot(attempt.Id), "result.json");
        if (!File.Exists(path)) return null;
        var result = Json.Read<FileAttemptManifest>(path);
        Guard.Require(result.SchemaVersion == 1 && result.AttemptId == attempt.Id && result.Token == attempt.Token
            && result.ReviewedRequestSha256 == attempt.ReviewedRequestSha256 && result.HumanReviewRequired,
            "attempt.manifest", "Final completion manifest does not bind this attempt.");
        var retired = RetiredArtifacts(attempt);
        VerifyArtifacts(AttemptRoot(attempt.Id), result.Artifacts, retired);
        return retired.Count == 0 ? result : result with { Artifacts = result.Artifacts.Where(a => !retired.Contains(a)).ToArray() };
    }

    private static void VerifyArtifacts(string root, VideoArtifact[] artifacts, HashSet<VideoArtifact>? retired = null)
    {
        Guard.Require(artifacts.Length is > 0 and <= 64
            && artifacts.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == artifacts.Length,
            "attempt.artifacts", "A completed attempt requires1..64distinct committed artifact handles.");
        foreach (var artifact in artifacts)
            Guard.Require(Files.ValidHash(artifact.Sha256) && artifact.Kind.Length is > 0 and <= 80
                && artifact.Path is not ("attempt.json" or "request.json" or "result.json" or "cancel.json" or "failure-diagnostic.json")
                && (retired?.Contains(artifact) == true || Files.Hash(Files.Under(root, artifact.Path)) == artifact.Sha256),
                "attempt.artifact-hash", "A final artifact differs from its committed hash.");
    }

    private void ReconcileAbandoned()
    {
        foreach (var directory in Directory.EnumerateDirectories(PathFor("attempts")))
        {
            var id = Path.GetFileName(directory);
            ValidateId(id);
            if (!File.Exists(Path.Combine(directory, "attempt.json"))) continue;
            var attempt = ReadAttempt(id);
            if (Terminal(attempt.Status)) continue;
            var committed = Manifest(attempt);
            Save(attempt with {
                Status = committed is null ? "interrupted" : "completed",
                Stage = committed is null ? attempt.Stage : "completed",
                UpdatedAt = DateTimeOffset.UtcNow,
                ErrorCode = committed is null ? "attempt.interrupted" : null
            });
        }
    }

    internal void ReconcileIfIdle()
    {
        using var available = FileTransactions.TryLock(PathFor("active.lock"));
        if (available is not null) ReconcileAbandoned();
    }

    private static ProducerIdentity CurrentProducer()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks);
    }
    private static bool SameLiveProcess(ProducerIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == identity.StartedUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private FileAttemptStatus Project(FileAttempt attempt, bool ownsLock = false, bool replay = false)
    {
        var result = Manifest(attempt);
        Guard.Require(attempt.Status != "completed" || result is not null,
            "attempt.manifest", "Completed status without its final manifest is not success.");
        var live = false;
        var liveness = Terminal(attempt.Status) ? "terminal" : "unverified-lock-held";
        if (!Terminal(attempt.Status))
        {
            var owner = Json.Read<FileAttempt>(PathFor("active-owner.json"));
            live = owner.Id == attempt.Id && owner.Token == attempt.Token && owner.Producer == attempt.Producer
                && (ownsLock || SameLiveProcess(attempt.Producer));
            liveness = live ? "exclusive-owner-and-process-start-identity" : "not-this-verified-owner";
        }
        return new(1, attempt.Id, attempt.Status, attempt.Stage, live, liveness, attempt.CancellationRequested,
            attempt.Status == "completed" ? result!.Artifacts : [], attempt.ErrorCode, replay);
    }

    public FileAttemptStatus Status(string id)
    {
        using var transaction = Admission();
        using var available = FileTransactions.TryLock(PathFor("active.lock"));
        if (available is not null) ReconcileAbandoned();
        return Project(ReadAttempt(id));
    }

    public FileAttemptStatus ObserveStatus(string id)
    {
        using var transaction = Admission();
        var attempt = ReadAttempt(id);
        using var available = FileTransactions.TryLock(PathFor("active.lock"));
        if (available is not null && !Terminal(attempt.Status))
        {
            var committed = Manifest(attempt);
            attempt = attempt with {
                Status = committed is null ? "interrupted" : "completed",
                Stage = committed is null ? attempt.Stage : "completed",
                ErrorCode = committed is null ? "attempt.interrupted" : null
            };
        }
        return Project(attempt);
    }

    public MediaVerifiedArtifacts MediaArtifacts(string id, bool observeOnly = false)
    {
        using var transaction = Admission();
        if (!observeOnly) ReconcileIfIdle();
        var attempt = ReadAttempt(id);
        Guard.Require(attempt.MediaKind == "media", "media.artifacts", "Per-output receipts apply only to the medium-aware production path.");
        var reviewed = Json.Read<MediaReviewedRequest>(Path.Combine(AttemptRoot(id), "request.json"));
        var receipts = new List<MediaOutputReceipt>();
        var deleted = 0;
        foreach (var output in new[] { "audio", "video" })
        {
            var path = Path.Combine(AttemptRoot(id), output + "-result.json");
            if (!File.Exists(path)) continue;
            if (ReadDeletion(attempt, output) is not null) { deleted++; continue; }
            var receipt = Json.Read<MediaOutputReceipt>(path);
            Guard.Require(receipt.SchemaVersion == 1 && receipt.AttemptId == id && receipt.Token == attempt.Token
                && receipt.ReviewedRequestSha256 == attempt.ReviewedRequestSha256 && receipt.Output == output
                && (output == "audio" ? reviewed.Project.Content.Outputs is MediaOutput.Audio or MediaOutput.Both
                    : reviewed.Project.Content.Outputs is MediaOutput.Video or MediaOutput.Both),
                "media.receipt", "Per-output receipt does not bind this exact attempt, output set and saved approval.");
            VerifyArtifacts(AttemptRoot(id), receipt.Artifacts);
            if (output == "audio")
            {
                var master = AudioProduction.Verify(Path.Combine(AttemptRoot(id), "audio-master"));
                var audioRequest = Json.Read<AudioProductionRequest>(Path.Combine(AttemptRoot(id), "audio-master", "request.json"));
                Guard.Require(audioRequest.ProjectId == reviewed.Project.Id && audioRequest.Revision == reviewed.Project.Revision
                    && Files.Fingerprint(audioRequest.Parts) == Files.Fingerprint(reviewed.Project.Content.AudioScript!)
                    && receipt.Artifacts.SingleOrDefault(a => a.Kind == "audio-master") is { Path: "audio-master/master.wav" } handle
                    && handle.Sha256 == master.MasterSha256,
                    "media.receipt", "Retained audio does not bind the reviewed revision/script and committed master.");
            }
            receipts.Add(receipt);
        }
        if (attempt.Status == "completed")
        {
            _ = Manifest(attempt) ?? throw new VideoException("attempt.manifest", "Completed media production lacks its final manifest.");
            Guard.Require(receipts.Count + deleted == (reviewed.Project.Content.Outputs == MediaOutput.Both ? 2 : 1),
                "media.receipt", "Completed output set lacks a committed per-output receipt.");
        }
        return new(1, id, attempt.Status, receipts.ToArray());
    }

    private void CommitMediaOutput(string root, string output, VideoArtifact[] artifacts)
    {
        using var transaction = Admission();
        var attempt = ReadAttempt(Path.GetFileName(root));
        VerifyArtifacts(root, artifacts);
        var path = Path.Combine(root, output + "-result.json");
        Guard.Require(!File.Exists(path), "media.receipt", "A committed per-output receipt cannot be overwritten.");
        Json.Write(path, new MediaOutputReceipt(1, attempt.Id, attempt.Token, attempt.ReviewedRequestSha256, output, artifacts));
    }

    public FileAttemptStatus Cancel(string id)
    {
        using var transaction = Admission();
        using var available = FileTransactions.TryLock(PathFor("active.lock"));
        if (available is not null) ReconcileAbandoned();
        var attempt = ReadAttempt(id);
        if (!Terminal(attempt.Status))
        {
            var request = new FileCancellation(1, id, attempt.Token, attempt.ReviewedRequestSha256, DateTimeOffset.UtcNow);
            if (!CancellationRequested(attempt)) Json.Write(Path.Combine(AttemptRoot(id), "cancel.json"), request);
            attempt = attempt with { CancellationRequested = true, Status = "cancelling", UpdatedAt = DateTimeOffset.UtcNow };
            Save(attempt);
        }
        return Project(attempt);
    }

    private bool CancellationRequested(FileAttempt attempt)
    {
        var path = Path.Combine(AttemptRoot(attempt.Id), "cancel.json");
        if (!File.Exists(path)) return false;
        var request = Json.Read<FileCancellation>(path);
        Guard.Require(request.SchemaVersion == 1 && request.AttemptId == attempt.Id && request.Token == attempt.Token
            && request.ReviewedRequestSha256 == attempt.ReviewedRequestSha256,
            "attempt.cancel-identity", "Cancellation request does not bind this exact attempt.");
        return true;
    }

    public Task<FileAttemptStatus> Produce(ReviewedVideoRequest request, IVideoJobPipeline pipeline,
        Action<FileAttemptStatus>? observe = null, CancellationToken ct = default)
    {
        request = System.Text.Json.JsonSerializer.Deserialize<ReviewedVideoRequest>(Json.Text(request), Json.Options)
            ?? throw new VideoException("attempt.reviewed-request", "Missing reviewed request.");
        Guard.Require(request.SchemaVersion == 1 && request.Request.Plan is not null, "attempt.reviewed-request",
            "Whole-attempt production requires schemaVersion1 and an explicit reviewed storyboard.");
        VideoJobs.ValidateSubmission(request.Request.Prompt, request.IdempotencyKey, request.Request.Constraints, request.Request.Plan);
        Guard.Require(request.Draft is not null
            || !System.Text.RegularExpressions.Regex.IsMatch(request.IdempotencyKey, @"\Adraft-[a-f0-9]{32}-r[0-9]+\z"),
            "draft.admission", "Draft-revision admission keys are reserved for exact saved snapshot requests.");
        return ProduceCore("video", request, request.Request, request.IdempotencyKey,
            () => pipeline.EnsureConfigured(request.Request),
            (attempt, commit) => new FileDraftStore(this).Admit(request, attempt, commit),
            (root, progress, token) => pipeline.Execute(root, request.Request, progress, token), observe, ct);
    }

    public Task<FileAttemptStatus> ProduceAudio(ReviewedAudioRequest request, INarrationBackend narration,
        Action<FileAttemptStatus>? observe = null, CancellationToken ct = default)
    {
        request = System.Text.Json.JsonSerializer.Deserialize<ReviewedAudioRequest>(Json.Text(request), Json.Options)
            ?? throw new VideoException("attempt.reviewed-request", "Missing reviewed audio request.");
        Guard.Require(request.SchemaVersion == 1 && request.IdempotencyKey.Length is > 0 and <= 128
            && request.Request.Parts.Length <= 12
            && request.Approval == AudioProduction.Approve(request.Request, request.Approval.ApprovedBy),
            "audio.approval", "Audio admission requires the exact reviewed request and bounded approved output.");
        return ProduceCore("audio", request, request.Request, request.IdempotencyKey,
            () => ArgumentNullException.ThrowIfNull(narration), (_, commit) => commit(),
            async (root, progress, token) =>
            {
                progress("audio-synthesis-composition");
                var output = Path.Combine(root, request.Request.OutputName);
                await AudioProduction.Produce(request.Request, request.Approval, narration, output, token);
                progress("audio-integrity");
                _ = AudioProduction.Verify(output);
                return Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Select(path =>
                    new VideoArtifact(Path.GetRelativePath(root, path).Replace('\\', '/'), Files.Hash(path),
                    Path.GetFileName(path) switch {
                        "master.wav" => "audio-master",
                        "transcript.json" => "audio-transcript",
                        "manifest.json" => "audio-manifest",
                        _ => "audio-evidence"
                    })).ToArray();
            }, observe, ct);
    }

    public Task<FileAttemptStatus> ProduceMedia(MediaReviewedRequest request, MediaProductionConfiguration configuration,
        Action<FileAttemptStatus>? observe = null, CancellationToken ct = default,
        Func<MediaCoverConfiguration, Microsoft.Extensions.AI.IImageGenerator>? coverFactory = null)
    {
        request = System.Text.Json.JsonSerializer.Deserialize<MediaReviewedRequest>(Json.Text(request), Json.Options)
            ?? throw new VideoException("media.approval", "An explicit reviewed media request is required.");
        configuration = System.Text.Json.JsonSerializer.Deserialize<MediaProductionConfiguration>(Json.Text(configuration), Json.Options)!;
        Guard.Require(request.SchemaVersion == 1 && configuration.SchemaVersion == 1
            && Files.Fingerprint(configuration) == request.ConfigurationSha256
            && string.Equals(PrivateFiles.LocalPath(configuration.StateRoot).TrimEnd(Path.DirectorySeparatorChar),
                Root, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(request.ApprovedBy),
            "media.approval", "Exact saved revision/output-set/provider/resource configuration approval is required.");
        MediaProjectStore.Validate(request.Project.Content, ready: true);
        MediaApprovals.Validate(request, configuration);
        var content = request.Project.Content;
        var audioSelected = content.Outputs is MediaOutput.Audio or MediaOutput.Both;
        var videoSelected = content.Outputs is MediaOutput.Video or MediaOutput.Both;
        var verification = configuration.Verification;
        Guard.Require(verification is null || configuration.Audio is null && configuration.Video is null,
            "verification.mode", "Explicit offline verification cannot masquerade as configured live providers or fall back from them.");
        Guard.Require(verification is null || configuration.Cover is null || coverFactory is not null,
            "verification.cover", "Offline verification cannot invoke a live cover provider without an explicitly injected fixture.");
        Guard.Require(!audioSelected || verification is not null || configuration.Audio is not null, "audio.unconfigured", "The selected audio output has no configured narration provider.");
        Guard.Require(!videoSelected || verification is not null || configuration.Video is not null, "video.unconfigured", "The selected video output has no configured visual runtime.");
        var videoRequest = videoSelected ? new VideoRequest(content.Prompt, new(content.MaximumSeconds, 12), content.Storyboard) : null;
        IVideoJobPipeline? pipeline = !videoSelected ? null : verification is not null
            ? new OfflineEncodedVideoPipeline(verification)
            : new ProductionVideoPipeline(new ChatVideoPlanner(null, null), configuration.Video!);
        var projects = new MediaProjectStore(this);
        return ProduceCore("media", request, request.Project, MediaProjectStore.SubmissionKey(request.Project),
            () => pipeline?.EnsureConfigured(videoRequest!),
            (_, commit) => projects.Admit(request, commit),
            async (root, progress, token) =>
            {
                var outcomes = new List<MediaOutputOutcome>();
                var artifacts = new List<VideoArtifact>();
                var output = audioSelected ? "audio" : "video";
                Exception? failure = null;
                var currentStage = "starting";
                void Advance(string stage) { currentStage = stage; progress(stage); }
                void Report() => Json.Write(Path.Combine(root, "output-outcomes.json"),
                    new MediaOutputReport(1, request.Project.Id, request.Project.Revision,
                        Files.Fingerprint(content), false, outcomes.ToArray()));
                try
                {
                    if (audioSelected)
                    {
                        var settings = configuration.Audio;
                        var rate = verification is not null ? 16000 : settings!.SampleRate;
                        var audio = new AudioProductionRequest(1, request.Project.Id, request.Project.Revision,
                            "audio-master", rate, checked((long)rate * content.MaximumSeconds),
                            content.AudioScript!, verification is not null ? OfflineNarrationBackend.Voice
                                : new(settings!.Speech.Voice, settings.Speech.Language, settings.Speech.Speed),
                            verification is not null ? OfflineNarrationBackend.Identity : settings!.Identity);
                        using var configured = verification is null ? new ConfiguredNarrationBackend(settings!.Speech, audio.Voice, settings.Identity) : null;
                        using var synthetic = verification is not null ? new OfflineNarrationBackend(verification.DelayMilliseconds) : null;
                        var backend = (INarrationBackend?)configured ?? synthetic!;
                        Advance("audio-synthesis-composition");
                        var directory = Path.Combine(root, "audio-master");
                        await AudioProduction.Produce(audio, AudioProduction.Approve(audio, request.ApprovedBy), backend, directory, token);
                        _ = AudioProduction.Verify(directory);
                        var audioArtifacts = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                            .Select(path => new VideoArtifact(Path.GetRelativePath(root, path).Replace('\\', '/'), Files.Hash(path),
                                Path.GetFileName(path) == "master.wav" ? "audio-master" : "audio-evidence")).ToArray();
                        CommitMediaOutput(root, "audio", audioArtifacts);
                        artifacts.AddRange(audioArtifacts);
                        outcomes.Add(new("audio", "completed", Files.Hash(Path.Combine(directory, "manifest.json"))));
                        Report();
                    }
                    if (videoSelected)
                    {
                        output = "video";
                        var videoArtifacts = await pipeline!.Execute(root, videoRequest!, Advance, token);
                        CommitMediaOutput(root, "video", videoArtifacts);
                        artifacts.AddRange(videoArtifacts);
                        outcomes.Add(new("video", "completed"));
                        Report();
                    }
                }
                catch (Exception error) when (error is VideoException or IOException or UnauthorizedAccessException
                    or System.Text.Json.JsonException or ArgumentException or InvalidOperationException
                    or OperationCanceledException or NotSupportedException or OverflowException or System.ComponentModel.Win32Exception)
                {
                    outcomes.Add(new(output, error is OperationCanceledException ? "cancelled" : "failed",
                        ErrorCode: error is VideoException known ? known.Code : error.GetType().Name));
                    if (videoSelected && output == "audio") outcomes.Add(new("video", "not-started"));
                    Report();
                    failure = error;
                }
                if (configuration.Cover is { } cover)
                {
                    var mediaStage = currentStage;
                    if (artifacts.Count > 0 && !token.IsCancellationRequested)
                    {
                        try
                        {
                            Advance("generating-library-cover");
                            Directory.CreateDirectory(PrivateFiles.LocalPath(cover.Raster.ScratchRoot));
                            PrivateFiles.Protect(cover.Raster.ScratchRoot, directory: true);
                            var covers = new MediaCoverStore(this);
                            var generated = covers.Read(request.Project);
                            if (generated is null)
                            {
                                using var generator = coverFactory is null
                                    ? new ConfiguredSvgImageGenerator(cover.Connection, cover.Raster, cover.Routing)
                                    : coverFactory(cover) ?? throw new VideoException("cover.factory", "The explicit image factory returned no generator; live fallback is forbidden.");
                                generated = await covers.GenerateAsync(request.Project, generator, token);
                            }
                            outcomes.Add(new("cover", "completed", Files.Hash(Path.Combine(Root, "projects", "covers",
                                $"{request.Project.Id}-r{request.Project.Revision}", "cover.json"))));
                        }
                        catch (Exception error) when (error is VideoException or IOException or UnauthorizedAccessException
                            or System.Text.Json.JsonException or ArgumentException or InvalidOperationException
                            or OperationCanceledException or NotSupportedException or OverflowException or System.ComponentModel.Win32Exception)
                        {
                            outcomes.Add(new("cover", error is OperationCanceledException ? "cancelled" : "failed",
                                ErrorCode: error is VideoException known ? known.Code : error.GetType().Name));
                            failure ??= error;
                        }
                    }
                    else outcomes.Add(new("cover", "not-started"));
                    Report();
                    if (failure is not null && outcomes.Any(o => o.Output != "cover" && o.Status == "failed"))
                        Advance(mediaStage);
                }
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                artifacts.Add(new("output-outcomes.json", Files.Hash(Path.Combine(root, "output-outcomes.json")), "output-outcomes"));
                return artifacts.ToArray();
            }, observe, ct);
    }

    private async Task<FileAttemptStatus> ProduceCore(string mediaKind, object reviewedRequest, object payload,
        string idempotencyKey, Action ensureConfigured, Action<FileAttempt, Action> admit,
        Func<string, Action<string>, CancellationToken, Task<VideoArtifact[]>> execute,
        Action<FileAttemptStatus>? observe, CancellationToken ct)
    {
        FileStream? owner = null;
        FileAttempt attempt;
        using (var transaction = Admission(ct))
        {
            if (Lookup(idempotencyKey) is { } existing)
            {
                Guard.Require(existing.ReviewedRequestSha256 == Files.Fingerprint(reviewedRequest)
                    && existing.MediaKind == mediaKind,
                    "attempt.idempotency-conflict", "This key already binds a different reviewed payload; nothing new was accepted.");
                using var available = FileTransactions.TryLock(PathFor("active.lock"));
                if (available is not null) ReconcileAbandoned();
                return Project(ReadAttempt(existing.Id), replay: true);
            }
            owner = FileTransactions.TryLock(PathFor("active.lock"));
            Guard.Require(owner is not null, "attempt.busy", "One production already owns this configured private root. No attempt was accepted or queued.");
            try
            {
                ReconcileAbandoned();
                ensureConfigured();
                ct.ThrowIfCancellationRequested();
                var now = DateTimeOffset.UtcNow;
                attempt = new(1, "attempt-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                    Files.HashText(idempotencyKey), Files.Fingerprint(payload), Files.Fingerprint(reviewedRequest),
                    CurrentProducer(), "running", "starting", now, now, MediaKind: mediaKind);
                admit(attempt, () =>
                {
                    Guard.Require(!Directory.Exists(AttemptRoot(attempt.Id)), "attempt.exists", "New attempt identity already exists.");
                    Directory.CreateDirectory(AttemptRoot(attempt.Id));
                    Json.Write(Path.Combine(AttemptRoot(attempt.Id), "request.json"), reviewedRequest);
                    Save(attempt);
                    Json.Write(PathFor("active-owner.json"), attempt);
                    Json.Write(PathFor($"keys/{attempt.IdempotencyHash}.json"),
                        new AttemptIndex(1, attempt.Id, attempt.IdempotencyHash, attempt.ReviewedRequestSha256));
                });
            }
            catch { owner!.Dispose(); throw; }
        }
        using (owner)
        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct))
        using (var stopWatching = new CancellationTokenSource())
        {
            VideoArtifact[] artifacts = [];
            Exception? failure = null;
            var watcher = WatchCancellation(attempt, cancellation, stopWatching.Token);
            try
            {
                observe?.Invoke(Project(attempt, ownsLock: true));
                artifacts = await execute(AttemptRoot(attempt.Id), stage =>
                {
                    Guard.Require(stage.Length is > 0 and <= 80 && stage.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'),
                        "attempt.stage", "Production progress must use bounded typed stage labels.");
                    FileAttemptStatus snapshot;
                    using (var transaction = Admission(cancellation.Token))
                    {
                        attempt = ReadAttempt(attempt.Id) with { Stage = stage, UpdatedAt = DateTimeOffset.UtcNow };
                        Save(attempt);
                        snapshot = Project(attempt, ownsLock: true);
                    }
                    observe?.Invoke(snapshot);
                }, cancellation.Token);
            }
            catch (Exception error) when (error is VideoException or IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException or ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception or OverflowException or OperationCanceledException or NotSupportedException)
            { failure = error; }
            finally
            {
                await stopWatching.CancelAsync();
                try { await watcher; }
                catch (OperationCanceledException) when (stopWatching.IsCancellationRequested) { }
                catch (Exception error) when (error is VideoException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { failure = error; }
            }
            using var terminal = Admission();
            attempt = ReadAttempt(attempt.Id);
            var cancelled = cancellation.IsCancellationRequested;
            try { cancelled |= CancellationRequested(attempt); }
            catch (Exception error) when (error is VideoException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { failure = error; }
            if (failure is OperationCanceledException && !cancelled)
                failure = new VideoException("attempt.unexpected-cancellation", "Pipeline cancellation did not originate from this attempt's token/request.");
            if (failure is null && !cancelled)
            {
                try { VerifyArtifacts(AttemptRoot(attempt.Id), artifacts); }
                catch (Exception error) when (error is VideoException or IOException or UnauthorizedAccessException) { failure = error; }
            }
            if (failure is not null && failure is not OperationCanceledException)
            {
                Json.Write(Path.Combine(AttemptRoot(attempt.Id), "failure-diagnostic.json"),
                    new { schemaVersion = 1, type = failure.GetType().FullName, failure.Message });
                attempt = attempt with { Status = "failed", ErrorCode = failure is VideoException video ? video.Code : "attempt.runtime" };
            }
            else if (cancelled)
                attempt = attempt with { Status = "cancelled", CancellationRequested = true, ErrorCode = "attempt.cancelled" };
            else
            {
                Json.Write(Path.Combine(AttemptRoot(attempt.Id), "result.json"),
                    new FileAttemptManifest(1, attempt.Id, attempt.Token, attempt.ReviewedRequestSha256, DateTimeOffset.UtcNow, artifacts));
                attempt = attempt with { Status = "completed", Stage = "completed" };
            }
            attempt = attempt with { UpdatedAt = DateTimeOffset.UtcNow };
            Save(attempt);
            return Project(attempt);
        }
    }

    private async Task WatchCancellation(FileAttempt attempt, CancellationTokenSource cancellation, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                stop.ThrowIfCancellationRequested();
                if (CancellationRequested(attempt)) { await cancellation.CancelAsync(); return; }
                await Task.Delay(50, stop);
            }
        }
        catch (Exception error) when (error is VideoException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            await cancellation.CancelAsync();
            throw;
        }
    }
}
