using Microsoft.Extensions.AI;
using VideoProduction.Providers.Experimental;

namespace VideoProduction.Providers;

public sealed class LocalVideoGenerationOptions : VideoGenerationOptions
{
    public required string IdempotencyKey { get; init; }
    public VideoOutline? Plan { get; init; }
    public int MaxScenes { get; init; } = 6;
}

public sealed class CompositeVideoGenerator(VideoJobs jobs, VideoJobConfiguration config) : IVideoGenerator
{
    public Task<VideoGenerationOperation> GenerateAsync(VideoGenerationRequest request, VideoGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Guard.Require(request.OperationKind == VideoOperationKind.Create && request.SourceVideo is null
            && request.SourceVideoId is null && request.StartFrame is null && request.EndFrame is null
            && request.ReferenceImages is null && request.NegativePrompt is null,
            "video.unsupported", "This composite creates authored narrated projects, not model video edits/extensions/interpolation/reference-image synthesis.");
        Guard.Require(options is LocalVideoGenerationOptions, "video.options",
            "Use LocalVideoGenerationOptions with an intentional stable idempotency key and optional caller-authored plan.");
        ValidateOptions(options!);
        var local = (LocalVideoGenerationOptions)options!;
        var duration = options!.Duration?.TotalSeconds ?? 180;
        Guard.Require(double.IsInteger(duration) && duration is >= 5 and <= 300, "video.options",
            "Duration is an integer maximum5..300seconds, never an exact model-generated duration.");
        var snapshot = jobs.Submit(request.Prompt!, local.IdempotencyKey, new((int)duration, local.MaxScenes), local.Plan);
        return Task.FromResult<VideoGenerationOperation>(new LocalVideoOperation(jobs, config, snapshot.JobId));
    }

    internal static void ValidateOptions(VideoGenerationOptions options)
    {
        Guard.Require((options.AspectRatio is null or "16:9") && (options.Count is null or 1)
            && (options.FramesPerSecond is null or 30) && (options.GenerateAudio is null or true)
            && (options.MediaType is null or "video/mp4") && options.ModelId is null && options.Seed is null
            && options.RawRepresentationFactory is null && options.AdditionalProperties is null
            && (options.ResponseFormat is null or VideoGenerationResponseFormat.Data)
            && (options.VideoSize is null || options.VideoSize == new System.Drawing.Size(1920, 1080)),
            "video.unsupported", "Only one narrated1080p30MP4 with in-memory content is supported; model/seed/raw/options/URI output are not silently ignored.");
    }
    public LocalVideoOperation Open(string jobId) { _ = jobs.Get(jobId); return new(jobs, config, jobId); }
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }
    public void Dispose() { } // The caller/DI host owns the shared job service.
}

public sealed class LocalVideoOperation(VideoJobs jobs, VideoJobConfiguration config, string id) : VideoGenerationOperation
{
    public VideoJobView Snapshot => jobs.Get(id);
    public override string OperationId => id;
    public override string Status => Snapshot.Status;
    public override int? PercentComplete => Status == "completed" ? 100 : null;
    public override bool IsCompleted => Status is "completed" or "failed" or "cancelled" or "interrupted";
    public override string? FailureReason => Snapshot.Failure?.Summary
        ?? (Status == "cancelled" ? "Operator-requested cancellation; partial artifacts preserved." : null);
    public override Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Snapshot;
        return Task.CompletedTask;
    }
    public VideoJobView Cancel() => jobs.Cancel(id);
    public override async Task WaitForCompletionAsync(IProgress<VideoGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = Snapshot;
            progress?.Report(new(snapshot.Status, snapshot.Status == "completed" ? 100 : null));
            if (snapshot.Status is "failed" or "cancelled" or "interrupted")
                throw new InvalidOperationException(FailureReason);
            if (snapshot.Status == "completed") return;
            await Task.Delay(100, cancellationToken);
        }
    }
    public override async Task<IList<AIContent>> GetContentsAsync(VideoGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (options is not null)
        {
            CompositeVideoGenerator.ValidateOptions(options);
            Guard.Require(options.Duration is null && options is not LocalVideoGenerationOptions,
                "video.unsupported", "Content retrieval cannot change generation options.");
        }
        var snapshot = Snapshot;
        Guard.Require(snapshot.Status == "completed", "video.incomplete", "Content is available only from a completed machine-candidate job.");
        var artifact = snapshot.Artifacts.Single(a => a.Kind == "candidate-mp4");
        var path = Files.Under(config.StateRoot, artifact.Path);
        Guard.Require(new FileInfo(path).Length <= 268_435_456, "video.content-limit",
            "Candidate exceeds the256MiB in-memory result bound; use verified local artifact references.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        Guard.Require(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) == artifact.Sha256,
            "jobs.artifact", "Completed content differs from its immutable artifact hash.");
        return [new DataContent(bytes, "video/mp4")];
    }
    public override VideoGenerationRequest CreateEditRequest(string prompt) =>
        throw new NotSupportedException("Model-only video editing is not implemented by this authored-project composite.");
    public override VideoGenerationRequest CreateExtensionRequest(string? prompt = null) =>
        throw new NotSupportedException("Model-only video extension is not implemented by this authored-project composite.");
}
