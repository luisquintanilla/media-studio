namespace VideoProduction.Providers;

public sealed record FileDraftContent(string Prompt, string Audience, string Outcome, VideoConstraints Constraints, VideoOutline? Storyboard);
public sealed record FileDraftOperation(string OperationId, string PayloadSha256, long Revision, string Kind);
public sealed record FileDraftSubmission(string AttemptId, long ReviewedRevision, string ReviewedRequestSha256);
public sealed record FileDraft(int SchemaVersion, string Id, long Revision, string Status, FileDraftContent Content,
    FileDraftOperation[] Operations, FileDraftSubmission? Submission, DateTimeOffset UpdatedAt, string? SourceDraftId = null);
public sealed record FileDraftStart(int SchemaVersion, string OperationId, FileDraftContent Content);
public sealed record FileDraftEdit(int SchemaVersion, string DraftId, long Revision, string OperationId, string Kind,
    string? Value = null, int? Number = null, VideoOutline? Storyboard = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    FileDraftContent? Content = null);
public sealed record FileDraftSnapshotCommand(int SchemaVersion, string DraftId, long Revision);
public sealed record FileDraftFork(int SchemaVersion, string DraftId, long Revision, string OperationId);

public sealed class FileDraftStore(FileProductionStore store)
{
    private static void Id(string id) => Guard.Require(
        System.Text.RegularExpressions.Regex.IsMatch(id, @"\Adraft-[a-f0-9]{32}\z"),
        "draft.id", "Expected a new-format draft ID, not a legacy binding or file path.");
    private static void Operation(string id) => Guard.Require(id.Length is >= 1 and <= 128
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'),
        "draft.operation", "Use a bounded stable ASCII operation identity.");
    private string PathFor(string id) { Id(id); return store.PathFor("drafts/" + id + ".json"); }
    private FileStream Lock(string id) { Id(id); return FileTransactions.Lock(store.PathFor("drafts/" + id + ".lock")); }
    private static string SubmissionKey(string id, long revision) => $"{id}-r{revision}";

    private static void Validate(FileDraftContent content)
    {
        if (content is null || content.Constraints is null)
            throw new VideoException("draft.fields", "Complete draft content and constraints are required.");
        Guard.Require(content.Audience is { Length: <= 2000 } && content.Outcome is { Length: <= 2000 }
            && !content.Audience.Contains('\0') && !content.Outcome.Contains('\0'),
            "draft.fields", "Audience/outcome must be explicit text of at most2000characters without NUL.");
        VideoJobs.ValidateSubmission(content.Prompt, "draft-validation", content.Constraints, content.Storyboard);
    }

    private FileDraft Load(string id)
    {
        var path = PathFor(id);
        Guard.Require(File.Exists(path), "draft.missing", "No new-format draft with that ID exists.");
        var draft = Json.Read<FileDraft>(path);
        Guard.Require(draft.SchemaVersion == 1 && draft.Id == id && draft.Revision >= 1
            && draft.Status is "draft" or "review" or "submitted"
            && draft.Operations.Length is >= 1 and <= 513 && draft.Revision == draft.Operations.Length
            && draft.Operations.Select((operation, index) => operation.Revision == index + 1).All(value => value)
            && draft.Operations.Select(o => o.OperationId).Distinct(StringComparer.Ordinal).Count() == draft.Operations.Length
            && draft.Operations.All(o => Files.ValidHash(o.PayloadSha256) && o.Revision is >= 1 && o.Revision <= draft.Revision)
            && (draft.Submission is null
                ? draft.Status == (draft.Content.Storyboard is null ? "draft" : "review")
                : draft.Status == "submitted" && draft.Content.Storyboard is not null
                    && draft.Submission.ReviewedRevision == draft.Revision - 1
                    && draft.Operations[^1].Kind == "submit"),
            "draft.state", "Invalid versioned draft state or operation ledger.");
        Validate(draft.Content);
        return draft;
    }
    private FileDraft Save(FileDraft draft) { Json.Write(PathFor(draft.Id), draft); return draft; }
    private FileDraft Reconcile(FileDraft draft)
    {
        if (draft.Submission is { } submission)
        {
            var stored = store.Lookup(SubmissionKey(draft.Id, submission.ReviewedRevision));
            Guard.Require(stored is not null && stored.Id == submission.AttemptId
                && stored.ReviewedRequestSha256 == submission.ReviewedRequestSha256
                && store.Reviewed(stored).Draft == new DraftSnapshot(draft.Id, submission.ReviewedRevision, Files.Fingerprint(draft.Content)),
                "draft.admission", "Submitted draft/index/saved-content identity changed.");
            return draft;
        }
        if (store.Lookup(SubmissionKey(draft.Id, draft.Revision)) is not { } accepted) return draft;
        var request = store.Reviewed(accepted);
        Guard.Require(request.Draft == new DraftSnapshot(draft.Id, draft.Revision, Files.Fingerprint(draft.Content)),
            "draft.admission", "An admission key exists but does not bind this exact saved draft snapshot.");
        return Submit(draft, accepted);
    }
    private FileDraft Submit(FileDraft draft, FileAttempt attempt) => Save(draft with {
        Revision = draft.Revision + 1,
        Status = "submitted",
        UpdatedAt = DateTimeOffset.UtcNow,
        Submission = new(attempt.Id, draft.Revision, attempt.ReviewedRequestSha256),
        Operations = [.. draft.Operations, new("admit-" + attempt.Id, attempt.ReviewedRequestSha256, draft.Revision + 1, "submit")]
    });

    public FileDraft Start(FileDraftStart command)
    {
        Guard.Require(command.SchemaVersion == 1, "draft.version", "Expected explicit file draft schemaVersion1.");
        Operation(command.OperationId); Validate(command.Content);
        var id = "draft-" + Files.HashText(command.OperationId)[..32];
        using var admission = store.Admission();
        using var transaction = Lock(id);
        var digest = Files.Fingerprint(command);
        if (File.Exists(PathFor(id)))
        {
            var existing = Load(id);
            Guard.Require(existing.Operations[0] == new FileDraftOperation(command.OperationId, digest, 1, "start"),
                "draft.operation-conflict", "This start identity already binds different content.");
            return Reconcile(existing);
        }
        return Save(new(1, id, 1, command.Content.Storyboard is null ? "draft" : "review", command.Content,
            [new(command.OperationId, digest, 1, "start")], null, DateTimeOffset.UtcNow));
    }

    public FileDraft Read(string id)
    {
        using var admission = store.Admission();
        using var transaction = Lock(id);
        return Reconcile(Load(id));
    }

    public FileDraft Edit(FileDraftEdit command)
    {
        Guard.Require(command.SchemaVersion == 1, "draft.version", "Expected explicit file draft schemaVersion1.");
        Operation(command.OperationId);
        using var admission = store.Admission();
        using var transaction = Lock(command.DraftId);
        var draft = Reconcile(Load(command.DraftId));
        var digest = Files.Fingerprint(command);
        if (draft.Operations.SingleOrDefault(o => o.OperationId == command.OperationId) is { } previous)
        {
            Guard.Require(previous.PayloadSha256 == digest && previous.Kind == command.Kind,
                "draft.operation-conflict", "An operation identity cannot be reused with a different payload.");
            return draft;
        }
        Guard.Require(draft.Revision == command.Revision, "draft.revision", "Stale draft revision; read and reconcile before editing.");
        Guard.Require(draft.Submission is null, "draft.immutable", "An accepted submission is immutable; explicitly fork a new review.");
        Guard.Require(draft.Operations.Length < 512, "draft.operations", "Draft reached its bounded operation ledger.");
        var content = draft.Content;
        Guard.Require(command.Kind switch {
            "set-prompt" or "set-audience" or "set-outcome" => command.Value is not null && command.Number is null && command.Storyboard is null && command.Content is null,
            "set-duration-seconds" or "set-scene-limit" => command.Number is not null && command.Value is null && command.Storyboard is null && command.Content is null,
            "replace-storyboard" => command.Storyboard is not null && command.Value is null && command.Number is null && command.Content is null,
            "replace-content" => command.Content is not null && command.Value is null && command.Number is null && command.Storyboard is null,
            _ => false
        }, "draft.edit", "Expected exactly the typed payload for this supported draft edit.");
        content = command.Kind switch {
            "set-prompt" => content with {
                Prompt = command.Value!,
                Storyboard = command.Value == content.Prompt ? content.Storyboard : null
            },
            "set-audience" => content with { Audience = command.Value! },
            "set-outcome" => content with { Outcome = command.Value! },
            "set-duration-seconds" => content with { Constraints = content.Constraints with { MaxDurationSeconds = command.Number!.Value } },
            "set-scene-limit" => content with { Constraints = content.Constraints with { MaxScenes = command.Number!.Value } },
            "replace-storyboard" => content with { Storyboard = command.Storyboard },
            "replace-content" => command.Content!,
            _ => throw new VideoException("draft.edit", "Unsupported draft operation.")
        };
        Validate(content);
        return Save(draft with {
            Content = content,
            Revision = draft.Revision + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = content.Storyboard is null ? "draft" : "review",
            Operations = [.. draft.Operations, new(command.OperationId, digest, draft.Revision + 1, command.Kind)]
        });
    }

    public ReviewedVideoRequest Snapshot(FileDraftSnapshotCommand command)
    {
        Guard.Require(command.SchemaVersion == 1, "draft.version", "Expected explicit file draft schemaVersion1.");
        using var admission = store.Admission();
        using var transaction = Lock(command.DraftId);
        var draft = Reconcile(Load(command.DraftId));
        if (draft.Submission is { } submitted && command.Revision == submitted.ReviewedRevision)
            return store.Reviewed(store.Lookup(SubmissionKey(draft.Id, submitted.ReviewedRevision))
                ?? throw new VideoException("draft.admission", "Submitted draft has no durable accepted attempt."));
        Guard.Require(draft.Revision == command.Revision && draft.Status == "review"
            && draft.Submission is null && draft.Content.Storyboard is not null,
            "draft.review", "Snapshot requires this exact saved reviewed revision; it does not authorize or start production.");
        return Snapshot(draft);
    }
    private static ReviewedVideoRequest Snapshot(FileDraft draft) => new(1, SubmissionKey(draft.Id, draft.Revision),
        new(draft.Content.Prompt, draft.Content.Constraints, draft.Content.Storyboard),
        new(draft.Id, draft.Revision, Files.Fingerprint(draft.Content)));

    internal void Admit(ReviewedVideoRequest request, FileAttempt attempt, Action commitAdmission)
    {
        if (request.Draft is not { } snapshot) { commitAdmission(); return; }
        using var transaction = Lock(snapshot.DraftId);
        var draft = Reconcile(Load(snapshot.DraftId));
        Guard.Require(draft.Revision == snapshot.Revision && draft.Status == "review" && draft.Submission is null
            && Files.Fingerprint(Snapshot(draft)) == Files.Fingerprint(request),
            "draft.revision", "Reviewed snapshot no longer matches the saved draft; no new attempt was accepted.");
        commitAdmission();
        Submit(draft, attempt);
    }

    public FileDraft Fork(FileDraftFork command)
    {
        Guard.Require(command.SchemaVersion == 1, "draft.version", "Expected explicit file draft schemaVersion1.");
        Operation(command.OperationId);
        using var admission = store.Admission();
        using var sourceTransaction = Lock(command.DraftId);
        var source = Reconcile(Load(command.DraftId));
        var id = "draft-" + Files.HashText(command.OperationId)[..32];
        Guard.Require(id != source.Id, "draft.operation-conflict", "Fork needs a new stable operation identity.");
        using var targetTransaction = Lock(id);
        var digest = Files.Fingerprint(command);
        if (File.Exists(PathFor(id)))
        {
            var existing = Load(id);
            Guard.Require(existing.Operations[0] == new FileDraftOperation(command.OperationId, digest, 1, "fork"),
                "draft.operation-conflict", "This fork operation already binds a different source.");
            return Reconcile(existing);
        }
        Guard.Require(source.Revision == command.Revision && source.Submission is not null,
            "draft.immutable", "Fork only an exact immutable submitted revision; it never retries production.");
        store.ReconcileIfIdle();
        var accepted = store.Lookup(SubmissionKey(source.Id, source.Submission!.ReviewedRevision))
            ?? throw new VideoException("draft.admission", "Submitted source has no durable accepted attempt.");
        Guard.Require(FileProductionStore.Terminal(accepted.Status), "draft.immutable",
            "Wait for terminal production before intentionally creating a separate review attempt.");
        return Save(new(1, id, 1, "review", source.Content,
            [new(command.OperationId, digest, 1, "fork")], null, DateTimeOffset.UtcNow, source.Id));
    }
}
