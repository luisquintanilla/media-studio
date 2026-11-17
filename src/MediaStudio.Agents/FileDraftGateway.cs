using VideoProduction.Providers;

namespace MediaStudio.Agents;

// Editor projection only: all transactions and persistence belong to FileDraftStore.
public sealed class FileDraftGateway
{
    public FileDraftStore Drafts { get; }
    public FileProductionStore Production { get; }
    public string DraftId { get; private set; }
    public string StateRoot { get; }

    public FileDraftGateway(string stateRoot, string? draftId = null, bool blank = false)
    {
        StateRoot = Path.GetFullPath(stateRoot);
        Production = new(stateRoot);
        Drafts = new(Production);
        DraftId = draftId ?? Drafts.Start(new(1, blank ? "hybrid-concierge-blank-v1" : "hybrid-private-sample-v1",
            blank ? BlankContent() : SampleContent())).Id;
        _ = Read();
    }

    public static FileDraftContent BlankContent() => new("Describe a video to begin.", "", "", new(180, 12), null);
    public StoryboardState New()
    {
        DraftId = Drafts.Start(new(1, "concierge-new-" + Guid.NewGuid().ToString("N"), BlankContent())).Id;
        return Read();
    }

    public static FileDraftContent SampleContent()
    {
        var sample = StoryboardRules.Sample();
        return new("Explain reviewing a private storyboard.", "Developers", "Keep human review in control.",
            new(60, 6), new("Private Hybrid sample", sample.Scenes.Select(s =>
                new ProposedScene(TemplateKind.Title, s.Title, s.Text, s.Script)).ToArray()));
    }

    public StoryboardState Read()
    {
        return Project(Drafts.Read(DraftId));
    }

    private static StoryboardState Project(FileDraft draft)
    {
        return new(draft.Revision, StoryboardRules.Scenes(draft.Content),
            DraftId: draft.Id, Status: draft.Status, Content: draft.Content);
    }

    public void EnsureCurrent(StoryboardState editable)
    {
        StoryboardRules.Validate(editable);
        var current = Read();
        if (editable.DraftId != DraftId || current.Revision != editable.Revision)
            throw new InvalidOperationException("Revision conflict: reload the shared file draft before authoring or saving.");
        if (current.Status is not ("draft" or "review"))
            throw new InvalidOperationException("Submitted drafts are immutable; authoring and editing are disabled.");
    }

    public StoryboardState Save(StoryboardState editable, long expectedRevision)
    {
        EnsureCurrent(editable);
        if (editable.Revision != expectedRevision)
            throw new InvalidOperationException("Revision conflict: the reviewed base revision changed.");
        return Project(Drafts.Edit(new(1, DraftId, expectedRevision, "hybrid-edit-" + Guid.NewGuid().ToString("N"),
            "replace-content", Content: StoryboardRules.WorkingContent(editable))));
    }

    public StoryboardState Accept(StoryboardState editable, StoryboardProposal proposal)
    {
        StoryboardRules.ValidateProposal(editable, proposal);
        return Save(editable with {
            Scenes = proposal.Scenes,
            Content = proposal.Content ?? editable.Content,
            Proposal = null,
            Status = "review"
        },
            proposal.BaseRevision);
    }
}
