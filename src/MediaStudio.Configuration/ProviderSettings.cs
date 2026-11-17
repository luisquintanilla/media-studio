using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaStudio.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<ConnectionKind>))]
public enum ConnectionKind
{
    [JsonStringEnumMemberName("openai-compatible")] OpenAICompatible,
    [JsonStringEnumMemberName("foundry")] Foundry,
    [JsonStringEnumMemberName("github-copilot")] GitHubCopilot
}

[JsonConverter(typeof(JsonStringEnumConverter<AuthMode>))]
public enum AuthMode
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("entra")] Entra,
    [JsonStringEnumMemberName("api-key")] ApiKey,
    /// <summary>GitHub Copilot device-flow sign-in (the CLI's stored login). Copilot connections only.</summary>
    [JsonStringEnumMemberName("logged-in-user")] LoggedInUser,
    /// <summary>GitHub Copilot token from COPILOT_GITHUB_TOKEN / GH_TOKEN / gh. Copilot connections only.</summary>
    [JsonStringEnumMemberName("env-token")] EnvToken
}

[JsonConverter(typeof(JsonStringEnumConverter<ChatApi>))]
public enum ChatApi
{
    [JsonStringEnumMemberName("chat-completions")] ChatCompletions,
    [JsonStringEnumMemberName("responses")] Responses,
    [JsonStringEnumMemberName("anthropic")] Anthropic
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentRuntime>))]
public enum AgentRuntime
{
    [JsonStringEnumMemberName("maf")] Maf,
    [JsonStringEnumMemberName("copilot")] Copilot
}

[JsonConverter(typeof(JsonStringEnumConverter<CopilotBackend>))]
public enum CopilotBackend
{
    [JsonStringEnumMemberName("subscription")] Subscription,
    [JsonStringEnumMemberName("byok")] Byok
}

[JsonConverter(typeof(JsonStringEnumConverter<TtsProvider>))]
public enum TtsProvider
{
    [JsonStringEnumMemberName("kokoro")] Kokoro,
    [JsonStringEnumMemberName("azure-speech")] AzureSpeech
}

[JsonConverter(typeof(JsonStringEnumConverter<SttProvider>))]
public enum SttProvider
{
    [JsonStringEnumMemberName("whisper")] Whisper,
    [JsonStringEnumMemberName("azure-speech")] AzureSpeech
}

[JsonConverter(typeof(JsonStringEnumConverter<LiveSpeechProvider>))]
public enum LiveSpeechProvider
{
    [JsonStringEnumMemberName("whisper-ptt")] WhisperPushToTalk,
    [JsonStringEnumMemberName("foundry-openai")] FoundryOpenAI
}

[JsonConverter(typeof(JsonStringEnumConverter<ImageMode>))]
public enum ImageMode
{
    [JsonStringEnumMemberName("svg")] Svg,
    [JsonStringEnumMemberName("png")] Png
}

[JsonConverter(typeof(JsonStringEnumConverter<JuliaMode>))]
public enum JuliaMode
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("on")] On,
    [JsonStringEnumMemberName("off")] Off
}

/// <summary>Well-known chat tier names (§3.2). Tiers are ordered weakest → strongest in <see cref="ChatSettings.Tiers"/>.</summary>
public static class ChatTierNames
{
    public const string Fast = "fast";
    public const string Balanced = "balanced";
    public const string Cloud = "cloud";
    public const string Strong = "strong";
    public const string Copilot = "copilot";
}

/// <summary>Settings file v2 (plan §3.2). API keys are never part of this document.</summary>
public sealed record ProviderSettings
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public PrivacySettings Privacy { get; init; } = new();
    public ConnectionSettings[] Connections { get; init; } = [];
    public ChatSettings Chat { get; init; } = new();
    public AgentSettings Agents { get; init; } = new();
    public RoleModel? Vision { get; init; }
    public RoleModel? Embeddings { get; init; }
    public DocumentSettings Documents { get; init; } = new();
    public SpeechSettings Speech { get; init; } = new();
    public ImageSettings Images { get; init; } = new();
    public DecisionSettings Decisions { get; init; } = new();

    public ConnectionSettings? FindConnection(string? id) =>
        id is null ? null : Connections.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    public ChatTier? FindTier(string name) =>
        Chat.Tiers.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>True when the referenced connection sends data off this machine.</summary>
    public bool IsCloud(string? connectionId) => FindConnection(connectionId)?.IsCloud ?? false;
}

public sealed record PrivacySettings
{
    /// <summary>Master switch. False means no cloud tier, cloud document extractor or cloud decision is ever selected.</summary>
    public bool AllowCloud { get; init; }
}

public sealed record ConnectionSettings
{
    public required string Id { get; init; }
    public required ConnectionKind Kind { get; init; }
    public string? Endpoint { get; init; }
    public AuthMode Auth { get; init; } = AuthMode.None;
    public string? TenantId { get; init; }
    public string? Region { get; init; }
    public string? ResourceId { get; init; }
    /// <summary>Required for a non-loopback openai-compatible endpoint; such a connection counts as cloud.</summary>
    public bool AllowRemote { get; init; }

    [JsonIgnore]
    public bool IsCloud => Kind switch {
        ConnectionKind.OpenAICompatible => !(Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback),
        _ => true
    };
}

public sealed record ChatTier
{
    public required string Name { get; init; }
    public required string Connection { get; init; }
    public required string Model { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatApi? Api { get; init; }
    /// <summary>
    /// Lets a reasoning model think on OpenAI-compatible (local) connections. Off by default: Ollama ignores
    /// output-token limits while a model thinks, so unbounded reasoning can stall a turn for minutes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Thinking { get; init; }
}

public sealed record ChatRoutingSettings
{
    public string Decider { get; init; } = "auto";
    public double ConfidenceFloor { get; init; } = 0.6;
    public string AuthoringFloor { get; init; } = ChatTierNames.Balanced;
    public bool Failover { get; init; } = true;
}

public sealed record ChatSettings
{
    public ChatTier[] Tiers { get; init; } = [];
    public ChatRoutingSettings Routing { get; init; } = new();
}

public sealed record AgentRuntimeSettings
{
    public AgentRuntime Runtime { get; init; } = AgentRuntime.Maf;
}

public sealed record CopilotAgentSettings
{
    public CopilotBackend Backend { get; init; } = CopilotBackend.Subscription;
    public string? Model { get; init; }
    public string? ByokConnection { get; init; }
}

public sealed record AgentSettings
{
    public AgentRuntimeSettings Authoring { get; init; } = new();
    public AgentRuntimeSettings Illustration { get; init; } = new();
    public CopilotAgentSettings Copilot { get; init; } = new();
}

public sealed record RoleModel
{
    public required string Connection { get; init; }
    public required string Model { get; init; }
}

public static class DocumentCandidates
{
    public const string PdfPig = "pdfpig";
    public const string Vlm = "vlm";
    public const string DocumentIntelligence = "document-intelligence";
    public const string ContentUnderstanding = "content-understanding";
    public const string Mistral = "mistral";
    public static readonly string[] All = [PdfPig, Vlm, DocumentIntelligence, ContentUnderstanding, Mistral];
    public static bool IsCloud(string candidate) => candidate is DocumentIntelligence or ContentUnderstanding or Mistral;
}

public sealed record DocumentSettings
{
    public string[] Candidates { get; init; } = [DocumentCandidates.PdfPig, DocumentCandidates.Vlm];
    public string Decider { get; init; } = "auto";
    public string MistralDeployment { get; init; } = "ocr";
}

public sealed record TtsSettings
{
    public TtsProvider Provider { get; init; } = TtsProvider.Kokoro;
    public string? Voice { get; init; } = "af_heart";
    public double Speed { get; init; } = 1.0;
    /// <summary>Kokoro asset quality: "quantized" (kokoro-v1.0-q8) or "fp32" (adds kokoro-v1.0-fp32).</summary>
    public string? Quality { get; init; } = "quantized";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Connection { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Style { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Instructions { get; init; }
}

public sealed record SttSettings
{
    public SttProvider Provider { get; init; } = SttProvider.Whisper;
    /// <summary>Whisper: "large-v3-turbo" or "base"; Azure Speech: e.g. "MAI-Transcribe-2".</summary>
    public string? Model { get; init; } = "large-v3-turbo";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Connection { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Diarization { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Style { get; init; }
}

public sealed record LiveSpeechSettings
{
    public LiveSpeechProvider Provider { get; init; } = LiveSpeechProvider.WhisperPushToTalk;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Connection { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Deployment { get; init; }
}

public sealed record SpeechSettings
{
    public TtsSettings Tts { get; init; } = new();
    public SttSettings Stt { get; init; } = new();
    public LiveSpeechSettings Live { get; init; } = new();
}

public sealed record CritiqueSettings
{
    public bool Enabled { get; init; } = true;
    public double Threshold { get; init; } = 0.7;
    public int MaxRetries { get; init; } = 1;
}

public sealed record ImageSettings
{
    public ImageMode Mode { get; init; } = ImageMode.Svg;
    /// <summary>Foundry image deployment used only when <see cref="Mode"/> is png.</summary>
    public string Deployment { get; init; } = "image";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Connection { get; init; }
    public bool Grounding { get; init; } = true;
    public bool Exemplars { get; init; } = true;
    public CritiqueSettings Critique { get; init; } = new();
}

public sealed record DecisionSettings
{
    public JuliaMode Julia { get; init; } = JuliaMode.Auto;
    /// <summary>Foundry deployment for OpenAI Decisions (used only when cloud is allowed).</summary>
    public string CloudDeployment { get; init; } = "chat";
}

public static class ProviderSettingsJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Serialize(ProviderSettings settings) => JsonSerializer.Serialize(settings, Options);

    public static ProviderSettings Deserialize(string json) =>
        JsonSerializer.Deserialize<ProviderSettings>(json, Options)
        ?? throw new InvalidDataException("The settings file is empty.");
}
