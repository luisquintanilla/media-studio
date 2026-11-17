using System.Text;
using System.Text.Json;
using MediaStudio.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VideoProduction.Providers.Illustrations;

/// <summary>Structured critique returned by the vision model.</summary>
public sealed record Critique(double Score, string[] Issues);

/// <summary>One generate → critique round.</summary>
public sealed record CritiqueAttempt(int Attempt, double? Score, string[] Issues, string? Error = null);

/// <summary>Critique receipt attached to the returned image content under <see cref="IllustrationProperties.Critique"/>.</summary>
public sealed record CritiqueReceipt(double Threshold, int MaxRetries, int SelectedAttempt, double? Score, bool Passed,
    string? Model, CritiqueAttempt[] Attempts);

/// <summary>
/// Scores each generated image against its brief with the <c>"vision"</c> <see cref="IChatClient"/> (PNG as an image,
/// SVG as source text) using a JSON-schema response. Below <see cref="CritiqueSettings.Threshold"/> it re-generates with the
/// listed issues, at most <see cref="CritiqueSettings.MaxRetries"/> times, keeps the best-scoring result and records
/// <see cref="IllustrationProperties.CritiqueScore"/>. Without a vision client, or when disabled, it passes through.
/// An unusable critique stops the loop (the image itself is still returned).
/// </summary>
public sealed class CritiquingImageGenerator : DelegatingImageGenerator
{
    public const int MaxIssues = 6;
    public const int MaxIssueCharacters = 200;
    private readonly IChatClient? vision;
    private readonly CritiqueSettings settings;
    private readonly ILogger logger;

    public CritiquingImageGenerator(IImageGenerator innerGenerator, IChatClient? vision, CritiqueSettings? settings = null,
        ILogger? logger = null) : base(innerGenerator)
    {
        this.vision = vision;
        this.settings = settings ?? new CritiqueSettings();
        Guard.Require(double.IsFinite(this.settings.Threshold) && this.settings.Threshold is >= 0 and <= 1
            && this.settings.MaxRetries is >= 0 and <= 5, "image.critique", "Critique threshold must be 0..1 and retries 0..5.");
        this.logger = logger ?? NullLogger.Instance;
    }

    public override async Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request,
        ImageGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (vision is null || !settings.Enabled) return await base.GenerateAsync(request, options, cancellationToken).ConfigureAwait(false);
        var brief = IllustrationProperties.Text(options, IllustrationProperties.Brief) ?? request.Prompt ?? "";
        var attempts = new List<CritiqueAttempt>();
        (ImageGenerationResponse Response, double Score, int Attempt)? best = null;
        ImageGenerationResponse? first = null;
        string? model = null;
        var issues = Array.Empty<string>();
        for (var attempt = 0; attempt <= settings.MaxRetries; attempt++)
        {
            ImageGenerationResponse response;
            try
            {
                var prompt = attempt == 0 || issues.Length == 0 ? request.Prompt ?? ""
                    : request.Prompt + "\n\nThe previous attempt was reviewed. Fix these issues:\n- " + string.Join("\n- ", issues);
                response = await base.GenerateAsync(new ImageGenerationRequest(prompt), options, cancellationToken).ConfigureAwait(false);
            }
            catch (VideoException error) when (attempt > 0)
            {
                attempts.Add(new(attempt, null, [], error.Code));
                break;
            }
            first ??= response;
            var (critique, critic, failure) = await CritiqueAsync(response, brief, cancellationToken).ConfigureAwait(false);
            model ??= critic;
            if (critique is null)
            {
                attempts.Add(new(attempt, null, [], failure));
                logger.LogWarning("Illustration critique unavailable ({Reason}); keeping the image without a score.", failure);
                break;
            }
            attempts.Add(new(attempt, critique.Score, critique.Issues));
            if (best is null || critique.Score > best.Value.Score) best = (response, critique.Score, attempt);
            if (critique.Score >= settings.Threshold) break;
            issues = critique.Issues;
        }
        var selected = best?.Response ?? first!;
        var passed = best is { } b && b.Score >= settings.Threshold;
        if (IllustrationProperties.Image(selected) is { } image)
        {
            var properties = image.AdditionalProperties ??= [];
            if (best is { } chosen) properties[IllustrationProperties.CritiqueScore] = chosen.Score;
            properties[IllustrationProperties.CritiquePassed] = passed;
            properties[IllustrationProperties.Critique] = new CritiqueReceipt(settings.Threshold, settings.MaxRetries,
                best?.Attempt ?? 0, best?.Score, passed, model, [.. attempts]);
        }
        logger.LogInformation("Illustration critique: {Attempts} attempt(s), best score {Score}, passed {Passed}.",
            attempts.Count, best?.Score, passed);
        return selected;
    }

    private async Task<(Critique? Critique, string? Model, string? Failure)> CritiqueAsync(ImageGenerationResponse response,
        string brief, CancellationToken cancellationToken)
    {
        var image = IllustrationProperties.Image(response);
        var content = new List<AIContent>
        {
            new TextContent("Scene brief:\n" + brief + "\n\nReview the illustration below against the brief.")
        };
        if (image is { MediaType: ChatSvgImageGenerator.SvgMediaType }) content.Add(new TextContent("SVG source:\n" + Encoding.UTF8.GetString(image.Data.Span)));
        else if (image is not null && image.HasTopLevelMediaType("image")) content.Add(image);
        else if (IllustrationProperties.Svg(response) is { } svg) content.Add(new TextContent("SVG source:\n" + svg));
        else return (null, null, "no-image");
        ChatResponse reply;
        try
        {
            reply = await vision!.GetResponseAsync(
                [new(ChatRole.System, """
                    You review illustrations for a narrated explainer video. Score from 0 to 1 how well the image matches
                    the scene brief: requested elements present, labels and numbers correct and readable, clear layout, good
                    contrast, nothing important in the bottom fifth (captions go there). List up to six short, concrete fixes.
                    Reply with JSON only: {"score": number, "issues": [string]}.
                    """), new(ChatRole.User, content)],
                new ChatOptions {
                    ResponseFormat = ChatResponseFormat.ForJsonSchema<Critique>(AIJsonUtilities.DefaultOptions, "critique",
                        "Illustration review score and fix list."),
                    MaxOutputTokens = 1024,
                    Temperature = 0
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return (null, null, "critic-failed:" + error.GetType().Name);
        }
        var critique = Parse(reply.Text);
        return critique is null ? (null, reply.ModelId, "critique-unparseable") : (critique, reply.ModelId, null);
    }

    /// <summary>Parses <c>{ "score": 0..1, "issues": [...] }</c> (fences tolerated); null when unusable.</summary>
    public static Critique? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            var root = document.RootElement;
            if (!TryGet(root, "score", out var scoreElement) || scoreElement.ValueKind != JsonValueKind.Number) return null;
            var score = scoreElement.GetDouble();
            if (!double.IsFinite(score)) return null;
            if (score is > 1 and <= 10) score /= 10; // models occasionally answer on a 0..10 scale
            score = Math.Clamp(score, 0, 1);
            var issues = TryGet(root, "issues", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!.Trim())
                    .Where(i => i.Length > 0).Select(i => i.Length <= MaxIssueCharacters ? i : i[..MaxIssueCharacters])
                    .Select(i => new string(i.Where(c => !char.IsControl(c)).ToArray())).Take(MaxIssues).ToArray()
                : [];
            return new Critique(score, issues);
        }
        catch (JsonException) { return null; }
    }

    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        return false;
    }
}
