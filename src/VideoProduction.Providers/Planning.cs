using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using OpenAI;
using Azure.Identity;
using System.ClientModel.Primitives;
using Azure.Core;
using VideoProduction;

namespace VideoProduction.Providers;

public sealed record VideoConstraints(int MaxDurationSeconds = 180, int MaxScenes = 6);
[JsonConverter(typeof(TemplateKindConverter))]
public enum TemplateKind { Title, Code, Diagram }
public sealed class TemplateKindConverter() : JsonStringEnumConverter<TemplateKind>(namingPolicy: null, allowIntegerValues: false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProposedScene(TemplateKind Kind, string Title, string Text, string Script,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IllustrationPrompt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] VisualTreatment? Visual = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TeachingIntent? Teaching = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MotionVisual? Motion = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VideoOutline(string Title, ProposedScene[] Scenes);
public sealed record PlannedVideo(VideoOutline Outline, string PlanningMode, string? ClientType = null,
    string? ConfiguredModel = null, string? ReturnedModelId = null, string? OriginalPrompt = null, VideoConstraints? Constraints = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StoryboardRouteBinding? Routing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PlanningGenerationUsage? GenerationUsage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ChatRouteReceipt? ChatRouting = null);
public sealed record PlanningGenerationUsage(long? InputTokens, long? OutputTokens, long? TotalTokens, double Milliseconds);
public interface IVideoPlanner
{
    void EnsureConfigured();
    Task<PlannedVideo> Plan(string prompt, VideoConstraints constraints, CancellationToken ct);
}

public sealed class ChatVideoPlanner(IChatClient? client, string? model) : IVideoPlanner
{
    public void EnsureConfigured() => Guard.Require(client is not null && !string.IsNullOrWhiteSpace(model),
        "planner.unconfigured", "Supply a bounded typed plan with create_video (Copilot can author it from your prompt), or explicitly configure a server IChatClient endpoint/model/key file. Search remains independently available.");

    public async Task<PlannedVideo> Plan(string prompt, VideoConstraints constraints, CancellationToken ct)
    {
        EnsureConfigured();
        var instruction = """
            Plan an English narrated technical video as inert JSON data only. Never propose code execution,
            acquisition, URLs, shell commands, external assets, credentials, renderer code or filtergraphs.
            Choose only Title, Code or Diagram templates; their text is displayed inertly, never executed.
            Narration must explain the prompt, flow naturally and fit the supplied bounds at normal speaking pace.
            Do not claim that illustrations are real captures or that unverified facts were demonstrated.
            Return exactly {"title":"...","scenes":[{"kind":"Title|Code|Diagram","title":"...","text":"...","script":"..."}]}.
            Optional "teaching" may contain learningObjective, priorUnderstanding, intendedChange,
            mechanism and evidenceBoundary, each a nonempty string <=2000 characters.
            No properties beyond this shape, optional teaching, visual and motion; no markdown fencing, tool calls or paths. Keep every title <=100 characters,
            scene text <=5000 characters and scene script <=3000 characters.
            Optionally add "visual" using only the inert contract in schemas/video-outline.schema.json:
            {"preset":"architectureFlowEmphasis|progressiveCodeFocus|beforeAfterComparison",
             "anchors":[{"id":"stable-id","startLine":1,"endLine":1}],
             "reveals":[{"targetId":"stable-id","startFrame":0,"endFrame":30}]}.
            Lines are 1-based inclusive; frames are scene-local 30fps, half-open, within actual narration duration.
            Flow: Diagram only, 2..6 single-line nodes covering all text, <=48 characters per line.
            Code focus: Code only, <=14 lines of <=88 characters, 1..8 ordered nonoverlapping line ranges.
            Comparison: Code or Diagram, exactly two contiguous ranges covering all text; before then after,
            <=10 lines per panel and <=42 characters per line. Use no control characters or surrogate pairs.
            Every anchor needs one reveal in the same order, first starting at zero; intervals cannot overlap.
            Revealed anchors persist; endFrame ends emphasis, not visibility. Do not combine visual with illustrationPrompt.
            Never supply HTML, CSS, JS, expressions, renderer paths or execution directives as visual properties.
            For explicitly requested animated mechanisms, optionally use "motion" on Diagram only,
            never combined with visual or illustrationPrompt. Follow schemas/motion.schema.json:
            {"entities":[{"id":"water","shape":"circle|box|cloud|drop|sun|arrow",
            "tone":"water|warm|air|neutral","label":"short label","width":100,"height":60,
            "keys":[{"percent":0,"x":500,"y":400,"opacity":1},{"percent":100,"x":500,"y":180,"opacity":1}]}],
            "stages":[{"percent":0,"caption":"What changes and why."}]}.
            Use 1..20 unique stable lowercase entity IDs, labels <=28 characters,
            width 12..240, height 12..140, and 2..8 increasing keys per entity, explicit first 0/last 100.
            Positions stay inside a 1000x600 schematic with readable label margins; opacity is 0..1.
            Use 1..6 increasing stages starting at 0, last <=95, captions <=100 characters.
            Include a meaningful visible change, maintain entity identity, and explain the mechanism.
            Percentages resolve against actual measured narration; require at least two seconds,
            never invent timestamps or silently add holds. This is an authored schematic, not a simulation.
            Invisible water vapor is not liquid droplets: use arrows for rising moist air, cloud droplets for liquid.
            """;
        ChatResponse response;
        try
        {
            response = await client!.GetResponseAsync(
                [new ChatMessage(ChatRole.System, TeachingGuidance.Instructions + "\n\nHost contract:\n" + instruction),
                    new ChatMessage(ChatRole.User, Json.Text(new { prompt, constraints }))],
                new ChatOptions { ModelId = model, ResponseFormat = ChatResponseFormat.Json, MaxOutputTokens = 4096 }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (VideoException error) when (error.Code.StartsWith("routing.", StringComparison.Ordinal)) { throw; }
        catch (Exception)
        {
            throw new VideoException("planner.provider", "The explicitly configured chat provider failed; no alternate provider or response body is exposed.");
        }
        Guard.Require(response.Text.Length is > 0 and <= 64000 && response.Messages.All(m =>
            m.Contents.All(c => c is TextContent)), "planner.output", "Planner must return bounded text-only JSON, not tool calls or media.");
        VideoOutline outline;
        try { outline = JsonSerializer.Deserialize<VideoOutline>(response.Text, Json.Options)!; }
        catch (JsonException) { throw new VideoException("planner.output", "Planner returned invalid or unsupported video-plan data."); }
        Validate(outline, constraints);
        return new PlannedVideo(outline, "server-IChatClient", client!.GetType().FullName!, model!, response.ModelId);
    }

    public static void Validate(VideoOutline? outline, VideoConstraints constraints)
    {
        Guard.Require(outline is not null && outline.Title is { Length: > 0 and <= 100 }
            && outline.Scenes is { Length: > 0 } && outline.Scenes.Length <= constraints.MaxScenes,
            "planner.output", "Plan needs a bounded title and a nonempty scene list within requested constraints.");
        foreach (var scene in outline!.Scenes)
        {
            TeachingGuidance.Validate(scene?.Teaching);
            Guard.Require(scene is not null && Enum.IsDefined(scene.Kind) && scene.Title is { Length: > 0 and <= 100 }
                && scene.Text is { Length: <= 5000 } && !string.IsNullOrWhiteSpace(scene.Script) && scene.Script.Length <= 3000
                && (scene.IllustrationPrompt is null || !string.IsNullOrWhiteSpace(scene.IllustrationPrompt)
                    && scene.IllustrationPrompt.Length <= 2000 && !scene.IllustrationPrompt.Contains('\0'))
                && !scene.Title.Contains('\0') && !scene.Text.Contains('\0') && !scene.Script.Contains('\0'),
                "planner.output", "Plan scene violates the inert authored-template profile.");
            Guard.Require(scene!.Visual is null || scene.IllustrationPrompt is null, "visual.preset",
                "Template visual presets cannot be combined with generated media.");
            Visuals.Validate(scene.Visual, scene.Kind switch {
                TemplateKind.Title => SceneKind.Title,
                TemplateKind.Code => SceneKind.Code,
                _ => SceneKind.Diagram
            }, scene.Text);
            MotionVisuals.Validate(scene.Motion, scene.Kind == TemplateKind.Diagram ? SceneKind.Diagram : SceneKind.Title,
                scene.Visual is not null || scene.IllustrationPrompt is not null);
        }
        Guard.Require(outline.Scenes.Sum(s => s.Script.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length)
            <= constraints.MaxDurationSeconds * 3, "planner.output", "Plan speech exceeds the conservative authored word budget; no speech will be truncated.");
    }

    public static IChatClient CreateClient(PlannerConfiguration config, PipelineTransport? transport = null,
        string? explicitApiKey = null, bool responses = false, HttpClient? anthropicHttpClient = null,
        TokenCredential? anthropicCredential = null)
    {
#pragma warning disable OPENAI001
        return config.Provider switch {
            ChatProviderKind.Anthropic => FoundryAnthropic.CreateClient(
                config, explicitApiKey, anthropicHttpClient, anthropicCredential, transport),
            ChatProviderKind.OpenAI when responses || config.Responses =>
                CreateOpenAIClient(config, transport, explicitApiKey).GetResponsesClient().AsIChatClient(config.Model),
            ChatProviderKind.OpenAI => CreateOpenAIClient(config, transport, explicitApiKey).GetChatClient(config.Model).AsIChatClient(),
            _ => throw new VideoException("planner.config", "Choose an explicitly supported chat provider.")
        };
#pragma warning restore OPENAI001
    }

    public static OpenAIClient CreateOpenAIClient(PlannerConfiguration config, PipelineTransport? transport = null,
        string? explicitApiKey = null)
    {
        Guard.Require(config.Provider == ChatProviderKind.OpenAI, "planner.config",
            "This capability requires an OpenAI connection; an Anthropic chat connection cannot be substituted.");
        Guard.Require(Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme is "http" or "https" && string.IsNullOrEmpty(endpoint.UserInfo)
            && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment)
            && (endpoint.IsLoopback || config.AllowRemote && endpoint.Scheme == "https")
            && !string.IsNullOrWhiteSpace(config.Model), "planner.config",
            "Configure an explicit local OpenAI-compatible endpoint/model, or explicitly opt into a trusted HTTPS remote endpoint.");
        var clientOptions = new OpenAIClientOptions {
            Endpoint = endpoint, RetryPolicy = new ClientRetryPolicy(0),
            NetworkTimeout = TimeSpan.FromSeconds(120)
        };
        if (transport is not null) clientOptions.Transport = transport;
        if (config.Authentication == "none")
        {
            Guard.Require(endpoint!.IsLoopback && config.ApiKeyFile is null && config.ApiKeySecret is null && explicitApiKey is null,
                "planner.config", "Keyless authentication is only allowed for a local loopback runtime (Ollama, Foundry Local, llama.app).");
            return new OpenAIClient(new ApiKeyCredential("local-runtime"), clientOptions);
        }
        if (config.Authentication is "azure-cli" or "user-secrets")
        {
            Guard.Require(config.ApiKeyFile is null && explicitApiKey is null && endpoint!.Scheme == "https"
                && (endpoint.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                    || endpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
                && endpoint.AbsolutePath.TrimEnd('/') == "/openai/v1",
                "planner.config", "Cloud authentication requires an explicit trusted Azure HTTPS /openai/v1 endpoint and no key file.");
            if (config.Authentication == "user-secrets")
            {
                Guard.Require(config.ApiKeySecret is not null, "planner.config", "Choose an explicit app provider key reference.");
                return new OpenAIClient(new ApiKeyCredential(ProviderUserSecrets.Read(config.ApiKeySecret!)), clientOptions);
            }
            Guard.Require(config.ApiKeySecret is null, "planner.config", "Entra authentication cannot also select an API key.");
            var credential = MediaStudio.Configuration.CredentialFactory.CreateEntra(config.TenantId);
            var auth = new BearerTokenPolicy(credential, "https://cognitiveservices.azure.com/.default");
#pragma warning disable OPENAI001
            return new OpenAIClient(auth, clientOptions);
#pragma warning restore OPENAI001
        }
        Guard.Require(config.Authentication == "api-key" && config.ApiKeySecret is null && (config.ApiKeyFile is not null || explicitApiKey is not null)
            && !(config.ApiKeyFile is not null && explicitApiKey is not null),
            "planner.config", "Choose explicit api-key or azure-cli authentication; no fallback credential chain is used.");
        if (config.ApiKeyFile is not null) PrivateFiles.RequirePrivate(config.ApiKeyFile);
        var key = explicitApiKey ?? File.ReadAllText(config.ApiKeyFile!).Trim();
        Guard.Require(key.Length is > 0 and <= 4096 && !key.Any(char.IsControl), "planner.config", "Configured planner key file is invalid.");
        return new OpenAIClient(new ApiKeyCredential(key), clientOptions);
    }
}

public sealed class OptionalChatPlanner(PlannerConfiguration? config) : IVideoPlanner, IDisposable
{
    private IChatClient? client;
    public void EnsureConfigured() => Guard.Require(config is not null, "planner.unconfigured",
        "Supply a bounded typed plan with create_video (Copilot can author it from your prompt), or explicitly configure a server IChatClient endpoint/model/key file. Search remains independently available.");
    public Task<PlannedVideo> Plan(string prompt, VideoConstraints constraints, CancellationToken ct)
    {
        EnsureConfigured();
        client ??= ChatVideoPlanner.CreateClient(config!);
        return new ChatVideoPlanner(client, config!.Model).Plan(prompt, constraints, ct);
    }
    public void Dispose() => client?.Dispose();
}
