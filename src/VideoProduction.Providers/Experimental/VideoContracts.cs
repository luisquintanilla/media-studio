// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses the original proposal under the MIT license.
// Adapted locally from dotnet/extensions PR7420 head a7a06c9bc9becfa4e40fc55aa313037801505457.
// Modified: isolated namespace/diagnostic, consolidated declarations and no internal shared helpers.
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using Microsoft.Extensions.AI;

namespace VideoProduction.Providers.Experimental;

[Experimental("VPROD001")]
public interface IVideoGenerator : IDisposable
{
    Task<VideoGenerationOperation> GenerateAsync(VideoGenerationRequest request, VideoGenerationOptions? options = null,
        CancellationToken cancellationToken = default);
    object? GetService(Type serviceType, object? serviceKey = null);
}

[Experimental("VPROD001")]
public class VideoGenerationRequest
{
    public VideoGenerationRequest() { }
    public VideoGenerationRequest(string prompt) => Prompt = prompt;
    public string? Prompt { get; set; }
    public string? NegativePrompt { get; set; }
    public VideoOperationKind OperationKind { get; set; }
    public string? SourceVideoId { get; set; }
    public AIContent? StartFrame { get; set; }
    public AIContent? EndFrame { get; set; }
    public IList<AIContent>? ReferenceImages { get; set; }
    public AIContent? SourceVideo { get; set; }
}

[Experimental("VPROD001")]
public enum VideoOperationKind { Create, Edit, Extend }

[Experimental("VPROD001")]
public enum VideoGenerationResponseFormat { Uri, Data, Hosted }

[Experimental("VPROD001")]
public class VideoGenerationOptions
{
    public string? AspectRatio { get; set; }
    public int? Count { get; set; }
    public TimeSpan? Duration { get; set; }
    public int? FramesPerSecond { get; set; }
    public bool? GenerateAudio { get; set; }
    public string? MediaType { get; set; }
    public string? ModelId { get; set; }
    public Func<IVideoGenerator, object?>? RawRepresentationFactory { get; set; }
    public VideoGenerationResponseFormat? ResponseFormat { get; set; }
    public int? Seed { get; set; }
    public Size? VideoSize { get; set; }
    public AdditionalPropertiesDictionary? AdditionalProperties { get; set; }
    public virtual VideoGenerationOptions Clone()
    {
        var clone = (VideoGenerationOptions)MemberwiseClone();
        clone.AdditionalProperties = AdditionalProperties?.Clone();
        return clone;
    }
}

[Experimental("VPROD001")]
public readonly record struct VideoGenerationProgress(string? Status, int? PercentComplete);

[Experimental("VPROD001")]
public abstract class VideoGenerationOperation
{
    public abstract string? OperationId { get; }
    public abstract string? Status { get; }
    public abstract int? PercentComplete { get; }
    public abstract bool IsCompleted { get; }
    public abstract string? FailureReason { get; }
    public string? ModelId { get; set; }
    public UsageDetails? Usage { get; set; }
    public object? RawRepresentation { get; set; }
    public AdditionalPropertiesDictionary? AdditionalProperties { get; set; }
    public abstract Task UpdateAsync(CancellationToken cancellationToken = default);
    public abstract Task WaitForCompletionAsync(IProgress<VideoGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    public abstract Task<IList<AIContent>> GetContentsAsync(VideoGenerationOptions? options = null,
        CancellationToken cancellationToken = default);
    public virtual VideoGenerationRequest CreateEditRequest(string prompt) =>
        new(prompt) { SourceVideoId = OperationId, OperationKind = VideoOperationKind.Edit };
    public virtual VideoGenerationRequest CreateExtensionRequest(string? prompt = null) =>
        new() { Prompt = prompt, SourceVideoId = OperationId, OperationKind = VideoOperationKind.Extend };
}
