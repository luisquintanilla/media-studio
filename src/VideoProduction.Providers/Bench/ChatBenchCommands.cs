using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaStudio.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using VideoProduction.Providers.Illustrations;

namespace VideoProduction.Providers.Bench;

/// <summary>One author-labelled routing case (a copy of <c>tests/MediaStudio.Tests/Routing/routing-fixtures-v1.json</c>).</summary>
public sealed record BenchRoutingCase(string Id, string Purpose, string Prompt, string Expected,
    bool Json = false, bool Tools = false, int? MaxOutputTokens = null, int Padding = 0);

/// <summary>Scene shape every authoring prompt must return.</summary>
public sealed record BenchScene(string Title, string Narration, string Visual);

/// <summary>Storyboard shape every authoring prompt must return.</summary>
public sealed record BenchStoryboard(string Title, BenchScene[] Scenes);

/// <summary>Quality and speed of one model on the bench prompts.</summary>
public sealed record ModelBenchResult(string Model, int RoutingCases, int RoutingAgreed, int AuthoringPrompts, int JsonValid,
    double? JudgeScore, double? TokensPerSecond, double MedianSeconds, double? FirstTokenSeconds, string? Error = null)
{
    public double? RoutingAgreement => RoutingCases == 0 ? null : (double)RoutingAgreed / RoutingCases;
    public double? JsonValidRate => AuthoringPrompts == 0 ? null : (double)JsonValid / AuthoringPrompts;
}

/// <summary>Quality and speed of the illustration pipeline on one brief.</summary>
public sealed record ImageBenchResult(string Brief, bool Valid, double Seconds, double? CritiqueScore, bool? CritiquePassed,
    int CritiqueAttempts, int Facts, string? Output, string? Error = null);

/// <summary>
/// <c>bench chat</c>, <c>bench image</c> and <c>bench local</c>: measure the configured chat tiers, the illustration pipeline and
/// candidate Ollama models on routing agreement, storyboard JSON validity, an optional judge score and tokens/s.
/// </summary>
public static class ChatBenchCommands
{
    public const string Usage = """
        Usage:
          bench chat  [--tiers <a,b>] [--prompts <n>] [--out <dir>] [--settings <file>] [--models <dir>]
          bench image [--briefs <n>] [--out <dir>] [--settings <file>] [--models <dir>]
          bench local [--candidates <tag,tag>] [--endpoint <url>] [--prompts <n>] [--judge <tier>|--no-judge] [--out <dir>] [--settings <file>]

        bench chat   times every configured chat tier (cloud tiers only when privacy.allowCloud is on).
        bench image  runs short briefs through the illustration pipeline (grounding, SVG sanitizer, critique).
        bench local  runs the routing fixtures and 10 storyboard-authoring prompts against each local candidate
                     (default: every chat model installed in Ollama) and, unless --no-judge, scores the drafts with the
                     strongest configured tier. Results are evidence for docs/models.md, not code defaults.
        """;

    public const int DefaultChatPrompts = 3;
    public const int DefaultImageBriefs = 3;

    public static readonly IReadOnlyList<BenchRoutingCase> RoutingCases =
    [
        new("greeting", "chat", "Hi! What can you do?", "fast"),
        new("rename-title", "chat", "Suggest a shorter title for 'Intro to sourdough baking'.", "fast"),
        new("define-term", "chat", "What does B-roll mean?", "fast"),
        new("fix-typo", "chat", "Fix the typo: 'Welcom to the show'.", "fast"),
        new("count-scenes", "chat", "How many scenes are in a 30 second teaser, roughly?", "fast"),
        new("caption-short", "illustration", "One-line caption for a picture of a lighthouse.", "fast"),
        new("json-outline", "chat", "Return a JSON outline of five scenes about tidal energy.", "strong", Json: true),
        new("storyboard", "authoring", "Write a storyboard for a three minute explainer on how vaccines train the immune system.", "strong"),
        new("long-summary", "chat", "Summarize the following transcript and keep every quoted figure exact.", "strong", Padding: 7000),
        new("tool-call", "chat", "Add a title scene and a diagram scene to my draft.", "strong", Tools: true),
        new("long-output", "chat", "Draft a detailed narration script.", "strong", MaxOutputTokens: 8000),
        new("document-authoring", "document-authoring", "Turn the attached report into a narrated summary video.", "strong"),
        new("subtle-reasoning", "chat", "Is it misleading to say correlation proves causation if the sample is randomized? Explain carefully.", "strong"),
        new("constraint-heavy", "chat", "Rewrite this intro without changing any numbers, do not add claims, keep it under 40 words.", "strong")
    ];

    /// <summary>Storyboard-authoring prompts written for this project.</summary>
    public static readonly IReadOnlyList<string> AuthoringPrompts =
    [
        "A 60 second explainer on how tides are caused by the moon and the sun.",
        "A three scene teaser for a community garden open day.",
        "How a bill becomes a law, for high school students, in five scenes.",
        "A product walkthrough of a reusable water bottle with a built-in filter.",
        "Why the sky is blue, for curious ten year olds.",
        "A safety briefing on lifting heavy boxes at a warehouse.",
        "The water cycle in four scenes with one diagram.",
        "A recipe video for a quick vegetable stir-fry.",
        "An onboarding welcome for new volunteers at a public library.",
        "How solar panels turn sunlight into electricity, with one chart of daily output."
    ];

    /// <summary>Illustration briefs written for this project.</summary>
    public static readonly IReadOnlyList<string> ImageBriefs =
    [
        "A lighthouse on a rocky shore at dusk with a beam of light.",
        "A simple bar chart of three rising bars with an upward arrow.",
        "A sprouting seed in soil with a small sun above it.",
        "A bicycle next to a tree in a park.",
        "Three gears turning together."
    ];

    public static bool Handles(string[] args) => args is { Length: > 1 } && args[0] == "bench" && args[1] is "chat" or "image" or "local";

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default,
        IEnumerable<ITierChatClientFactory>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        if (!Handles(args) || args.Skip(2).Any(a => a is "-h" or "--help")) { output.WriteLine(Usage); return Handles(args) ? 0 : 2; }
        Options options;
        try { options = Options.Parse(args.Skip(2)); }
        catch (ArgumentException error) { output.WriteLine(error.Message); output.WriteLine(Usage); return 2; }
        var settings = (options.Value("--settings") is { } file ? new ProviderSettingsStore(file) : new ProviderSettingsStore()).TryLoad()
            ?? new ProviderSettings();
        var outDirectory = Path.GetFullPath(options.Value("--out")
            ?? Path.Combine(MediaStudioPaths.Root, "bench", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(outDirectory);
        try
        {
            return args[1] switch {
                "chat" => await ChatAsync(settings, options, outDirectory, extensions, output, cancellationToken).ConfigureAwait(false),
                "image" => await ImageAsync(settings, options, outDirectory, output, cancellationToken).ConfigureAwait(false),
                _ => await LocalAsync(settings, options, outDirectory, extensions, output, cancellationToken).ConfigureAwait(false)
            };
        }
        catch (FormatException error) { output.WriteLine(error.Message); output.WriteLine(Usage); return 2; }
    }

    private static async Task<int> ChatAsync(ProviderSettings settings, Options options, string outDirectory,
        IEnumerable<ITierChatClientFactory>? extensions, TextWriter output, CancellationToken cancellationToken)
    {
        var wanted = List(options.Value("--tiers"));
        var tiers = settings.Chat.Tiers.Where(t => wanted is null || wanted.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (tiers.Length == 0)
        {
            output.WriteLine("No chat tiers configured" + (wanted is null ? "; run `configure` first." : " with those names."));
            return 2;
        }
        var prompts = Count(options.Value("--prompts"), DefaultChatPrompts, AuthoringPrompts.Count);
        var results = new List<ModelBenchResult>();
        foreach (var tier in tiers)
        {
            var label = $"{tier.Name} ({tier.Model})";
            if (settings.FindConnection(tier.Connection) is { IsCloud: true } && !settings.Privacy.AllowCloud)
            {
                output.WriteLine($"{label}: skipped (cloud tier and privacy.allowCloud is off).");
                continue;
            }
            output.WriteLine($"Benchmarking {label} ...");
            try
            {
                using var client = ProviderFactory.CreateTierClient(settings, tier, extensions);
                results.Add(await MeasureAsync(label, client, null, routing: false, prompts, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                results.Add(Failed(label, error));
            }
        }
        Print(results, output);
        var path = Path.Combine(outDirectory, "bench-chat.json");
        await WriteAsync(path, results, cancellationToken).ConfigureAwait(false);
        output.WriteLine($"Results: {MediaStudio.Configuration.Diagnostics.Friendly(path)}");
        return results.Count > 0 && results.All(r => r.Error is not null) ? 1 : 0;
    }

    private static async Task<int> LocalAsync(ProviderSettings settings, Options options, string outDirectory,
        IEnumerable<ITierChatClientFactory>? extensions, TextWriter output, CancellationToken cancellationToken)
    {
        var endpoint = new Uri((options.Value("--endpoint") ?? ProviderSettingsDefaults.OllamaEndpoint.AbsoluteUri).TrimEnd('/') + "/");
        var candidates = List(options.Value("--candidates"));
        if (candidates is null)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                candidates = [.. await OllamaChatModelsAsync(http, new Uri(endpoint, "/"), cancellationToken).ConfigureAwait(false)];
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
            {
                output.WriteLine($"Could not list Ollama models at {new Uri(endpoint, "/")} ({error.GetType().Name}); pass --candidates a,b.");
                return 2;
            }
        }
        if (candidates.Length == 0) { output.WriteLine("No local chat models found; pull one with `ollama pull` or pass --candidates."); return 2; }
        var prompts = Count(options.Value("--prompts"), AuthoringPrompts.Count, AuthoringPrompts.Count);
        var local = new ProviderSettings {
            Connections = [new ConnectionSettings { Id = "bench-local", Kind = ConnectionKind.OpenAICompatible, Endpoint = endpoint.AbsoluteUri.TrimEnd('/') }]
        };
        using var judge = options.Has("--no-judge") ? null : CreateJudge(settings, options.Value("--judge"), extensions, output);
        output.WriteLine("OCR pages: skipped (document extraction benches arrive with the documents work).");
        var results = new List<ModelBenchResult>();
        foreach (var model in candidates)
        {
            output.WriteLine($"Benchmarking {model} ...");
            try
            {
                using var client = ProviderFactory.CreateTierClient(local, new ChatTier { Name = "bench", Connection = "bench-local", Model = model });
                results.Add(await MeasureAsync(model, client, judge, routing: true, prompts, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                results.Add(Failed(model, error));
            }
        }
        Print(results, output);
        var path = Path.Combine(outDirectory, "bench-local.json");
        await WriteAsync(path, results, cancellationToken).ConfigureAwait(false);
        output.WriteLine("Routing = agreement with the author-labelled fast/strong fixtures (hypotheses, not measured quality).");
        output.WriteLine($"Results: {MediaStudio.Configuration.Diagnostics.Friendly(path)}");
        return results.All(r => r.Error is not null) ? 1 : 0;
    }

    private static async Task<int> ImageAsync(ProviderSettings settings, Options options, string outDirectory, TextWriter output,
        CancellationToken cancellationToken)
    {
        var count = Count(options.Value("--briefs"), DefaultImageBriefs, ImageBriefs.Count);
        var services = new ServiceCollection();
        // A bench-only exemplar library so benchmark drawings never enter the user's library.
        var modelId = IllustrationPipeline.EmbeddingModelId(settings);
        services.AddSingleton(sp => new IllustrationSources(modelId is not null && settings.Images.Exemplars
            && sp.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(MediaStudioAIKeys.Embedding) is { } embedding
                ? new SvgExemplarLibrary(embedding, modelId, Path.Combine(outDirectory, "svg-library")) : null));
        services.AddMediaStudioAI(settings, TryAssets(options.Value("--models")));
        await using var provider = services.BuildServiceProvider();
        var generator = provider.GetKeyedService<IImageGenerator>(MediaStudioAIKeys.Illustration);
        if (generator is null)
        {
            output.WriteLine("No illustration generator configured: add a chat tier (run `configure`), or png mode with a Foundry connection and cloud allowed.");
            return 2;
        }
        var grounding = provider.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(MediaStudioAIKeys.Embedding) is not null && settings.Images.Grounding;
        var critique = provider.GetKeyedService<IChatClient>(MediaStudioAIKeys.Vision) is not null && settings.Images.Critique.Enabled;
        output.WriteLine($"Pipeline: mode {settings.Images.Mode}, grounding {(grounding ? "on" : "off")}, critique {(critique ? "on" : "off")}.");
        var results = new List<ImageBenchResult>();
        for (var i = 0; i < count; i++)
        {
            var r = await MeasureImageAsync(generator, ImageBriefs[i], Path.Combine(outDirectory, $"image-{i + 1:00}"), cancellationToken).ConfigureAwait(false);
            results.Add(r);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{i + 1,2}. {(r.Valid ? "valid" : "INVALID"),-7} {r.Seconds,6:0.0}s  critique {Format(r.CritiqueScore, "0.00"),5} ({r.CritiqueAttempts} attempt(s))  facts {r.Facts}  {r.Output ?? r.Error}"));
        }
        var valid = results.Count(r => r.Valid);
        var scores = results.Where(r => r.CritiqueScore is not null).Select(r => r.CritiqueScore!.Value).ToArray();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Valid {valid}/{results.Count} ({(double)valid / results.Count:P0}); mean critique {(scores.Length == 0 ? "n/a" : scores.Average().ToString("0.00", CultureInfo.InvariantCulture))}; median {Median(results.Select(r => r.Seconds)):0.0}s."));
        var path = Path.Combine(outDirectory, "bench-image.json");
        await WriteAsync(path, results, cancellationToken).ConfigureAwait(false);
        output.WriteLine($"Results: {MediaStudio.Configuration.Diagnostics.Friendly(path)}");
        return valid == 0 ? 1 : 0;
    }

    /// <summary>Generates one brief and reads the receipts the grounding and critique decorators attach.</summary>
    public static async Task<ImageBenchResult> MeasureImageAsync(IImageGenerator generator, string brief, string outputPathWithoutExtension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var watch = Stopwatch.StartNew();
        try
        {
            var response = await generator.GenerateAsync(new ImageGenerationRequest(brief), new ImageGenerationOptions {
                AdditionalProperties = new() { [IllustrationProperties.Brief] = brief }
            }, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            var image = response.Contents.OfType<DataContent>().FirstOrDefault();
            var critique = image?.AdditionalProperties?.TryGetValue(IllustrationProperties.Critique, out var c) == true ? c as CritiqueReceipt : null;
            var grounding = image?.AdditionalProperties?.TryGetValue(IllustrationProperties.Grounding, out var g) == true ? g as GroundingReceipt : null;
            string? path = null;
            var valid = false;
            if (IllustrationProperties.Svg(response) is { } svg)
            {
                try { SvgSanitizer.Sanitize(svg); valid = true; }
                catch (VideoException) { valid = false; }
                path = outputPathWithoutExtension + ".svg";
                await File.WriteAllTextAsync(path, svg, cancellationToken).ConfigureAwait(false);
            }
            else if (image is { Data.Length: > 0 })
            {
                valid = true;
                path = outputPathWithoutExtension + (image.MediaType == "image/png" ? ".png" : ".bin");
                await File.WriteAllBytesAsync(path, image.Data.ToArray(), cancellationToken).ConfigureAwait(false);
            }
            return new(brief, valid, watch.Elapsed.TotalSeconds, critique?.Score, critique?.Passed, critique?.Attempts.Length ?? 0,
                grounding?.Facts ?? 0, path);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(brief, false, watch.Elapsed.TotalSeconds, null, null, 0, 0, null, $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// Runs the routing fixtures (when <paramref name="routing"/>) and the first <paramref name="prompts"/> authoring prompts
    /// against <paramref name="model"/>; <paramref name="judge"/> scores each valid storyboard 0–1.
    /// </summary>
    public static async Task<ModelBenchResult> MeasureAsync(string name, IChatClient model, IChatClient? judge, bool routing, int prompts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var firstToken = await FirstTokenAsync(model, cancellationToken).ConfigureAwait(false);
        var agreed = 0;
        if (routing)
            foreach (var item in RoutingCases)
            {
                var response = await model.GetResponseAsync(
                    [new(ChatRole.System, RouterInstructions), new(ChatRole.User, Describe(item))],
                    new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema<RouteAnswer>(AIJsonUtilities.DefaultOptions), MaxOutputTokens = 64, Temperature = 0 },
                    cancellationToken).ConfigureAwait(false);
                if (TryDeserialize<RouteAnswer>(response.Text)?.Tier?.Trim().Equals(item.Expected, StringComparison.OrdinalIgnoreCase) == true)
                    agreed++;
            }
        var valid = 0;
        long outputTokens = 0;
        double tokenSeconds = 0;
        var seconds = new List<double>();
        var scores = new List<double>();
        foreach (var prompt in AuthoringPrompts.Take(prompts))
        {
            var watch = Stopwatch.StartNew();
            var response = await model.GetResponseAsync(
                [new(ChatRole.System, AuthoringInstructions), new(ChatRole.User, prompt)],
                new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema<BenchStoryboard>(AIJsonUtilities.DefaultOptions), MaxOutputTokens = 2048, Temperature = 0 },
                cancellationToken).ConfigureAwait(false);
            watch.Stop();
            seconds.Add(watch.Elapsed.TotalSeconds);
            if (response.Usage?.OutputTokenCount is { } tokens) { outputTokens += tokens; tokenSeconds += watch.Elapsed.TotalSeconds; }
            if (!ValidStoryboard(response.Text)) continue;
            valid++;
            if (judge is not null && await JudgeAsync(judge, prompt, response.Text, cancellationToken).ConfigureAwait(false) is { } score)
                scores.Add(score);
        }
        return new(name, routing ? RoutingCases.Count : 0, agreed, seconds.Count, valid, scores.Count == 0 ? null : scores.Average(),
            tokenSeconds > 0 ? outputTokens / tokenSeconds : null, Median(seconds), firstToken);
    }

    /// <summary>True when <paramref name="json"/> is a storyboard with 1–20 scenes and no empty fields.</summary>
    public static bool ValidStoryboard(string? json) =>
        TryDeserialize<BenchStoryboard>(json) is { Title.Length: > 0, Scenes.Length: > 0 and <= 20 } board
        && board.Scenes.All(s => s is not null && !string.IsNullOrWhiteSpace(s.Title)
            && !string.IsNullOrWhiteSpace(s.Narration) && !string.IsNullOrWhiteSpace(s.Visual));

    /// <summary>Installed Ollama chat models (embedding models are skipped), from <c>/api/tags</c>.</summary>
    public static async Task<IReadOnlyList<string>> OllamaChatModelsAsync(HttpClient http, Uri api, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(api);
        using var stream = await http.GetStreamAsync(new Uri(api, "api/tags"), cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return [];
        return models.EnumerateArray()
            .Where(m => !IsEmbedding(m))
            .Select(m => m.TryGetProperty("name", out var n) ? n.GetString() : null)
            .OfType<string>().Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsEmbedding(JsonElement model)
    {
        var name = model.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (name.Contains("embed", StringComparison.OrdinalIgnoreCase)) return true;
        return model.TryGetProperty("details", out var details) && details.TryGetProperty("families", out var families)
            && families.ValueKind == JsonValueKind.Array
            && families.EnumerateArray().Any(f => f.GetString() is { } family && family.Contains("bert", StringComparison.OrdinalIgnoreCase));
    }

    private const string RouterInstructions = """
        You route requests between two models.
        fast: small local model for short answers, quick edits and simple questions.
        strong: strongest model for hard authoring, long structured output, tool use, long inputs and careful reasoning.
        Reply only with JSON {"tier":"fast"} or {"tier":"strong"}.
        """;

    private const string AuthoringInstructions = """
        You write storyboards for short narrated videos. Return only JSON: {"title": string, "scenes": [{"title": string,
        "narration": string, "visual": string}]} with 3 to 8 scenes. Narration is one or two spoken sentences; visual
        describes what is on screen.
        """;

    private sealed record RouteAnswer(string? Tier);
    private sealed record JudgeAnswer(double Score, string[]? Issues);

    private static string Describe(BenchRoutingCase item)
    {
        var lines = new List<string> { $"Purpose: {item.Purpose}", $"Request: {item.Prompt}" };
        if (item.Padding > 0) lines.Add($"Attached text: {item.Padding} characters.");
        if (item.Json) lines.Add("The answer must be JSON.");
        if (item.Tools) lines.Add("The model must call editing tools.");
        if (item.MaxOutputTokens is { } max) lines.Add($"Expected answer length: up to {max} tokens.");
        return string.Join('\n', lines);
    }

    private static async Task<double?> FirstTokenAsync(IChatClient model, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        await foreach (var update in model.GetStreamingResponseAsync([new(ChatRole.User, "In one short sentence, what is B-roll?")],
                           new ChatOptions { MaxOutputTokens = 64, Temperature = 0 }, cancellationToken).ConfigureAwait(false))
            if (!string.IsNullOrEmpty(update.Text)) return watch.Elapsed.TotalSeconds;
        return null;
    }

    private static async Task<double?> JudgeAsync(IChatClient judge, string prompt, string storyboard, CancellationToken cancellationToken)
    {
        try
        {
            var response = await judge.GetResponseAsync(
            [
                new(ChatRole.System, "You grade storyboards for short narrated videos. Score 0-10 for accuracy, coverage of the request, "
                    + "clear narration and filmable visuals. Reply only with JSON {\"score\": number, \"issues\": [string]}."),
                new(ChatRole.User, $"Request: {prompt}\n\nStoryboard:\n{storyboard}")
            ], new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema<JudgeAnswer>(AIJsonUtilities.DefaultOptions), MaxOutputTokens = 512, Temperature = 0 },
                cancellationToken).ConfigureAwait(false);
            return CritiquingImageGenerator.Parse(response.Text)?.Score;
        }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    private static IChatClient? CreateJudge(ProviderSettings settings, string? tierName, IEnumerable<ITierChatClientFactory>? extensions, TextWriter output)
    {
        var tiers = ProviderFactory.CreateTiers(settings, extensions)
            .Where(t => t.UnavailableReason is null && (!t.IsCloud || settings.Privacy.AllowCloud)).ToArray();
        var tier = tierName is null ? tiers.LastOrDefault() : tiers.FirstOrDefault(t => t.Name.Equals(tierName, StringComparison.OrdinalIgnoreCase));
        if (tier is null)
        {
            output.WriteLine(tierName is null ? "Judge: none (no usable chat tier configured); scores are omitted."
                : $"Judge: tier '{tierName}' is not configured or not allowed; scores are omitted.");
            return null;
        }
        output.WriteLine($"Judge: {tier.Name} ({tier.Model}).");
        return ProviderFactory.CreateTierClient(settings, settings.Chat.Tiers.First(t => t.Name == tier.Name), extensions);
    }

    private static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var text = json.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var start = text.IndexOf('\n');
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start > 0 && end > start) text = text[(start + 1)..end];
        }
        try { return JsonSerializer.Deserialize<T>(text, AIJsonUtilities.DefaultOptions); }
        catch (JsonException) { return null; }
    }

    private static ModelBenchResult Failed(string name, Exception error) =>
        new(name, 0, 0, 0, 0, null, null, 0, null, $"{error.GetType().Name}: {error.Message}");

    private static void Print(IReadOnlyList<ModelBenchResult> results, TextWriter output)
    {
        output.WriteLine($"{"model",-32} {"routing",8} {"json ok",8} {"judge",6} {"tok/s",7} {"median s",9} {"first s",8}");
        foreach (var r in results)
            output.WriteLine(r.Error is not null ? $"{r.Model,-32} failed: {r.Error}" : string.Create(CultureInfo.InvariantCulture,
                $"{r.Model,-32} {Format(r.RoutingAgreement, "P0"),8} {Format(r.JsonValidRate, "P0"),8} {Format(r.JudgeScore, "0.00"),6} {Format(r.TokensPerSecond, "0.0"),7} {r.MedianSeconds,9:0.0} {Format(r.FirstTokenSeconds, "0.00"),8}"));
    }

    private static string Format(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? "n/a";

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private static Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);

    private static ModelAssets? TryAssets(string? directory)
    {
        try { return File.Exists(ModelManifest.DefaultPath) ? new ModelAssets(ModelManifest.LoadDefault(), directory) : null; }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { return null; }
    }

    private static string[]? List(string? value) =>
        value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } items ? items : null;

    private static int Count(string? value, int fallback, int max) =>
        value is null ? fallback
        : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 ? Math.Min(n, max)
        : throw new FormatException($"Expected a positive whole number, got '{value}'.");

    private sealed class Options
    {
        private static readonly string[] Flags = ["--no-judge"];
        private static readonly string[] Valued = ["--tiers", "--prompts", "--briefs", "--candidates", "--endpoint", "--judge", "--out", "--settings", "--models"];
        private readonly Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            using var enumerator = args.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var name = enumerator.Current;
                if (Flags.Contains(name, StringComparer.OrdinalIgnoreCase)) options.values[name] = null;
                else if (Valued.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!enumerator.MoveNext()) throw new ArgumentException($"{name} needs a value.");
                    options.values[name] = enumerator.Current;
                }
                else throw new ArgumentException($"Unknown option '{name}'.");
            }
            return options;
        }

        public bool Has(string name) => values.ContainsKey(name);
        public string? Value(string name) => values.TryGetValue(name, out var value) ? value : null;
    }
}
