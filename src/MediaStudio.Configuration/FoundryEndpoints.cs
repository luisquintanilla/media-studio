namespace MediaStudio.Configuration;

/// <summary>How MAI-Voice TTS is addressed on a Foundry resource (plan §2.8; P6.2b picks the final default).</summary>
public enum SpeechTtsMode
{
    /// <summary><c>{root}tts/cognitiveservices/v1</c> with a plain bearer token.</summary>
    CustomDomain,
    /// <summary><c>https://{region}.tts.speech.microsoft.com/cognitiveservices/v1</c> with <c>Bearer aad#{resourceId}#{token}</c>.</summary>
    RegionalAad
}

/// <summary>
/// Derives every service URL from ONE Foundry (AIServices) account endpoint, so configuring a single
/// resource makes chat, Anthropic, documents, OCR and speech available (plan §3.2 table).
/// Accepts the account root (<c>https://x.services.ai.azure.com/</c>), a project endpoint
/// (<c>…/api/projects/&lt;name&gt;</c>) or an already-suffixed <c>…/openai/v1</c> endpoint.
/// </summary>
public sealed class FoundryEndpoints
{
    public const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";
    public const string AzureAIScope = "https://ai.azure.com/.default";
    public const string SpeechTranscribeApiVersion = "2025-10-15";
    public const SpeechTtsMode DefaultSpeechTtsMode = SpeechTtsMode.CustomDomain;

    private static readonly string[] TrustedHostSuffixes =
        [".services.ai.azure.com", ".openai.azure.com", ".cognitiveservices.azure.com"];

    public FoundryEndpoints(string endpoint, string? region = null, string? resourceId = null)
    {
        Root = NormalizeRoot(endpoint);
        Region = string.IsNullOrWhiteSpace(region) ? null : region.Trim().ToLowerInvariant();
        ResourceId = string.IsNullOrWhiteSpace(resourceId) ? null : resourceId.Trim();
    }

    public static FoundryEndpoints From(ConnectionSettings connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Kind != ConnectionKind.Foundry || connection.Endpoint is null)
            throw new InvalidOperationException($"Connection '{connection.Id}' is not a Foundry connection with an endpoint.");
        return new(connection.Endpoint, connection.Region, connection.ResourceId);
    }

    /// <summary>Account root with a trailing slash, e.g. <c>https://x.services.ai.azure.com/</c>.</summary>
    public Uri Root { get; }
    public string? Region { get; }
    public string? ResourceId { get; }

    /// <summary>OpenAI v1 surface: chat, responses, audio, images, realtime, decisions.</summary>
    public Uri OpenAIv1 => new(Root, "openai/v1/");
    public Uri Anthropic => new(Root, "anthropic");
    public Uri DocumentIntelligence => Root;
    public Uri ContentUnderstanding => Root;
    public Uri MistralOcr => new(Root, "providers/mistral/azure/ocr");
    public Uri SpeechTts => new(Root, "tts/cognitiveservices/v1");

    public Uri SpeechTtsRegional => Region is { } region
        ? new($"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1")
        : throw new InvalidOperationException("The regional Speech endpoint needs the connection's region. Run `video-production configure azd` or set connections[].region.");

    public Uri SpeechTranscribe => new(Root, "speechtotext/transcriptions:transcribe?api-version=" + SpeechTranscribeApiVersion);

    public Uri SpeechTtsFor(SpeechTtsMode mode) => mode == SpeechTtsMode.CustomDomain ? SpeechTts : SpeechTtsRegional;

    /// <summary>Authorization header value for Speech: plain bearer on the custom domain, <c>aad#{resourceId}#{token}</c> regionally.</summary>
    public string SpeechAuthorization(string token, SpeechTtsMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (mode == SpeechTtsMode.CustomDomain) return "Bearer " + token;
        var resource = ResourceId ?? throw new InvalidOperationException(
            "Regional Speech Entra auth needs the Foundry resource id. Run `video-production configure azd` or set connections[].resourceId.");
        return $"Bearer aad#{resource}#{token}";
    }

    public static bool IsTrustedHost(string host) =>
        TrustedHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length);

    public static bool TryNormalizeRoot(string? endpoint, out Uri root, out string? error)
    {
        root = null!;
        error = null;
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri))
        {
            error = $"'{endpoint}' is not an absolute URL. Paste the Foundry endpoint, e.g. https://<account>.services.ai.azure.com/.";
            return false;
        }
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            error = $"Foundry endpoints must use HTTPS ('{uri}').";
            return false;
        }
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "Remove credentials, query strings and fragments from the Foundry endpoint.";
            return false;
        }
        if (!IsTrustedHost(uri.Host))
        {
            error = $"'{uri.Host}' is not a Foundry host. Expected *.services.ai.azure.com, *.openai.azure.com or *.cognitiveservices.azure.com.";
            return false;
        }
        var path = uri.AbsolutePath.TrimEnd('/');
        var projects = path.IndexOf("/api/projects/", StringComparison.OrdinalIgnoreCase);
        if (projects >= 0) path = path[..projects];
        foreach (var suffix in new[] { "/openai/v1", "/openai", "/anthropic" })
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { path = path[..^suffix.Length]; break; }
        if (path.Length > 0)
        {
            error = $"Unexpected path '{uri.AbsolutePath}' on the Foundry endpoint. Use the account root or a project endpoint (…/api/projects/<name>).";
            return false;
        }
        root = new UriBuilder(Uri.UriSchemeHttps, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, "/").Uri;
        return true;
    }

    public static Uri NormalizeRoot(string endpoint) =>
        TryNormalizeRoot(endpoint, out var root, out var error) ? root : throw new ArgumentException(error, nameof(endpoint));
}
