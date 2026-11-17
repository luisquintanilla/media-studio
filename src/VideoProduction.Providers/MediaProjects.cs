using System.Text.Json;
using System.Text.Json.Serialization;
using MediaStudio.Audio;

namespace VideoProduction.Providers;

[JsonConverter(typeof(JsonStringEnumConverter<MediaOutput>))]
public enum MediaOutput { Audio, Video, Both }
public sealed record MediaProjectContent(string Title, string Prompt, string Audience, string Outcome,
    MediaOutput Outputs, int MaximumSeconds, AudioScriptPart[]? AudioScript, VideoOutline? Storyboard,
    TeachingIntent? Teaching = null);
public sealed record MediaProject(int SchemaVersion, string Id, int Revision, MediaProjectContent Content,
    GuidanceIdentity Guidance, DateTimeOffset SavedAt);
public sealed record MediaCatalog(int SchemaVersion, MediaProject[] Projects);
public sealed record AudioProductionSettings(int SampleRate, SpeechConfig Speech, SpeechClientIdentity Identity);
public sealed record MediaProductionConfiguration(int SchemaVersion, string StateRoot,
    AudioProductionSettings? Audio, GenerationConfiguration? Video,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OfflineMediaConfiguration? Verification = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DecisionProviderConfiguration? Decisions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MediaCoverConfiguration? Cover = null);
public sealed record MediaCoverConfiguration(PlannerConfiguration Connection, SvgRasterConfiguration Raster,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChatRoutingConfiguration? Routing = null);
public sealed record OfflineMediaConfiguration(string Ffmpeg, string Ffprobe, string FontFile, string FontSha256,
    int DelayMilliseconds = 0);
public sealed record MediaReviewedRequest(int SchemaVersion, MediaProject Project, string ConfigurationSha256,
    string ApprovedBy, MediaStageGrant[] Grants);
public sealed record MediaOutputOutcome(string Output, string Status, string? ManifestSha256 = null,
    string? ErrorCode = null);
public sealed record MediaOutputReport(int SchemaVersion, string ProjectId, int Revision, string ContentSha256,
    bool TakesReused, MediaOutputOutcome[] Outputs);
public sealed record MediaOutputReceipt(int SchemaVersion, string AttemptId, string Token,
    string ReviewedRequestSha256, string Output, VideoArtifact[] Artifacts);
public sealed record MediaVerifiedArtifacts(int SchemaVersion, string AttemptId, string OverallStatus,
    MediaOutputReceipt[] Outputs);

public sealed class MediaProjectStore
{
    private readonly FileProductionStore production;
    private string CatalogPath => production.PathFor("projects/catalog.json");
    public MediaProjectStore(FileProductionStore production)
    {
        this.production = production;
        using var transaction = production.Admission();
        Directory.CreateDirectory(production.PathFor("projects"));
        if (!File.Exists(CatalogPath)) Json.Write(CatalogPath, new MediaCatalog(1, []));
    }

    public static void Validate(MediaProjectContent content, bool ready = false)
    {
        Guard.Require(content is not null && Enum.IsDefined(content.Outputs)
            && content.Title is { Length: > 0 and <= 100 }
            && content.Prompt is { Length: > 0 and <= 6000 }
            && content.Audience is { Length: <= 2000 } && content.Outcome is { Length: <= 2000 }
            && content.MaximumSeconds is >= 1 and <= 1800,
            "media.content", "A bounded named project, explicit Audio/Video/Both intent and duration are required.");
        TeachingGuidance.Validate(content!.Teaching);
        if (content.Outputs == MediaOutput.Audio)
            Guard.Require(content.Storyboard is null, "media.content", "Audio-only projects cannot contain dummy visual fields.");
        if (content.Outputs == MediaOutput.Video)
            Guard.Require(content.AudioScript is null, "media.content", "Video-only projects do not imply an audio export.");
        if (content.AudioScript is { } parts)
            Guard.Require(parts.Length is > 0 and <= 12
                && parts.All(p => p is not null && Files.ValidId(p.Id)
                    && p.Text is { Length: > 0 and <= 8000 } && p.PauseAfterSamples >= 0)
                && parts.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == parts.Length
                && parts.Sum(p => (long)p.Text.Length) <= 32000,
                "media.script", "An audio script needs unique bounded parts and explicit sample pauses.");
        if (content.Storyboard is not null)
            ChatVideoPlanner.Validate(content.Storyboard, new(content.MaximumSeconds, 12));
        if (ready)
            Guard.Require((content.Outputs == MediaOutput.Video || content.AudioScript is { Length: > 0 })
                && (content.Outputs == MediaOutput.Audio || content.Storyboard is not null),
                "media.review", "The selected outputs require their own saved script/storyboard before approval.");
    }

    private MediaCatalog Load()
    {
        var catalog = Json.Read<MediaCatalog>(CatalogPath);
        Guard.Require(catalog.SchemaVersion == 1 && catalog.Projects is { Length: <= 256 }
            && catalog.Projects.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == catalog.Projects.Length,
            "media.catalog", "Invalid versioned project catalog.");
        foreach (var project in catalog.Projects) ValidateRecord(project);
        return catalog;
    }
    public static void ValidateRecord(MediaProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Guard.Require(project.SchemaVersion == 1 && project.Revision > 0
            && System.Text.RegularExpressions.Regex.IsMatch(project.Id, @"\Amedia-[a-f0-9]{32}\z")
            && project.Guidance == TeachingGuidance.Identity, "media.project", "Invalid project/guidance revision.");
        Validate(project.Content);
    }
    private string RevisionPath(string id, int revision)
    {
        Guard.Require(System.Text.RegularExpressions.Regex.IsMatch(id, @"\Amedia-[a-f0-9]{32}\z") && revision > 0,
            "media.project", "An explicit saved project/revision identity is required.");
        return production.PathFor($"projects/{id}/r{revision}.json");
    }

    public MediaProject[] List()
    {
        using var transaction = production.Admission();
        return Load().Projects.OrderByDescending(p => p.SavedAt).ToArray();
    }
    public MediaProject Read(string id, int? revision = null)
    {
        using var transaction = production.Admission();
        return ReadCore(id, revision);
    }
    private MediaProject ReadCore(string id, int? revision)
    {
        var current = Load().Projects.SingleOrDefault(p => p.Id == id)
            ?? throw new VideoException("media.missing", "No project with that identity exists.");
        var saved = Json.Read<MediaProject>(RevisionPath(id, revision ?? current.Revision));
        ValidateRecord(saved);
        Guard.Require(saved.Id == id && saved.Revision == (revision ?? current.Revision)
            && (saved.Revision != current.Revision || Files.Fingerprint(saved) == Files.Fingerprint(current)),
            "media.identity", "Saved revision and catalog head differ.");
        return saved;
    }
    public MediaProject Save(string? id, int expectedRevision, MediaProjectContent content)
    {
        content = JsonSerializer.Deserialize<MediaProjectContent>(Json.Text(content), Json.Options)
            ?? throw new VideoException("media.content", "Project content is required.");
        Validate(content);
        using var transaction = production.Admission();
        var catalog = Load();
        if (id is null)
        {
            Guard.Require(expectedRevision == 0 && catalog.Projects.Length < 256, "media.catalog", "A new project requires revision zero and catalog capacity.");
            id = "media-" + Guid.NewGuid().ToString("N");
        }
        else
            Guard.Require(ReadCore(id, null).Revision == expectedRevision, "media.revision",
                "The saved project changed. Reload before applying or saving changes.");
        var saved = new MediaProject(1, id, checked(expectedRevision + 1), content,
            TeachingGuidance.Identity, DateTimeOffset.UtcNow);
        var path = RevisionPath(id, saved.Revision);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            Guard.Require(Files.Fingerprint(Json.Read<MediaProject>(path).Content) == Files.Fingerprint(content),
                "media.revision", "An immutable saved revision already exists with different content.");
        else Json.Write(path, saved);
        saved = Json.Read<MediaProject>(path);
        Json.Write(CatalogPath, catalog with { Projects = [.. catalog.Projects.Where(p => p.Id != id), saved] });
        return saved;
    }
    public static string SubmissionKey(MediaProject project) => $"{project.Id}-r{project.Revision}";
    public FileAttemptStatus? Submission(MediaProject project, bool observeOnly = false)
    {
        FileAttempt? attempt;
        using (var transaction = production.Admission())
        {
            var saved = ReadCore(project.Id, project.Revision);
            Guard.Require(Files.Fingerprint(saved) == Files.Fingerprint(project), "media.identity", "Project snapshot differs.");
            attempt = production.Lookup(SubmissionKey(project));
        }
        return attempt is null ? null : observeOnly ? production.ObserveStatus(attempt.Id) : production.Status(attempt.Id);
    }
    internal void Admit(MediaReviewedRequest request, Action commit)
    {
        var saved = ReadCore(request.Project.Id, null);
        Guard.Require(Files.Fingerprint(saved) == Files.Fingerprint(request.Project), "media.revision",
            "Approval must bind the exact current saved revision, not unsaved edits or an older head.");
        Validate(saved.Content, ready: true);
        commit();
    }
}
