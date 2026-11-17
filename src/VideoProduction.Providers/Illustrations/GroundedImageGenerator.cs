using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VideoProduction.Providers.Illustrations;

/// <summary>Keys the illustration decorators read from options and write onto the generated image content.</summary>
public static class IllustrationProperties
{
    /// <summary>Option: the original scene brief (critique judges against it, not the grounded prompt).</summary>
    public const string Brief = "mediastudio.brief";
    /// <summary>Option: palette name used to pick and reuse exemplars (default "default").</summary>
    public const string Palette = "mediastudio.palette";
    /// <summary>Content: critique score (double, 0..1) of the returned image.</summary>
    public const string CritiqueScore = "critiqueScore";
    /// <summary>Content: true when the returned image met the critique threshold.</summary>
    public const string CritiquePassed = "critiquePassed";
    /// <summary>Content: <see cref="CritiqueReceipt"/>.</summary>
    public const string Critique = "mediastudio.critique";
    /// <summary>Content: <see cref="GroundingReceipt"/>.</summary>
    public const string Grounding = "mediastudio.grounding";
    public const string DefaultPalette = "default";

    internal static string? Text(ImageGenerationOptions? options, string key) =>
        options?.AdditionalProperties?.TryGetValue(key, out var value) == true && value is string text && !string.IsNullOrWhiteSpace(text)
            ? text : null;

    internal static DataContent? Image(ImageGenerationResponse response) => response.Contents.OfType<DataContent>().FirstOrDefault();

    /// <summary>The validated SVG behind a response: the SVG content itself, or the evidence of a rasterized SVG.</summary>
    public static string? Svg(ImageGenerationResponse response) =>
        response.RawRepresentation is SvgImageEvidence { Svg: { Length: > 0 } svg } ? svg
        : Image(response) is { MediaType: ChatSvgImageGenerator.SvgMediaType } content ? Encoding.UTF8.GetString(content.Data.Span)
        : null;
}

/// <summary>What grounding added to an illustration prompt.</summary>
public sealed record GroundingReceipt(string? EmbeddingModel, int Facts, int Exemplars, string? ReusedSha256, string? Skipped = null);

/// <summary>
/// Grounds each illustration: embeds the scene brief once, reuses a cached exemplar when one is near-identical, otherwise
/// prepends "Facts from the source" (top-k <see cref="SourceIndex"/> passages, ≤ <see cref="MaxFactCharacters"/>) and
/// "Style examples" (nearest <see cref="SvgExemplarLibrary"/> SVGs, each ≤ <see cref="MaxExemplarCharacters"/>) to the
/// prompt. Results that pass critique are added to the exemplar library. Without an embedding generator it passes through.
/// </summary>
public sealed class GroundedImageGenerator : DelegatingImageGenerator
{
    public const int MaxBriefCharacters = 2000;
    public const int MaxFactCharacters = 1500;
    public const int MaxExemplarCharacters = 4096;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? embeddings;
    private readonly Func<SourceIndex?> sources;
    private readonly SvgExemplarLibrary? exemplars;
    private readonly ILogger logger;

    public GroundedImageGenerator(IImageGenerator innerGenerator, IEmbeddingGenerator<string, Embedding<float>>? embeddings,
        Func<SourceIndex?>? sources = null, SvgExemplarLibrary? exemplars = null, ILogger? logger = null)
        : base(innerGenerator)
    {
        this.embeddings = embeddings;
        this.sources = sources ?? (() => null);
        this.exemplars = exemplars;
        this.logger = logger ?? NullLogger.Instance;
    }

    public override async Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request,
        ImageGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var brief = request.Prompt ?? "";
        Guard.Require(!string.IsNullOrWhiteSpace(brief) && brief.Length <= MaxBriefCharacters && request.OriginalImages is null,
            "image.request", "Provide a scene brief of at most 2,000 characters, not image editing/original images.");
        if (embeddings is null) return await base.GenerateAsync(request, options, cancellationToken).ConfigureAwait(false);

        var palette = IllustrationProperties.Text(options, IllustrationProperties.Palette) ?? IllustrationProperties.DefaultPalette;
        var model = embeddings.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId;
        ReadOnlyMemory<float> vector;
        try { vector = (await embeddings.GenerateAsync([brief], cancellationToken: cancellationToken).ConfigureAwait(false))[0].Vector; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning("Illustration grounding skipped: the embedding model failed ({Error}).", error.GetType().Name);
            var plain = await base.GenerateAsync(request, options, cancellationToken).ConfigureAwait(false);
            Annotate(plain, new GroundingReceipt(model, 0, 0, null, "embedding-failed"));
            return plain;
        }

        if (exemplars is not null && InnerGenerator.GetService<ChatSvgImageGenerator>() is { } svgGenerator
            && await exemplars.TryReuseAsync(vector, palette, cancellationToken).ConfigureAwait(false) is { } reuse)
        {
            var size = options?.ImageSize ?? new System.Drawing.Size(1920, 1080);
            try
            {
                var reused = await svgGenerator.RenderSvgAsync(reuse.Svg, brief, size, cancellationToken).ConfigureAwait(false);
                Annotate(reused, new GroundingReceipt(model, 0, 0, reuse.Sha256));
                logger.LogInformation("Illustration reused exemplar {Sha} (similarity {Score:0.000}).", reuse.Sha256[..12], reuse.Similarity);
                return reused;
            }
            catch (VideoException error) when (error.Code == "image.svg")
            {
                logger.LogWarning("Cached exemplar {Sha} no longer validates for this canvas; generating instead.", reuse.Sha256[..12]);
            }
        }

        var facts = sources() is { Count: > 0 } index ? index.TopK(vector) : [];
        var styles = exemplars is null ? []
            : (await exemplars.NearestAsync(vector, palette, cancellationToken: cancellationToken).ConfigureAwait(false))
                .Where(e => e.Svg.Length <= MaxExemplarCharacters).ToArray();
        var prompt = BuildPrompt(brief, facts.Select(f => f.Item.Text), styles.Select(s => s.Svg));
        var grounded = options?.Clone() ?? new ImageGenerationOptions();
        grounded.AdditionalProperties ??= [];
        grounded.AdditionalProperties.TryAdd(IllustrationProperties.Brief, brief);
        var response = await base.GenerateAsync(new ImageGenerationRequest(prompt), grounded, cancellationToken).ConfigureAwait(false);
        Annotate(response, new GroundingReceipt(model, facts.Count, styles.Length, null));

        if (exemplars is not null && IllustrationProperties.Image(response)?.AdditionalProperties is { } properties
            && properties.TryGetValue(IllustrationProperties.CritiquePassed, out var passed) && passed is true
            && IllustrationProperties.Svg(response) is { } svg)
            await exemplars.AddAsync(brief, palette, svg, vector, cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <summary>Facts (≤ 1,500 chars in total, truncated) and style examples first, then the scene brief.</summary>
    public static string BuildPrompt(string brief, IEnumerable<string> facts, IEnumerable<string> exemplarSvgs)
    {
        var builder = new StringBuilder();
        var factText = new StringBuilder();
        foreach (var fact in facts)
        {
            var line = "- " + string.Join(' ', fact.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) + "\n";
            var room = MaxFactCharacters - factText.Length;
            if (room <= 3) break;
            factText.Append(line.Length <= room ? line : line[..(room - 2)] + "…\n");
        }
        if (factText.Length > 0)
            builder.Append("Facts from the source (use these exact labels, names and numbers; do not invent others):\n")
                .Append(factText).Append('\n');
        var examples = exemplarSvgs.ToArray();
        if (examples.Length > 0)
        {
            builder.Append("Style examples (match their palette, spacing and visual style; do not copy their content):\n");
            for (var i = 0; i < examples.Length; i++) builder.Append("Example ").Append(i + 1).Append(":\n").Append(examples[i]).Append("\n\n");
        }
        if (builder.Length == 0) return brief;
        return builder.Append("Scene brief:\n").Append(brief).ToString();
    }

    private static void Annotate(ImageGenerationResponse response, GroundingReceipt receipt)
    {
        if (IllustrationProperties.Image(response) is { } image)
            (image.AdditionalProperties ??= [])[IllustrationProperties.Grounding] = receipt;
    }
}
