using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Serialization;
using VideoProduction.Providers;

namespace MediaStudio.Agents;

// Field names and editor semantics match Studio's outline scene title/text/script fields.
public sealed record StoryboardScene(string Id, string Title, string Text, string Script);
public sealed record StoryboardProposal(string Id, long BaseRevision, string BaseFingerprint, StoryboardScene[] Scenes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FileDraftContent? Content = null);
[method: JsonConstructor]
public sealed record StoryboardState(long Revision, StoryboardScene[] Scenes, StoryboardProposal? Proposal = null,
    string DraftId = "", string Status = "review", FileDraftContent? Content = null)
{
    public StoryboardState() : this(1, StoryboardRules.Sample().Scenes) { }
}

public static class StoryboardJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
}

public static class StoryboardRules
{
    public static StoryboardState Sample() => new(1,
    [
        new("scene-1", "An idea becomes a story", "Shape the explanation.",
            "Begin with a clear idea, then shape it into a short, approachable story for the viewer."),
        new("scene-2", "Review before producing", "Human direction stays in control.",
            "Review the storyboard before explicitly choosing to produce a video.")
    ]);

    public static void Validate(StoryboardState state)
    {
        if (state.Revision < 1 || state.Scenes is not { Length: <= 12 }
            || state.Scenes.Length == 0 && (state.Content is null || state.Content.Storyboard is not null)
            || state.Scenes.Select(s => s.Id).Distinct().Count() != state.Scenes.Length
            || state.Scenes.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id)
                || string.IsNullOrWhiteSpace(s.Title) || s.Title.Length > 100
                || string.IsNullOrWhiteSpace(s.Text) || s.Text.Length > 5000
                || string.IsNullOrWhiteSpace(s.Script) || s.Script.Length > 3000))
            throw new InvalidOperationException("A valid revision and 1..12 bounded, uniquely identified scenes are required.");
        if (state.Status is not ("draft" or "review"))
            throw new InvalidOperationException("Submitted drafts are immutable; authoring and editing are disabled.");
        if (state.Content is { } content)
        {
            if (content.Constraints is null || content.Audience is null || content.Outcome is null)
                throw new InvalidOperationException("The working brief requires explicit constraints, audience and outcome.");
            VideoJobs.ValidateSubmission(content.Prompt, "hybrid-validation", content.Constraints,
                state.Scenes.Length == 0 ? null : Outline(state));
        }
    }

    public static string Fingerprint(StoryboardState state) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(state with { Proposal = null }, StoryboardJson.Options)));

    public static void ValidateProposal(StoryboardState current, StoryboardProposal proposal)
    {
        Validate(current);
        Validate(current with {
            Scenes = proposal.Scenes,
            Content = proposal.Content ?? current.Content,
            Proposal = null,
            Status = "review"
        });
        if (proposal.BaseRevision != current.Revision || proposal.BaseFingerprint != Fingerprint(current))
            throw new InvalidOperationException("Stale proposal: the editable storyboard has changed.");
        if (proposal.Content is { } content)
        {
            if (content.Storyboard is null || !Scenes(content).SequenceEqual(proposal.Scenes)
                || content.Audience is null || content.Outcome is null
                || content.Audience.Length > 2000 || content.Outcome.Length > 2000
                || content.Storyboard.Scenes.Any(s => s.IllustrationPrompt is not null))
                throw new InvalidOperationException("The proposal requires a bounded complete brief/storyboard; no external image capability is available.");
            return;
        }
        if (current.Scenes.Length == 0 || !proposal.Scenes.Skip(1).SequenceEqual(current.Scenes.Skip(1))
            || proposal.Scenes[0] with { Script = current.Scenes[0].Script } != current.Scenes[0]
            || proposal.Scenes[0].Script.Length >= current.Scenes[0].Script.Length)
            throw new InvalidOperationException("The proposal must only shorten scene one's narration; every other field must stay unchanged.");
    }

    public static StoryboardScene[] Scenes(FileDraftContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Storyboard is not { } outline) return [];
        if (outline.Scenes is null || outline.Scenes.Any(s => s is null))
            throw new InvalidOperationException("A storyboard must contain an explicit bounded scene list.");
        return outline.Scenes.Select((s, i) => new StoryboardScene($"scene-{i + 1}", s.Title, s.Text, s.Script)).ToArray();
    }

    public static FileDraftContent WorkingContent(StoryboardState state) => (state.Content
        ?? throw new InvalidOperationException("A durable brief is required.")) with { Storyboard = state.Scenes.Length == 0 ? null : Outline(state) };

    public static VideoOutline Outline(StoryboardState state)
    {
        var source = state.Content?.Storyboard
            ?? throw new InvalidOperationException("The file draft must have a reviewed storyboard before this editor can open it.");
        if (source.Scenes.Length != state.Scenes.Length)
            throw new InvalidOperationException("Storyboard scene identity changed; reload the file draft.");
        return source with {
            Scenes = state.Scenes.Select((scene, index) => source.Scenes[index] with { Title = scene.Title, Text = scene.Text, Script = scene.Script }).ToArray()
        };
    }
}
