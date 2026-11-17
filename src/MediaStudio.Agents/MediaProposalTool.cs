using GitHub.Copilot;
using Microsoft.Extensions.AI;
using VideoProduction;
using VideoProduction.Providers;

#pragma warning disable GHCP001
namespace MediaStudio.Agents;

public sealed record MediaProposal(string Id, string ProjectId, int BaseRevision, string WorkingSha256,
    MediaProjectContent Content, GuidanceIdentity Guidance);

public sealed class MediaProposalTool
{
    public const string ToolName = "propose_media_revision";
    private MediaProject? active;
    private CancellationToken cancellation;
    public MediaProposal? Completed { get; private set; }
    public AIFunction Function { get; }
    public MediaProposalTool() => Function = CopilotTool.DefineTool(
        (int revision, string workingSha256, MediaProjectContent content) => Propose(revision, workingSha256, content),
        new CopilotToolOptions { SkipPermission = true, IsTerminal = true },
        new AIFunctionFactoryOptions {
            Name = ToolName,
            Description = "Propose inert shared brief/teaching metadata and independent audio script/video storyboard for the exact working revision/output intent. Never save, approve, generate, adopt a transcript or publish."
        });
    public string Begin(MediaProject working, string request, CancellationToken ct)
    {
        if (active is not null) throw new InvalidOperationException("An authoring turn is already active.");
        MediaProjectStore.ValidateRecord(working);
        Guard.Require(!string.IsNullOrWhiteSpace(request) && request.Length <= 8000 && !request.Contains('\0'),
            "media.authoring", "An explicit bounded revision request is required.");
        active = System.Text.Json.JsonSerializer.Deserialize<MediaProject>(Json.Text(working), Json.Options)!;
        cancellation = ct;
        Completed = null;
        return Json.Text(new
        {
            schemaVersion = 1, request, workingRevision = active.Revision,
            workingSha256 = Files.Fingerprint(active.Content), currentProject = active,
            guidance = TeachingGuidance.Identity,
            capabilities = new
            {
                outputs = active.Content.Outputs.ToString(),
                audio = "Self-contained spoken explanation; explicit sample pauses; not extracted video or a dummy scene.",
                video = "Title, Code, Diagram and bounded trusted visual presets only.",
                authority = "One typed proposal; no save, approval, execution, files, network or publication."
            }
        });
    }
    public MediaProposal Propose(int revision, string workingSha256, MediaProjectContent content)
    {
        cancellation.ThrowIfCancellationRequested();
        var current = active ?? throw new InvalidOperationException("No explicit authoring turn is active.");
        if (Completed is not null || revision != current.Revision
            || workingSha256 != Files.Fingerprint(current.Content) || content.Outputs != current.Content.Outputs)
            throw new InvalidOperationException("Proposal must bind the exact current revision, working content and output intent.");
        MediaProjectStore.Validate(content);
        return Completed = new(Guid.NewGuid().ToString("N"), current.Id, revision, workingSha256,
            System.Text.Json.JsonSerializer.Deserialize<MediaProjectContent>(Json.Text(content), Json.Options)!,
            TeachingGuidance.Identity);
    }
    public void End(bool succeeded) { active = null; cancellation = default; if (!succeeded) Completed = null; }
}
