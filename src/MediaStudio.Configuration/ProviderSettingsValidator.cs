namespace MediaStudio.Configuration;

public sealed record SettingsError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

public sealed class ProviderSettingsValidationException(IReadOnlyList<SettingsError> errors)
    : InvalidOperationException("Provider settings are invalid:" + Environment.NewLine +
        string.Join(Environment.NewLine, errors.Select(e => "  - " + e)) + Environment.NewLine +
        "Fix the file, run `video-production configure local`, or reopen the Setup wizard.")
{
    public IReadOnlyList<SettingsError> Errors { get; } = errors;
}

/// <summary>Plan P3.2 rules. Returns every problem with a JSON path and an actionable message.</summary>
public static class ProviderSettingsValidator
{
    private static readonly string[] KnownTierNames =
        [ChatTierNames.Fast, ChatTierNames.Balanced, ChatTierNames.Cloud, ChatTierNames.Strong, ChatTierNames.Copilot];

    public static IReadOnlyList<SettingsError> Validate(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<SettingsError>();
        void Error(string path, string message) => errors.Add(new(path, message));

        if (settings.SchemaVersion != ProviderSettings.CurrentSchemaVersion)
            Error("schemaVersion", $"Expected {ProviderSettings.CurrentSchemaVersion}; found {settings.SchemaVersion}. Older files are migrated automatically when loaded through ProviderSettingsStore.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < settings.Connections.Length; i++)
        {
            var connection = settings.Connections[i];
            var path = $"connections[{i}]";
            if (string.IsNullOrWhiteSpace(connection.Id) || connection.Id.Length > 64 ||
                !connection.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                Error(path + ".id", "Use 1-64 letters, digits, '-' or '_'.");
            else if (!ids.Add(connection.Id))
                Error(path + ".id", $"Duplicate connection id '{connection.Id}'.");
            ValidateConnection(connection, path, Error);
        }

        var tierNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < settings.Chat.Tiers.Length; i++)
        {
            var tier = settings.Chat.Tiers[i];
            var path = $"chat.tiers[{i}]";
            if (!tierNames.Add(tier.Name)) Error(path + ".name", $"Duplicate tier '{tier.Name}'.");
            if (!KnownTierNames.Contains(tier.Name)) Error(path + ".name", $"Unknown tier '{tier.Name}'. Use one of: {string.Join(", ", KnownTierNames)}.");
            if (string.IsNullOrWhiteSpace(tier.Model)) Error(path + ".model", "A model (Ollama tag or Foundry deployment name) is required.");
            var connection = RequireConnection(settings, tier.Connection, path + ".connection", Error);
            if (connection is null) continue;
            if (tier.Api is ChatApi.Anthropic or ChatApi.Responses && connection.Kind != ConnectionKind.Foundry)
                Error(path + ".api", $"'{Describe(tier.Api.Value)}' is only available on Foundry connections.");
            if (connection.Kind == ConnectionKind.GitHubCopilot && tier.Name != ChatTierNames.Copilot)
                Error(path + ".name", "GitHub Copilot connections can only back the 'copilot' tier.");
        }
        if (settings.Chat.Tiers.Length == 0)
            Error("chat.tiers", "At least one chat tier is required. Run `video-production configure local`.");
        if (!KnownTierNames.Contains(settings.Chat.Routing.AuthoringFloor))
            Error("chat.routing.authoringFloor", $"Unknown tier '{settings.Chat.Routing.AuthoringFloor}'.");
        if (settings.Chat.Routing.ConfidenceFloor is < 0 or > 1)
            Error("chat.routing.confidenceFloor", "Use a value between 0 and 1.");

        if (settings.Vision is { } vision) ValidateRoleModel(settings, vision, "vision", Error);
        if (settings.Embeddings is { } embeddings) ValidateRoleModel(settings, embeddings, "embeddings", Error);

        foreach (var candidate in settings.Documents.Candidates)
            if (!DocumentCandidates.All.Contains(candidate))
                Error("documents.candidates", $"Unknown document extractor '{candidate}'. Use: {string.Join(", ", DocumentCandidates.All)}.");
        if (settings.Documents.Candidates.Any(DocumentCandidates.IsCloud) && !settings.Connections.Any(c => c.Kind == ConnectionKind.Foundry))
            Error("documents.candidates", "Cloud document extractors need a Foundry connection. Run `video-production configure azd` or remove them.");

        ValidateSpeech(settings, Error);
        ValidateAgents(settings, Error);

        if (settings.Images.Mode == ImageMode.Png)
        {
            var image = settings.Images.Connection is { } id ? settings.FindConnection(id) : settings.Connections.FirstOrDefault(c => c.Kind == ConnectionKind.Foundry);
            if (image is null || image.Kind != ConnectionKind.Foundry)
                Error("images.mode", "PNG illustrations need a Foundry connection with an image deployment. Use \"svg\" for local.");
        }
        if (settings.Images.Critique.Threshold is < 0 or > 1) Error("images.critique.threshold", "Use a value between 0 and 1.");
        if (settings.Images.Critique.MaxRetries is < 0 or > 5) Error("images.critique.maxRetries", "Use 0-5.");

        return errors;
    }

    public static void ThrowIfInvalid(ProviderSettings settings)
    {
        var errors = Validate(settings);
        if (errors.Count > 0) throw new ProviderSettingsValidationException(errors);
    }

    /// <summary>Validates a single connection (used by the wizard before saving a pasted endpoint).</summary>
    public static IReadOnlyList<SettingsError> ValidateConnection(ConnectionSettings connection)
    {
        var errors = new List<SettingsError>();
        ValidateConnection(connection, "connection", (path, message) => errors.Add(new(path, message)));
        return errors;
    }

    private static void ValidateConnection(ConnectionSettings connection, string path, Action<string, string> error)
    {
        switch (connection.Kind)
        {
            case ConnectionKind.OpenAICompatible:
                if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    error(path + ".endpoint", "An http(s) endpoint is required, e.g. http://localhost:11434/v1 for Ollama.");
                    break;
                }
                if (uri.UserInfo.Length > 0 || uri.Query.Length > 0)
                    error(path + ".endpoint", "Remove credentials and query strings from the endpoint.");
                if (!uri.IsLoopback && !connection.AllowRemote)
                    error(path + ".allowRemote", $"'{uri.Host}' is not loopback. Set \"allowRemote\": true to use a remote OpenAI-compatible server (it then counts as cloud).");
                if (!uri.IsLoopback && uri.Scheme != Uri.UriSchemeHttps)
                    error(path + ".endpoint", "Remote OpenAI-compatible endpoints must use HTTPS.");
                if (connection.Auth is not (AuthMode.None or AuthMode.ApiKey))
                    error(path + ".auth", "OpenAI-compatible connections support \"none\" or \"api-key\".");
                break;
            case ConnectionKind.Foundry:
                if (!FoundryEndpoints.TryNormalizeRoot(connection.Endpoint, out _, out var foundryError))
                    error(path + ".endpoint", foundryError!);
                if (connection.Auth is not (AuthMode.Entra or AuthMode.ApiKey))
                    error(path + ".auth", "Foundry connections support \"entra\" (recommended) or \"api-key\".");
                if (connection.TenantId is { Length: > 0 } tenant && !Guid.TryParse(tenant, out _) && !tenant.Contains('.'))
                    error(path + ".tenantId", "Use a tenant GUID or domain.");
                if (connection.Region is { Length: > 0 } region && !region.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
                    error(path + ".region", "Use the Azure region name, e.g. eastus2.");
                if (connection.ResourceId is { Length: > 0 } resourceId &&
                    !resourceId.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
                    error(path + ".resourceId", "Use the full ARM resource id (/subscriptions/…/providers/Microsoft.CognitiveServices/accounts/<name>).");
                break;
            case ConnectionKind.GitHubCopilot:
                if (connection.Endpoint is not null) error(path + ".endpoint", "GitHub Copilot connections have no endpoint.");
                if (connection.Auth is not (AuthMode.LoggedInUser or AuthMode.EnvToken))
                    error(path + ".auth", "GitHub Copilot connections support \"logged-in-user\" or \"env-token\"; tokens are never stored.");
                break;
        }
    }

    private static void ValidateRoleModel(ProviderSettings settings, RoleModel role, string path, Action<string, string> error)
    {
        if (string.IsNullOrWhiteSpace(role.Model)) error(path + ".model", "A model is required.");
        var connection = RequireConnection(settings, role.Connection, path + ".connection", error);
        if (connection?.Kind == ConnectionKind.GitHubCopilot) error(path + ".connection", "GitHub Copilot cannot serve this role.");
    }

    private static void ValidateSpeech(ProviderSettings settings, Action<string, string> error)
    {
        var speech = settings.Speech;
        if (speech.Tts.Provider != TtsProvider.Kokoro)
            RequireFoundry(settings, speech.Tts.Connection, "speech.tts.connection", error);
        if (speech.Tts.Speed is < 0.5 or > 2.0) error("speech.tts.speed", "Use a speed between 0.5 and 2.0.");
        if (speech.Tts.Provider == TtsProvider.Kokoro && speech.Tts.Quality is not (null or "quantized" or "fp32"))
            error("speech.tts.quality", "Use \"quantized\" or \"fp32\".");
        if (speech.Stt.Provider != SttProvider.Whisper)
            RequireFoundry(settings, speech.Stt.Connection, "speech.stt.connection", error);
        else if (speech.Stt.Model is not (null or "large-v3-turbo" or "base"))
            error("speech.stt.model", "Whisper supports \"large-v3-turbo\" or \"base\".");
        if (speech.Live.Provider == LiveSpeechProvider.FoundryOpenAI)
        {
            RequireFoundry(settings, speech.Live.Connection, "speech.live.connection", error);
            if (string.IsNullOrWhiteSpace(speech.Live.Deployment))
                error("speech.live.deployment", "Live transcription needs a realtime deployment (e.g. \"transcribe-live\").");
        }
    }

    private static void ValidateAgents(ProviderSettings settings, Action<string, string> error)
    {
        var agents = settings.Agents;
        var usesCopilot = agents.Authoring.Runtime == AgentRuntime.Copilot || agents.Illustration.Runtime == AgentRuntime.Copilot;
        if (agents.Copilot.Backend == CopilotBackend.Byok)
        {
            if (agents.Copilot.ByokConnection is null)
            {
                if (usesCopilot) error("agents.copilot.byokConnection", "BYOK needs a connection id (e.g. \"local\" or \"azure\").");
            }
            else
            {
                var connection = RequireConnection(settings, agents.Copilot.ByokConnection, "agents.copilot.byokConnection", error);
                if (connection?.Kind == ConnectionKind.GitHubCopilot)
                    error("agents.copilot.byokConnection", "BYOK must point at an OpenAI-compatible or Foundry connection.");
                if (usesCopilot && connection is { IsCloud: true } && !settings.Privacy.AllowCloud)
                    error("agents.copilot.byokConnection", $"Connection '{connection.Id}' is cloud but privacy.allowCloud is false.");
            }
        }
        else if (usesCopilot && !settings.Privacy.AllowCloud)
        {
            error("agents", "The Copilot runtime with the subscription backend sends prompts to GitHub. Turn on privacy.allowCloud or use backend \"byok\" with a local connection.");
        }
    }

    private static void RequireFoundry(ProviderSettings settings, string? id, string path, Action<string, string> error)
    {
        var connection = RequireConnection(settings, id, path, error);
        if (connection is not null && connection.Kind != ConnectionKind.Foundry)
            error(path, $"Connection '{connection.Id}' must be a Foundry connection for this provider.");
    }

    private static ConnectionSettings? RequireConnection(ProviderSettings settings, string? id, string path, Action<string, string> error)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            error(path, "A connection id is required.");
            return null;
        }
        var connection = settings.FindConnection(id);
        if (connection is null)
            error(path, $"No connection with id '{id}'. Known: {(settings.Connections.Length == 0 ? "(none)" : string.Join(", ", settings.Connections.Select(c => c.Id)))}.");
        return connection;
    }

    private static string Describe(ChatApi api) => api switch {
        ChatApi.Anthropic => "anthropic",
        ChatApi.Responses => "responses",
        _ => "chat-completions"
    };
}
