namespace VideoProduction.Providers;

public sealed record MediaStageGrant(string Operation, string PayloadSha256);
public sealed record MediaPendingOperations(MediaProject Project, string ConfigurationSha256, MediaStageGrant[] Operations);

public static class MediaApprovals
{
    public static MediaPendingOperations Pending(MediaProject project, MediaProductionConfiguration configuration)
    {
        MediaProjectStore.ValidateRecord(project);
        MediaProjectStore.Validate(project.Content, ready: true);
        var digest = Files.Fingerprint(configuration);
        var operations = project.Content.Outputs switch {
            MediaOutput.Audio => new[] { "synthesize-compose-audio" },
            MediaOutput.Video => ["synthesize-render-video"],
            MediaOutput.Both => ["synthesize-compose-audio", "synthesize-render-video"],
            _ => throw new VideoException("media.approval", "Unknown output intent.")
        };
        if (configuration.Cover is { } cover)
        {
            MediaCoverStore.ValidateConfiguration(cover, configuration.StateRoot);
            operations = [.. operations, "generate-library-cover"];
        }
        return new(project, digest, operations.Select(operation => new MediaStageGrant(operation,
            Files.Fingerprint(new { operation, project, configurationSha256 = digest }))).ToArray());
    }

    public static MediaReviewedRequest Approve(MediaProject project, MediaProductionConfiguration configuration, string approvedBy)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(approvedBy), "media.approval", "An explicit application-owned approval is required.");
        var pending = Pending(project, configuration);
        return new(1, pending.Project, pending.ConfigurationSha256, approvedBy, pending.Operations);
    }

    public static void Validate(MediaReviewedRequest request, MediaProductionConfiguration configuration)
    {
        var pending = Pending(request.Project, configuration);
        Guard.Require(request.Grants is not null && request.Grants.SequenceEqual(pending.Operations)
            && request.ConfigurationSha256 == pending.ConfigurationSha256,
            "media.stage-approval", "Each selected synthesis/composition/render operation needs its exact revision/configuration-bound application grant. Framework approval metadata is not authority.");
    }
}
