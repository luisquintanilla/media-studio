namespace VideoStudio.Contracts;

public sealed record StudioQuestion(string Id, string Text, string[] Choices);
public sealed record StudioAnswer(string QuestionId, string Question, string Answer);
public sealed record StudioQuickPick(string Id, string Label, string Selected, string[] Options, bool IsExplicit = false);
public sealed record StudioDirection(string Summary, StudioQuickPick[] Picks,
    int? DurationSeconds = null, int? SceneLimit = null);
public sealed record SkillReceipt(string Path, string Sha256, int Bytes);
public sealed record StudioAuthoringAction(string Id, string ViewId, string Trigger);
public sealed record StudioAuthoringCommand(string BriefId, long Revision, StudioAuthoringAction Action,
    string? QuestionId = null, string? Answer = null);
public sealed record AuthoringReceipt(string Source, string SessionId, string? ConfiguredModel,
    string? ReturnedModel, string GuidanceIdentity, SkillReceipt[] Skills, string[] AppliedGuidance,
    string Rationale, DateTimeOffset CreatedAt, StudioAuthoringAction? Action = null);
public sealed record StudioSubmission(string IdempotencyKey, string Prompt, int MaxDurationSeconds,
    int MaxScenes, string OutlineJson, string? JobId = null);
public sealed record StudioBrief(string Id, long Revision, string Prompt, string Audience, string Outcome,
    int MaxDurationSeconds, int MaxScenes, string Status, StudioQuestion? Question, StudioAnswer[] Answers,
    string? OutlineJson, AuthoringReceipt[] Receipts, StudioSubmission? Submission, string? ErrorCode,
    DateTimeOffset UpdatedAt, StudioDirection? Direction = null, int AuthoringAttempts = 0,
    bool DurationIsExplicit = false, bool ScenesAreExplicit = false, StudioAuthoringAction[]? AuthoringActions = null,
    string? SourceBriefId = null, StudioConversation? Conversation = null);
public sealed record StudioConversation(string BindingId, string ContextLabel, string ViewId,
    string SourceBriefId, StudioOperationReceipt[] Operations);
public sealed record StudioOperationReceipt(string OperationId, string PayloadSha256, long Revision,
    string Kind, DateTimeOffset CommittedAt);
public sealed record StudioBindCommand(string ContextLabel, string ViewId, string BriefId, long Revision,
    string OperationId, bool AllowNewAttempt = false);
public sealed record StudioStateUpdate(StudioBrief Brief, string RunId, string? OperationId, string Origin);
public sealed record StudioWatch(string ViewId, string BriefId);
public sealed record StudioView(string ViewId, string BriefId, long Revision, string? BindingId);
public sealed record BriefEdit(long Revision, string Prompt, string Audience, string Outcome,
    int MaxDurationSeconds, int MaxScenes, string? OutlineJson, StudioQuickPick[]? Picks = null,
    bool? DurationIsExplicit = null, bool? ScenesAreExplicit = null);
public sealed record RevisionCommand(long Revision, StudioAuthoringAction? Action = null);
public sealed record AnswerCommand(long Revision, string QuestionId, string Answer, StudioAuthoringAction? Action = null);
public sealed record StudioArtifact(string Id, string Kind, string Sha256, string Url);
public sealed record StudioJob(string JobId, string Status, string Stage, bool CancellationRequested,
    string? ErrorCode, StudioArtifact[] Artifacts, bool HumanReviewRequired = true,
    bool AutomaticRestartResume = false, StudioFailure? Failure = null);
public sealed record StudioFailure(string Summary, StudioTranscriptDifference[] Transcripts);
public sealed record StudioTranscriptDifference(string SceneId, string SceneTitle, string Expected,
    string Observed, string EvidenceSha256);
public sealed record StudioSession(string Csrf, string AuthoringSource, string Components,
    bool GenerationConfigured, bool SearchConfigured, string? GenerationErrorCode = null);
public sealed record StudioError(string Code, string Message);
public sealed record StudioMediaItem(string Id, string Name, string Type, string Url);
public sealed record StudioMediaSearchResult(string Query, StudioMediaItem[] Items, int TotalRecordCount,
    string Ranking, string Principal);

public static class StudioDirectionChoices
{
    public static StudioDirection Select(StudioDirection direction, string id, string value)
    {
        var pick = direction.Picks.SingleOrDefault(p => p.Id == id);
        if (pick is null || !pick.Options.Contains(value))
            throw new InvalidOperationException("Choose an existing supported direction pick and one of its saved options.");
        return direction with {
            Picks = direction.Picks.Select(p => p.Id == id
            ? p with { Selected = value, IsExplicit = true } : p).ToArray()
        };
    }
}

public sealed class StudioAuthoringIntentGate
{
    private StudioAuthoringAction? pending;
    public StudioAuthoringAction Arm(string viewId, string trigger)
    {
        if (pending is not null) throw new InvalidOperationException("An authoring intent is already armed.");
        return pending = new StudioAuthoringAction(Guid.NewGuid().ToString("N"), viewId, trigger);
    }
    public void Consume(StudioAuthoringAction action)
    {
        if (pending is null || pending != action)
            throw new InvalidOperationException("Authoring requires a fresh explicit action; a missing or replayed intent cannot call the model.");
        pending = null;
    }
    public void Clear() => pending = null;
}
