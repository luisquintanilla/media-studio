using Microsoft.Extensions.AI;

namespace VideoProduction.Providers;

public sealed record MediaCover(int SchemaVersion, string ProjectId, int Revision, string ProjectSha256,
    string PngSha256, SvgImageEvidence Evidence, DateTimeOffset CreatedAt);

public sealed class MediaCoverStore(FileProductionStore production)
{
    public static void ValidateConfiguration(MediaCoverConfiguration cover, string stateRoot)
    {
        if (cover.Routing is not null) DecisionRoutingChatClient.Validate(cover.Routing);
        var raster = cover.Raster ?? throw new VideoException("cover.configuration", "The approved cover needs a configured renderer.");
        var connection = cover.Connection ?? throw new VideoException("cover.configuration", "The approved cover needs an explicit chat model.");
        Guard.Require(connection.Model is { Length: > 0 and <= 120 }
            && string.Equals(PrivateFiles.LocalPath(raster.ScratchRoot),
                Path.Combine(PrivateFiles.LocalPath(stateRoot), "cover-scratch"), StringComparison.OrdinalIgnoreCase),
            "cover.configuration", "The approved cover needs an explicit chat model and state-bound private renderer.");
        _ = Files.FindExecutable(raster.Node);
        Guard.Require(File.Exists(Path.Combine(PrivateFiles.LocalPath(raster.SourceRoot), "workers", "browser", "illustration.mjs"))
            && Files.ValidHash(raster.FontSha256) && Files.Hash(PrivateFiles.LocalPath(raster.FontFile)) == raster.FontSha256,
            "cover.renderer", "The cover renderer source and pinned font must be configured before approval.");
    }
    private string Root(MediaProject project)
    {
        Guard.Require(Files.ValidId(project.Id) && project.Revision > 0, "cover.identity", "Select a saved project revision.");
        return production.PathFor($"projects/covers/{project.Id}-r{project.Revision}");
    }
    public MediaCover? Read(MediaProject project)
    {
        var root = Root(project);
        var file = Path.Combine(root, "cover.json");
        if (!File.Exists(file)) return null;
        var cover = Json.Read<MediaCover>(file);
        Guard.Require(cover.SchemaVersion == 1 && cover.ProjectId == project.Id && cover.Revision == project.Revision
            && cover.ProjectSha256 == Files.Fingerprint(project) && Files.ValidHash(cover.PngSha256)
            && cover.Evidence is { NativeImageModel: false }
            && cover.Evidence.Svg is { Length: > 0 }
            && Files.ValidHash(cover.Evidence.SvgSha256)
            && Files.HashText(cover.Evidence.Svg) == cover.Evidence.SvgSha256
            && cover.Evidence.PngSha256 == cover.PngSha256 && Files.Hash(Path.Combine(root, "cover.png")) == cover.PngSha256,
            "cover.integrity", "Generated cover does not match its saved project, image or provenance.");
        ChatSvgImageGenerator.ValidatePng(File.ReadAllBytes(Path.Combine(root, "cover.png")), 640, 360);
        _ = SafeSvg.Validate(cover.Evidence.Svg, 640, 360);
        return cover;
    }
    public string? DataUri(MediaProject project) => Read(project) is null ? null
        : "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(Path.Combine(Root(project), "cover.png")));

    public async Task<MediaCover> GenerateAsync(MediaProject project, IImageGenerator generator, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = Root(project);
        Directory.CreateDirectory(root); PrivateFiles.Protect(root, directory: true);
        using var lease = FileTransactions.TryLock(Path.Combine(root, "generation.lock"))
            ?? throw new VideoException("cover.busy", "A cover is already being generated for this saved revision.");
        if (Read(project) is { } retained) return retained;
        var prompt = $"Create an original attractive cover illustration for an explanatory audio/video project titled: {project.Content.Title}. "
            + "Show a clear visual metaphor for the title, not a slide or a screenshot. Use a dark navy background with warm and cool accents. "
            + "No text, letters, media-type badges or watermark. Use the whole 640x360 canvas. This is an illustrative cover, not evidence.";
        var response = await generator.GenerateAsync(new(prompt), new() { Count = 1, ImageSize = new(640, 360), MediaType = "image/png", ResponseFormat = ImageGenerationResponseFormat.Data }, ct);
        Guard.Require(response.Contents is [DataContent { MediaType: "image/png" }]
            && response.RawRepresentation is SvgImageEvidence { NativeImageModel: false },
            "cover.output", "The existing chat-backed image generator must return one PNG with its SVG/raster provenance.");
        var bytes = ((DataContent)response.Contents[0]).Data.ToArray();
        ChatSvgImageGenerator.ValidatePng(bytes, 640, 360);
        var evidence = (SvgImageEvidence)response.RawRepresentation!;
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        Guard.Require(evidence.PngSha256 == sha && evidence.Svg is { Length: > 0 }
            && Files.ValidHash(evidence.SvgSha256) && Files.HashText(evidence.Svg) == evidence.SvgSha256,
            "cover.integrity", "Cover image or SVG differs from its generator's provenance.");
        _ = SafeSvg.Validate(evidence.Svg, 640, 360);
        ct.ThrowIfCancellationRequested();
        using var transaction = production.Admission(ct);
        if (Read(project) is { } existing) return existing;
        var temporary = Path.Combine(root, "cover.pending.png");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Combine(root, "cover.png"), overwrite: true);
            var cover = new MediaCover(1, project.Id, project.Revision, Files.Fingerprint(project), sha, evidence, DateTimeOffset.UtcNow);
            Json.Write(Path.Combine(root, "cover.json"), cover);
            return cover;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
