using System.Text.Json;
using System.Text.Json.Serialization;
using MediaStudio.Audio;

namespace VideoProduction.Providers;

public sealed record MediaDirection(int TargetSeconds = 60, string Audience = "Auto", string Depth = "Auto");
public sealed record MediaDraftSource(string Id, string OriginalSha256, string ProjectionSha256, string ManifestSha256);
public sealed record MediaCitation(string ItemId, string SourceId, string SectionId);
public sealed record MediaDraftFrame(MediaProjectContent Content, string[] SceneIds, string? SelectedItemId,
    MediaDirection Direction, Dictionary<string, int> ItemTargets, MediaDraftSource[] Sources, MediaCitation[] Citations);
public sealed record MediaDraftMessage(string Role, string Text, DateTimeOffset At);
public sealed record MediaNarrationEstimate(string Output, int Words, int Seconds);
[method: JsonConstructor]
public sealed record MediaDraftState(int SchemaVersion, string DraftId, string? ProjectId, int BaseRevision,
    long Version, string ContentSha256, MediaDraftFrame Frame, string[] AppliedOperationIds,
    MediaDraftMessage[] Messages, string? ActiveRunId = null, string? ThreadId = null, string RunStatus = "idle",
    string? SelectedItemId = null)
{
    public MediaDraftState() : this(1, "", null, 0, 0, "", MediaDraftRules.FromContent(
        new("Untitled explanation", "", "", "", MediaOutput.Video, 300, null, null)), [], [])
    { }
}
public sealed record MediaDraftJournal(int SchemaVersion, MediaDraftState State,
    MediaDraftFrame[] Undo, MediaDraftFrame[] Redo, string? EditKey = null, DateTimeOffset? EditedAt = null);
public sealed record MediaDraftTransaction(string OperationId, string DraftId, string ThreadId, string RunId,
    long BaseVersion, string BaseSha256, MediaDraftOperation[] Operations, string Summary);

[JsonConverter(typeof(JsonStringEnumConverter<MediaDraftOperationKind>))]
public enum MediaDraftOperationKind
{
    AddScene, DeleteScene, MoveScene, UpdateScene, AddPart, DeletePart, MovePart,
    UpdatePart, ReplaceVideo, ReplaceAudio, ReviseDirection, SetItemTarget, UpdateBrief
}
public sealed record MediaDraftOperation(MediaDraftOperationKind Kind, string? ItemId = null,
    string? BeforeId = null, ProposedScene? Scene = null, AudioScriptPart? Part = null,
    VideoOutline? Storyboard = null, string[]? SceneIds = null, AudioScriptPart[]? Parts = null,
    MediaDirection? Direction = null, int? TargetSeconds = null, string? Title = null, string? Prompt = null,
    string? Audience = null, string? Outcome = null, MediaCitation[]? Citations = null);

public static class MediaDraftRules
{
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(Json.Text(value), Json.Options)!;
    public static bool ValidDraftId(string id) => System.Text.RegularExpressions.Regex.IsMatch(id, @"\Adraft-[a-f0-9]{32}\z");
    public static bool ValidItemId(string? id) => id is not null && Files.ValidId(id);
    public static MediaDraftFrame FromContent(MediaProjectContent content) => new(Copy(content),
        (content.Storyboard?.Scenes ?? []).Select(_ => "scene-" + Guid.NewGuid().ToString("N")).ToArray(),
        null, new(), [], [], []);
    public static void Validate(MediaDraftFrame frame)
    {
        var c = frame.Content;
        ArgumentNullException.ThrowIfNull(c);
        Guard.Require(Enum.IsDefined(c.Outputs) && c.Title.Length <= 100 && c.Prompt.Length <= 6000
            && c.Audience.Length <= 2000 && c.Outcome.Length <= 2000 && c.MaximumSeconds is >= 1 and <= 1800,
            "draft.content", "Draft fields exceed supported limits.");
        Guard.Require(frame.Direction.TargetSeconds is >= 1 and <= 1800
            && frame.Direction.Audience.Length <= 2000 && frame.Direction.Depth.Length <= 100,
            "draft.direction", "Choose a creative duration target within1..1800seconds.");
        var scenes = c.Storyboard?.Scenes ?? [];
        var parts = c.AudioScript ?? [];
        Guard.Require(scenes.Length <= 12 && parts.Length <= 12 && frame.SceneIds.Length == scenes.Length
            && frame.SceneIds.All(ValidItemId) && frame.SceneIds.Distinct().Count() == scenes.Length
            && parts.All(p => ValidItemId(p.Id) && p.Text.Length <= 8000 && p.PauseAfterSamples >= 0)
            && parts.Select(p => p.Id).Distinct().Count() == parts.Length && parts.Sum(p => p.Text.Length) <= 32000
            && scenes.All(s => Enum.IsDefined(s.Kind) && s.Title.Length <= 100 && s.Text.Length <= 5000 && s.Script.Length <= 3000
                && (s.IllustrationPrompt is null || s.IllustrationPrompt.Length is > 0 and <= 2000
                    && !string.IsNullOrWhiteSpace(s.IllustrationPrompt) && !s.IllustrationPrompt.Contains('\0')
                    && s.Visual is null && s.Motion is null)),
            "draft.items", "Draft needs unique bounded scene/part identities and inert supported content.");
        foreach (var scene in scenes)
            MotionVisuals.Validate(scene.Motion, scene.Kind == TemplateKind.Diagram ? SceneKind.Diagram : SceneKind.Title,
                scene.Visual is not null || scene.IllustrationPrompt is not null);
        Guard.Require((c.Outputs != MediaOutput.Audio || scenes.Length == 0)
            && (c.Outputs != MediaOutput.Video || parts.Length == 0), "draft.outputs", "Choose output intent explicitly.");
        var ids = frame.SceneIds.Concat(parts.Select(p => p.Id)).ToHashSet(StringComparer.Ordinal);
        Guard.Require(ids.Count == scenes.Length + parts.Length, "draft.items",
            "Scene and audio-part identities must be unique across their shared namespace.");
        Guard.Require(frame.ItemTargets.Count <= 24 && frame.ItemTargets.All(p => ids.Contains(p.Key) && p.Value is >= 1 and <= 1800)
            && frame.Sources.Length <= 5 && frame.Sources.All(s => Files.ValidId(s.Id) && Files.ValidHash(s.OriginalSha256)
                && Files.ValidHash(s.ProjectionSha256) && Files.ValidHash(s.ManifestSha256))
            && frame.Sources.Select(s => s.Id).Distinct().Count() == frame.Sources.Length
            && frame.Citations.Length <= 200 && frame.Citations.All(r => ids.Contains(r.ItemId)
                && frame.Sources.Any(s => s.Id == r.SourceId) && Files.ValidId(r.SectionId)),
            "draft.sources", "Targets and citations must bind known items and supplied source identities.");
        Guard.Require(frame.SelectedItemId is null || ids.Contains(frame.SelectedItemId),
            "draft.selection", "Selection must name an existing item.");
    }
    public static MediaDraftFrame Apply(MediaDraftFrame original, MediaDraftOperation[] operations)
    {
        Guard.Require(operations is { Length: > 0 and <= 24 }, "draft.operations", "A transaction needs1..24typed draft operations.");
        var frame = Copy(original);
        foreach (var op in operations)
        {
            var content = frame.Content;
            var scenes = (content.Storyboard?.Scenes ?? []).ToList();
            var sceneIds = frame.SceneIds.ToList();
            var parts = (content.AudioScript ?? []).ToList();
            var sceneIndex = op.ItemId is null ? -1 : sceneIds.IndexOf(op.ItemId);
            var partIndex = op.ItemId is null ? -1 : parts.FindIndex(p => p.Id == op.ItemId);
            switch (op.Kind)
            {
                case MediaDraftOperationKind.AddScene:
                    Guard.Require(ValidItemId(op.ItemId), "draft.scene-id", "Adding a scene requires a new stable scene ID.");
                    Guard.Require(content.Outputs != MediaOutput.Audio && !sceneIds.Contains(op.ItemId!)
                        && op.Scene is not null, "draft.operation", "Add an explicitly identified supported video scene.");
                    scenes.Add(op.Scene!); sceneIds.Add(op.ItemId!); break;
                case MediaDraftOperationKind.DeleteScene:
                    Guard.Require(sceneIndex >= 0, "draft.operation", "Choose an existing scene.");
                    scenes.RemoveAt(sceneIndex); sceneIds.RemoveAt(sceneIndex); break;
                case MediaDraftOperationKind.UpdateScene:
                    Guard.Require(sceneIndex >= 0 && op.Scene is not null, "draft.operation", "Update an existing scene by stable ID.");
                    scenes[sceneIndex] = op.Scene!; break;
                case MediaDraftOperationKind.MoveScene:
                    Guard.Require(sceneIndex >= 0 && (op.BeforeId is null || sceneIds.Contains(op.BeforeId)), "draft.operation", "Move relative to an existing scene.");
                    var scene = scenes[sceneIndex]; var id = sceneIds[sceneIndex];
                    scenes.RemoveAt(sceneIndex); sceneIds.RemoveAt(sceneIndex);
                    var insert = op.BeforeId is null ? scenes.Count : sceneIds.IndexOf(op.BeforeId);
                    Guard.Require(insert >= 0, "draft.operation", "Cannot move an item before itself.");
                    scenes.Insert(insert, scene); sceneIds.Insert(insert, id); break;
                case MediaDraftOperationKind.AddPart:
                    Guard.Require(content.Outputs != MediaOutput.Video, "draft.part-output",
                        "This project is set to Video. Audio parts require Audio or Both selected in Create; no output was changed.");
                    Guard.Require(op.Part is not null, "draft.part-missing", "AddPart requires a complete part object.");
                    Guard.Require(ValidItemId(op.Part!.Id), "draft.part-id",
                        "Audio part IDs must start with a lowercase letter and contain only lowercase letters, digits or hyphens (maximum64characters).");
                    Guard.Require(parts.All(p => p.Id != op.Part.Id), "draft.part-duplicate",
                        "That audio part already exists. Update it using its stable ID, or add a part with a new ID.");
                    parts.Add(op.Part!); break;
                case MediaDraftOperationKind.DeletePart:
                    Guard.Require(partIndex >= 0, "draft.operation", "Choose an existing audio part.");
                    parts.RemoveAt(partIndex); break;
                case MediaDraftOperationKind.UpdatePart:
                    Guard.Require(partIndex >= 0 && op.Part is not null && op.Part.Id == op.ItemId,
                        "draft.operation", "Update an existing part by stable ID.");
                    parts[partIndex] = op.Part!; break;
                case MediaDraftOperationKind.MovePart:
                    Guard.Require(partIndex >= 0 && (op.BeforeId is null || parts.Any(p => p.Id == op.BeforeId)),
                        "draft.operation", "Move relative to an existing audio part.");
                    var part = parts[partIndex]; parts.RemoveAt(partIndex);
                    var partInsert = op.BeforeId is null ? parts.Count : parts.FindIndex(p => p.Id == op.BeforeId);
                    Guard.Require(partInsert >= 0, "draft.operation", "Cannot move an item before itself.");
                    parts.Insert(partInsert, part); break;
                case MediaDraftOperationKind.ReplaceVideo:
                    Guard.Require(content.Outputs != MediaOutput.Audio && op.Storyboard is not null && op.SceneIds is not null,
                        "draft.operation", "Replace only the explicitly selected video storyboard.");
                    scenes = op.Storyboard!.Scenes.ToList(); sceneIds = op.SceneIds!.ToList(); break;
                case MediaDraftOperationKind.ReplaceAudio:
                    Guard.Require(content.Outputs != MediaOutput.Video && op.Parts is not null,
                        "draft.operation", "Replace only the explicitly selected independent audio script.");
                    parts = op.Parts!.ToList(); break;
                case MediaDraftOperationKind.ReviseDirection:
                    Guard.Require(op.Direction is not null, "draft.operation", "Creative direction is required.");
                    frame = frame with { Direction = op.Direction! }; break;
                case MediaDraftOperationKind.SetItemTarget:
                    Guard.Require(op.ItemId is not null && (sceneIndex >= 0 || partIndex >= 0) && op.TargetSeconds is not null,
                        "draft.operation", "A target needs an existing stable item.");
                    frame.ItemTargets[op.ItemId!] = op.TargetSeconds!.Value; break;
                case MediaDraftOperationKind.UpdateBrief:
                    content = content with {
                        Title = op.Title ?? content.Title,
                        Prompt = op.Prompt ?? content.Prompt,
                        Audience = op.Audience ?? content.Audience,
                        Outcome = op.Outcome ?? content.Outcome
                    }; break;
                default: throw new VideoException("draft.operation", "Unsupported draft operation.");
            }
            var remaining = sceneIds.Concat(parts.Select(p => p.Id)).ToHashSet();
            frame = frame with {
                Content = content with {
                    Storyboard = scenes.Count == 0 ? null : new(content.Title, scenes.ToArray()),
                    AudioScript = parts.Count == 0 ? null : parts.ToArray()
                },
                SceneIds = sceneIds.ToArray(),
                SelectedItemId = frame.SelectedItemId is { } selected && remaining.Contains(selected) ? selected : remaining.FirstOrDefault(),
                ItemTargets = frame.ItemTargets.Where(p => remaining.Contains(p.Key)).ToDictionary(),
                Citations = op.Citations ?? frame.Citations.Where(c => remaining.Contains(c.ItemId)).ToArray()
            };
        }
        Validate(frame);
        return frame;
    }
    public static int EstimateSeconds(string script) => Math.Max(1, (int)Math.Ceiling(script.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries).Length * 60d / 150));
    public static MediaNarrationEstimate[] EstimateNarration(MediaProjectContent content)
    {
        var estimates = new List<MediaNarrationEstimate>();
        void Add(string output, IEnumerable<string> scripts)
        {
            var words = scripts.Sum(s => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
            estimates.Add(new(output, words, (int)Math.Ceiling(words * 60d / 150)));
        }
        if (content.Outputs != MediaOutput.Video) Add("Audio", content.AudioScript?.Select(p => p.Text) ?? []);
        if (content.Outputs != MediaOutput.Audio) Add("Video narration", content.Storyboard?.Scenes.Select(s => s.Script) ?? []);
        return estimates.ToArray();
    }
    public static MediaProjectContent ProductionContent(MediaDraftFrame frame)
    {
        var content = Copy(frame.Content);
        if (content.Storyboard is { } outline) content = content with { Storyboard = outline with { Title = content.Title } };
        MediaProjectStore.Validate(content);
        return content;
    }
}

public sealed class MediaDraftStore
{
    private readonly FileProductionStore production;
    public MediaDraftStore(FileProductionStore production) { this.production = production; Directory.CreateDirectory(production.PathFor("media-drafts")); }
    private string PathFor(string id)
    {
        Guard.Require(MediaDraftRules.ValidDraftId(id), "draft.id", "Expected a private draft identity.");
        return production.PathFor($"media-drafts/{id}.json");
    }
    public MediaDraftState New(MediaProjectContent content, MediaProject? saved = null)
    {
        using var held = production.Admission();
        var frame = MediaDraftRules.FromContent(content);
        string? selection = null;
        if (saved is not null)
        {
            var sidecar = production.PathFor($"projects/{saved.Id}/r{saved.Revision}.authoring.json");
            if (File.Exists(sidecar))
            {
                var metadata = Json.Read<MediaDraftState>(sidecar);
                Guard.Require(metadata.SchemaVersion == 1 && metadata.ProjectId == saved.Id && metadata.BaseRevision == saved.Revision
                    && Files.Fingerprint(metadata.Frame) == metadata.ContentSha256
                    && Files.Fingerprint(metadata.Frame.Content) == Files.Fingerprint(saved.Content)
                    && Files.Fingerprint(content) == Files.Fingerprint(saved.Content),
                    "draft.saved", "Saved authoring metadata does not bind its immutable production revision.");
                frame = MediaDraftRules.Copy(metadata.Frame); selection = metadata.SelectedItemId;
            }
        }
        MediaDraftRules.Validate(frame);
        var state = new MediaDraftState(1, "draft-" + Guid.NewGuid().ToString("N"), saved?.Id, saved?.Revision ?? 0,
            1, Files.Fingerprint(frame), frame, [], [], SelectedItemId: selection);
        Write(new(1, state, [], []));
        return state;
    }
    private MediaDraftJournal ReadCore(string id)
    {
        var journal = Json.Read<MediaDraftJournal>(PathFor(id));
        var state = journal.State;
        Guard.Require(journal.SchemaVersion == 1 && state.SchemaVersion == 1 && state.DraftId == id && state.Version > 0
            && state.BaseRevision >= 0 && state.AppliedOperationIds.Length <= 256 && state.Messages.Length <= 80
            && journal.Undo.Length <= 32 && journal.Redo.Length <= 32 && Files.Fingerprint(state.Frame) == state.ContentSha256
            && (state.SelectedItemId is null || state.Frame.SceneIds.Contains(state.SelectedItemId)
                || state.Frame.Content.AudioScript?.Any(p => p.Id == state.SelectedItemId) == true),
            "draft.journal", "Invalid editable draft journal; no automatic repair or production replay.");
        MediaDraftRules.Validate(state.Frame);
        return journal;
    }
    private void Write(MediaDraftJournal journal) => Json.Write(PathFor(journal.State.DraftId), journal);
    public MediaDraftState Read(string id)
    {
        using var held = production.Admission();
        return MediaDraftRules.Copy(ReadCore(id).State);
    }
    public MediaDraftState[] List()
    {
        using var held = production.Admission();
        var paths = Directory.EnumerateFiles(production.PathFor("media-drafts"), "draft-*.json").Take(257).ToArray();
        Guard.Require(paths.Length <= 256, "draft.bound", "Editable draft history exceeds the supported demo limit.");
        return paths.Select(path => ReadCore(System.IO.Path.GetFileNameWithoutExtension(path)).State).ToArray();
    }
    public MediaDraftState? Active()
    {
        using var held = production.Admission();
        var path = production.PathFor("active-media-draft.json");
        if (!File.Exists(path)) return null;
        var pointer = Json.Read<Dictionary<string, string>>(path);
        Guard.Require(pointer.TryGetValue("draftId", out var id) && MediaDraftRules.ValidDraftId(id),
            "draft.active", "The current draft pointer is invalid.");
        return ReadCore(id!).State;
    }
    public void Activate(string id)
    {
        using var held = production.Admission();
        ReadCore(id);
        Json.Write(production.PathFor("active-media-draft.json"), new Dictionary<string, string> { ["draftId"] = id });
    }
    public MediaDraftState Select(MediaDraftState expected, string id)
    {
        using var held = production.Admission();
        var current = Current(expected);
        Guard.Require(current.State.Frame.SceneIds.Contains(id)
            || current.State.Frame.Content.AudioScript?.Any(p => p.Id == id) == true,
            "draft.selection", "Select an existing scene or script part.");
        var state = current.State with { SelectedItemId = id };
        Write(current with { State = state }); return state;
    }
    private MediaDraftJournal Current(MediaDraftState expected)
    {
        var journal = ReadCore(expected.DraftId);
        Guard.Require(journal.State.Version == expected.Version && journal.State.ContentSha256 == expected.ContentSha256
            && journal.State.ProjectId == expected.ProjectId && journal.State.BaseRevision == expected.BaseRevision,
            "draft.stale", "The draft changed; reload its current version before editing.");
        return journal;
    }
    public MediaDraftState Edit(MediaDraftState expected, MediaDraftFrame frame, string summary, string? operationId = null,
        string? coalesceKey = null)
    {
        frame = MediaDraftRules.Copy(frame);
        MediaDraftRules.Validate(frame);
        using var held = production.Admission();
        var current = Current(expected);
        Guard.Require(summary.Length is > 0 and <= 500, "draft.summary", "A concise change summary is required.");
        var now = DateTimeOffset.UtcNow;
        Guard.Require(coalesceKey is null || coalesceKey.Length is > 0 and <= 160,
            "draft.edit-key", "A bounded field identity is required for typing history.");
        var coalesced = operationId is null && coalesceKey is not null && current.EditKey == coalesceKey
            && current.EditedAt is { } at && now >= at && now - at < TimeSpan.FromSeconds(1.5)
            && current.Undo.Length > 0 && current.Redo.Length == 0 && current.State.Messages.LastOrDefault()?.Role == "change";
        var messages = coalesced ? current.State.Messages[..^1] : current.State.Messages;
        var state = current.State with {
            Frame = frame,
            Version = checked(current.State.Version + 1),
            ContentSha256 = Files.Fingerprint(frame),
            AppliedOperationIds = operationId is null ? current.State.AppliedOperationIds
                : current.State.AppliedOperationIds.Append(operationId).TakeLast(256).ToArray(),
            Messages = messages.Append(new("change", summary, now)).TakeLast(80).ToArray()
        };
        if (state.SelectedItemId is { } selected && !frame.SceneIds.Contains(selected)
            && frame.Content.AudioScript?.Any(p => p.Id == selected) != true) state = state with { SelectedItemId = null };
        Write(current with {
            State = state,
            Undo = coalesced ? current.Undo : current.Undo.Append(current.State.Frame).TakeLast(32).ToArray(),
            Redo = [],
            EditKey = operationId is null ? coalesceKey : null,
            EditedAt = now
        });
        return state;
    }
    public MediaDraftState BeginRun(MediaDraftState expected, string runId, string threadId, string request)
    {
        using var held = production.Admission();
        var current = Current(expected);
        Guard.Require(current.State.ActiveRunId is null && Files.ValidId(runId) && Files.ValidId(threadId)
            && request.Length is > 0 and <= 8000, "draft.run", "An explicit bound authoring run is required.");
        var state = current.State with {
            ActiveRunId = runId,
            ThreadId = threadId,
            RunStatus = "running",
            Messages = current.State.Messages.Append(new("user", request, DateTimeOffset.UtcNow)).TakeLast(80).ToArray()
        };
        Write(current with { State = state, EditKey = null, EditedAt = null });
        return state;
    }
    public MediaDraftState Apply(MediaDraftState expected, MediaDraftTransaction transaction)
    {
        var current = Read(expected.DraftId);
        Guard.Require(current.Version == expected.Version && current.ContentSha256 == expected.ContentSha256
            && current.ActiveRunId is not null && transaction.RunId == current.ActiveRunId
            && transaction.ThreadId == current.ThreadId && transaction.DraftId == current.DraftId
            && transaction.BaseVersion == current.Version && transaction.BaseSha256 == current.ContentSha256
            && Files.ValidId(transaction.OperationId) && !current.AppliedOperationIds.Contains(transaction.OperationId)
            && current.AppliedOperationIds.Length < 256,
            "draft.transaction", "Rejected stale, foreign, replayed or unbound draft transaction.");
        return Edit(current, MediaDraftRules.Apply(current.Frame, transaction.Operations), transaction.Summary, transaction.OperationId);
    }
    public MediaDraftState FinishRun(string id, string runId, string status)
    {
        using var held = production.Admission();
        var current = ReadCore(id);
        Guard.Require(current.State.ActiveRunId == runId && status is "completed" or "cancelled" or "failed" or "interrupted",
            "draft.run", "Finish only the bound active run.");
        var state = current.State with { ActiveRunId = null, RunStatus = status };
        Write(current with { State = state }); return state;
    }
    public MediaDraftState Recover(string id)
    {
        var state = Read(id);
        return state.ActiveRunId is { } run ? FinishRun(id, run, "interrupted") : state;
    }
    public MediaDraftState Undo(MediaDraftState expected, bool redo = false)
    {
        using var held = production.Admission();
        var current = Current(expected);
        Guard.Require(current.State.ActiveRunId is null, "draft.busy", "Cancel or finish authoring before Undo.");
        var history = redo ? current.Redo : current.Undo;
        Guard.Require(history.Length > 0, "draft.undo", redo ? "Nothing to redo." : "Nothing to undo.");
        var frame = history[^1];
        var state = current.State with {
            Frame = frame,
            Version = current.State.Version + 1,
            ContentSha256 = Files.Fingerprint(frame),
            Messages = current.State.Messages.Append(new("change", redo ? "Redid changes" : "Undid changes", DateTimeOffset.UtcNow)).TakeLast(80).ToArray()
        };
        if (state.SelectedItemId is { } selected && !frame.SceneIds.Contains(selected)
            && frame.Content.AudioScript?.Any(p => p.Id == selected) != true) state = state with { SelectedItemId = null };
        Write(current with {
            State = state,
            Undo = redo ? current.Undo.Append(current.State.Frame).TakeLast(32).ToArray() : history[..^1],
            Redo = redo ? history[..^1] : current.Redo.Append(current.State.Frame).TakeLast(32).ToArray(),
            EditKey = null,
            EditedAt = null
        });
        return state;
    }
    public MediaDraftState BindSaved(MediaDraftState expected, MediaProject saved)
    {
        using var held = production.Admission();
        var current = Current(expected);
        Guard.Require(Files.Fingerprint(saved.Content) == Files.Fingerprint(current.State.Frame.Content),
            "draft.saved", "Only the exact flushed draft can bind a saved production revision.");
        var state = current.State with { ProjectId = saved.Id, BaseRevision = saved.Revision };
        var sidecar = production.PathFor($"projects/{saved.Id}/r{saved.Revision}.authoring.json");
        Guard.Require(!File.Exists(sidecar), "draft.saved", "Submitted authoring metadata is immutable.");
        Json.Write(sidecar, state);
        Write(current with { State = state, EditKey = null, EditedAt = null });
        return state;
    }
}
