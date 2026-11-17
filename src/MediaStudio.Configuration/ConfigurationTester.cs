using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;

namespace MediaStudio.Configuration;

public enum RoleCheckStatus { Ok, Failed, Skipped }

public sealed record RoleCheck(string Role, RoleCheckStatus Status, string Detail, string? Remediation = null)
{
    public bool Ok => Status == RoleCheckStatus.Ok;
    public string Symbol => Status switch { RoleCheckStatus.Ok => "✅", RoleCheckStatus.Failed => "❌", _ => "⏭️" };
}

/// <summary>
/// <c>configure test</c>: a tiny probe per role. Local roles are checked for real (model pulled + 1-token chat /
/// 1 embedding against Ollama, model assets verified). Cloud roles are skipped unless <c>privacy.allowCloud</c>;
/// otherwise they get a credential check and a model-list call, and degrade to ❌ with a hint when offline.
/// </summary>
/// <summary>
/// Extra <c>configure test</c> rows from a runtime the configuration layer cannot reference (e.g. the GitHub Copilot
/// agent in <c>MediaStudio.Agents</c>). Implementations must honor <c>privacy.allowCloud</c> and never throw for
/// "not installed" or "not signed in"; they return failed rows with remediation instead.
/// </summary>
public interface IConfigurationCheck
{
    Task<IReadOnlyList<RoleCheck>> CheckAsync(ProviderSettings settings, bool cloudOnly, CancellationToken cancellationToken);
}

public sealed class ConfigurationTester(
    HttpClient? httpClient = null,
    ModelAssets? assets = null,
    Func<ConnectionSettings, TokenCredential>? credentials = null,
    Func<string, string?>? environment = null,
    IEnumerable<IConfigurationCheck>? extensions = null)
{
    public const string CopilotChecksUnavailable =
        "GitHub Copilot is checked by Media Studio (MediaStudio.exe --configure test) or Settings → Setup → Test.";
    public const string TroubleshootingDoc = "docs/troubleshooting.md";
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CloudTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _http = httpClient ?? new HttpClient();
    private readonly Func<ConnectionSettings, TokenCredential> _credentials = credentials ?? CredentialFactory.CreateTokenCredential;
    private readonly Dictionary<string, Task<RoleCheck>> _connectionChecks = [];
    private readonly Dictionary<string, Task<RuntimeProbe>> _probes = [];

    public async Task<IReadOnlyList<RoleCheck>> TestAsync(ProviderSettings settings, bool cloudOnly = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var checks = new List<RoleCheck>();
        foreach (var tier in settings.Chat.Tiers)
            await Add($"chat:{tier.Name}", tier.Connection, () => ChatAsync(settings, tier, cancellationToken)).ConfigureAwait(false);
        if (settings.Vision is { } vision)
            await Add("vision", vision.Connection, () => ModelAsync(settings, "vision", vision, cancellationToken)).ConfigureAwait(false);
        if (settings.Embeddings is { } embeddings)
            await Add("embeddings", embeddings.Connection, () => EmbeddingAsync(settings, embeddings, cancellationToken)).ConfigureAwait(false);

        if (!cloudOnly) checks.Add(new("documents:pdfpig", RoleCheckStatus.Ok, "built in"));
        foreach (var candidate in settings.Documents.Candidates.Where(DocumentCandidates.IsCloud))
            await Add($"documents:{candidate}", FirstFoundry(settings), () => CloudConnectionAsync(settings, FirstFoundry(settings)!, cancellationToken)).ConfigureAwait(false);

        var speech = settings.Speech;
        if (speech.Tts.Provider == TtsProvider.Kokoro)
        {
            if (!cloudOnly) checks.Add(AssetCheck("speech:tts (kokoro)", speech.Tts.Quality == "fp32" ? "kokoro-v1.0-fp32" : "kokoro-v1.0-q8"));
            if (!cloudOnly) checks.Add(LocalRuntimeDetector.FindEspeakNg() is { } espeak
                ? new("speech:tts espeak-ng", RoleCheckStatus.Ok, espeak)
                : new("speech:tts espeak-ng", RoleCheckStatus.Failed, "not found; Kokoro needs espeak-ng to turn text into phonemes",
                    "Install espeak-ng (Windows: winget install eSpeak-NG.eSpeak-NG; macOS: brew install espeak-ng; Linux: apt install espeak-ng) and put it on PATH."));
        }
        else await Add($"speech:tts ({Name(speech.Tts.Provider)})", speech.Tts.Connection, () => CloudConnectionAsync(settings, speech.Tts.Connection!, cancellationToken)).ConfigureAwait(false);
        if (speech.Stt.Provider == SttProvider.Whisper)
        {
            if (!cloudOnly) checks.Add(AssetCheck("speech:stt (whisper)", speech.Stt.Model == "base" ? "whisper-base" : "whisper-large-v3-turbo-q5"));
        }
        else await Add($"speech:stt ({Name(speech.Stt.Provider)})", speech.Stt.Connection, () => CloudConnectionAsync(settings, speech.Stt.Connection!, cancellationToken)).ConfigureAwait(false);
        if (speech.Live.Provider == LiveSpeechProvider.FoundryOpenAI)
            await Add("speech:live", speech.Live.Connection, () => CloudConnectionAsync(settings, speech.Live.Connection!, cancellationToken)).ConfigureAwait(false);

        if (!cloudOnly && settings.Decisions.Julia != JuliaMode.Off)
        {
            var julia = AssetCheck("decisions:julia", "julia-1");
            checks.Add(julia.Ok || settings.Decisions.Julia == JuliaMode.On ? julia
                : julia with { Status = RoleCheckStatus.Ok, Detail = "not installed; the heuristic decider is used", Remediation = julia.Remediation });
        }
        if (settings.Images.Mode == ImageMode.Png && (settings.Images.Connection ?? FirstFoundry(settings)) is { } imageConnection)
            await Add("images:png", imageConnection, () => CloudConnectionAsync(settings, imageConnection, cancellationToken)).ConfigureAwait(false);

        foreach (var extension in extensions ?? [])
            checks.AddRange(await extension.CheckAsync(settings, cloudOnly, cancellationToken).ConfigureAwait(false));
        return checks;

        async Task Add(string role, string? connection, Func<Task<RoleCheck>> probe)
        {
            var cloud = settings.IsCloud(connection);
            if (cloudOnly && !cloud) return;
            if (cloud && !settings.Privacy.AllowCloud)
            {
                checks.Add(new(role, RoleCheckStatus.Skipped, "cloud is off (privacy.allowCloud = false)"));
                return;
            }
            var result = await probe().ConfigureAwait(false);
            checks.Add(result with { Role = role });
        }
    }

    private static string? FirstFoundry(ProviderSettings settings) => settings.Connections.FirstOrDefault(c => c.Kind == ConnectionKind.Foundry)?.Id;

    private RoleCheck AssetCheck(string role, string id)
    {
        try
        {
            var store = assets ?? new ModelAssets(ModelManifest.LoadDefault());
            var status = store.List().FirstOrDefault(s => s.Asset.Id == id);
            return status?.State switch {
                ModelAssetState.Verified => new(role, RoleCheckStatus.Ok, $"{id} verified at {status.Directory}"),
                ModelAssetState.Present => new(role, RoleCheckStatus.Ok, $"{id} present (not yet hash-verified)", $"video-production assets verify --id {id}"),
                null => new(role, RoleCheckStatus.Failed, $"{id} is not in the model manifest"),
                _ => new(role, RoleCheckStatus.Failed, $"{id} is {status.State.ToString().ToLowerInvariant()}{(status.Detail is null ? "" : ": " + status.Detail)}",
                    $"video-production assets ensure --id {id}")
            };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(role, RoleCheckStatus.Failed, exception.Message, "Rebuild so assets/model-manifest.json is copied next to the app.");
        }
    }

    private async Task<RoleCheck> ChatAsync(ProviderSettings settings, ChatTier tier, CancellationToken cancellationToken)
    {
        var connection = settings.FindConnection(tier.Connection)!;
        return connection.Kind switch {
            ConnectionKind.OpenAICompatible => await LocalChatAsync(connection, tier.Model, cancellationToken).ConfigureAwait(false),
            ConnectionKind.Foundry => await CloudConnectionAsync(settings, connection.Id, cancellationToken).ConfigureAwait(false),
            _ => new("", RoleCheckStatus.Skipped, extensions is null ? CopilotChecksUnavailable : "see the copilot.* rows")
        };
    }

    private async Task<RoleCheck> ModelAsync(ProviderSettings settings, string role, RoleModel model, CancellationToken cancellationToken)
    {
        var connection = settings.FindConnection(model.Connection)!;
        if (connection.Kind != ConnectionKind.OpenAICompatible) return await CloudConnectionAsync(settings, connection.Id, cancellationToken).ConfigureAwait(false);
        var probe = await ProbeAsync(connection, cancellationToken).ConfigureAwait(false);
        return MissingModel(probe, model.Model) ?? new(role, RoleCheckStatus.Ok, $"{model.Model} available on {probe.Name}");
    }

    private async Task<RoleCheck> LocalChatAsync(ConnectionSettings connection, string model, CancellationToken cancellationToken)
    {
        var probe = await ProbeAsync(connection, cancellationToken).ConfigureAwait(false);
        if (MissingModel(probe, model) is { } missing) return missing;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LocalTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(probe.Endpoint, "chat/completions")) {
                Content = JsonContent.Create(new { model, max_tokens = 1, messages = new[] { new { role = "user", content = "Reply with OK." } } })
            };
            AddLocalKey(connection, request);
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new("", RoleCheckStatus.Ok, $"{model}: 1-token chat succeeded")
                : new("", RoleCheckStatus.Failed, $"{model}: chat returned HTTP {(int)response.StatusCode}", $"ollama run {model} \"hi\" to see the error");
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new("", RoleCheckStatus.Failed, $"{model}: chat did not complete ({exception.GetType().Name})", "Is Ollama still loading the model? Retry, or pick a smaller tier.");
        }
    }

    private async Task<RoleCheck> EmbeddingAsync(ProviderSettings settings, RoleModel model, CancellationToken cancellationToken)
    {
        var connection = settings.FindConnection(model.Connection)!;
        if (connection.Kind != ConnectionKind.OpenAICompatible) return await CloudConnectionAsync(settings, connection.Id, cancellationToken).ConfigureAwait(false);
        var probe = await ProbeAsync(connection, cancellationToken).ConfigureAwait(false);
        if (MissingModel(probe, model.Model) is { } missing) return missing;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LocalTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(probe.Endpoint, "embeddings")) {
                Content = JsonContent.Create(new { model = model.Model, input = "hello" })
            };
            AddLocalKey(connection, request);
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new("", RoleCheckStatus.Ok, $"{model.Model}: embedding succeeded")
                : new("", RoleCheckStatus.Failed, $"{model.Model}: embeddings returned HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new("", RoleCheckStatus.Failed, $"{model.Model}: embedding did not complete ({exception.GetType().Name})");
        }
    }

    private void AddLocalKey(ConnectionSettings connection, HttpRequestMessage request)
    {
        if (connection.Auth == AuthMode.ApiKey)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CredentialFactory.GetApiKey(connection.Id, environment));
    }

    private static RoleCheck? MissingModel(RuntimeProbe probe, string model)
    {
        if (!probe.Available)
            return new("", RoleCheckStatus.Failed, $"{probe.Name} at {probe.Endpoint} is {probe.Detail ?? "not reachable"}",
                "Install and start Ollama: winget install Ollama.Ollama (then reopen the terminal).");
        if (!probe.HasModel(model))
            return new("", RoleCheckStatus.Failed, $"{model} is not pulled", $"ollama pull {model}");
        return null;
    }

    private Task<RuntimeProbe> ProbeAsync(ConnectionSettings connection, CancellationToken cancellationToken)
    {
        lock (_probes)
        {
            if (!_probes.TryGetValue(connection.Id, out var task))
            {
                var name = connection.Id == ProviderSettingsDefaults.LocalConnectionId ? "Ollama" : connection.Id;
                task = new LocalRuntimeDetector(_http).ProbeAsync(name, new Uri(connection.Endpoint!), cancellationToken);
                _probes[connection.Id] = task;
            }
            return task;
        }
    }

    /// <summary>One credential + <c>GET openai/v1/models</c> check per cloud connection, cached across roles.</summary>
    private Task<RoleCheck> CloudConnectionAsync(ProviderSettings settings, string connectionId, CancellationToken cancellationToken)
    {
        lock (_connectionChecks)
        {
            if (!_connectionChecks.TryGetValue(connectionId, out var task))
                _connectionChecks[connectionId] = task = CheckCloudAsync(settings.FindConnection(connectionId)!, cancellationToken);
            return task;
        }
    }

    private async Task<RoleCheck> CheckCloudAsync(ConnectionSettings connection, CancellationToken cancellationToken)
    {
        if (connection.Kind == ConnectionKind.GitHubCopilot)
            return new("", RoleCheckStatus.Skipped, extensions is null ? CopilotChecksUnavailable : "see the copilot.* rows");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CloudTimeout);
        Uri models;
        HttpRequestMessage request;
        try
        {
            if (connection.Kind == ConnectionKind.Foundry)
            {
                models = new Uri(FoundryEndpoints.From(connection).OpenAIv1, "models");
                request = new HttpRequestMessage(HttpMethod.Get, models);
                if (connection.Auth == AuthMode.ApiKey)
                    request.Headers.Add("api-key", CredentialFactory.GetApiKey(connection.Id, environment));
                else
                {
                    var token = await _credentials(connection)
                        .GetTokenAsync(new TokenRequestContext([FoundryEndpoints.CognitiveServicesScope]), timeout.Token).ConfigureAwait(false);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                }
            }
            else
            {
                models = new Uri(new Uri(connection.Endpoint!.TrimEnd('/') + "/"), "models");
                request = new HttpRequestMessage(HttpMethod.Get, models);
                if (connection.Auth == AuthMode.ApiKey)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CredentialFactory.GetApiKey(connection.Id, environment));
            }
        }
        catch (InvalidOperationException exception)
        {
            return new("", RoleCheckStatus.Failed, exception.Message, "Set the key as described, or switch the connection to Entra.");
        }
        catch (Exception exception) when (exception is Azure.Identity.AuthenticationFailedException or Azure.Identity.CredentialUnavailableException ||
                                          (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new("", RoleCheckStatus.Failed, $"No Entra token for '{connection.Id}': {FirstLine(exception.Message)}",
                "Sign in with `az login` or `azd auth login` (add --tenant <id> if the resource is in another tenant).");
        }

        try
        {
            using (request)
            using (var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false))
            {
                return response.IsSuccessStatusCode
                    ? new("", RoleCheckStatus.Ok, $"'{connection.Id}' reachable and authorized ({models.Host})")
                    : new("", RoleCheckStatus.Failed, $"'{connection.Id}' returned HTTP {(int)response.StatusCode} for {models.AbsolutePath}",
                        (int)response.StatusCode is 401 or 403
                            ? "Grant yourself 'Cognitive Services User' (azd up does this) and wait a few minutes for RBAC to propagate."
                            : $"See {TroubleshootingDoc}.");
            }
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new("", RoleCheckStatus.Failed, $"'{connection.Id}' unreachable ({models.Host}): {FirstLine(exception.Message)}",
                "Check your network/VPN, or turn off cloud to work fully offline.");
        }
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private static string Name<T>(T value) where T : struct, Enum => value.ToString();
}
