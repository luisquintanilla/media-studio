using System.IO;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using VideoProduction;
using VideoProduction.Providers;

namespace MediaStudio.App;

public sealed record ModelConnection(string Id, string Name, string Endpoint, string[] Models,
    string Authentication = "azure-cli", string Region = "", bool NativeDecisionsAvailable = false,
    string[]? ChatModels = null, string[]? SpeechModels = null, string[]? ImageModels = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    ChatProviderKind Provider = ChatProviderKind.OpenAI)
{
    public const string NativeDecisionsNote =
        "Decisions run locally with Julia by default. Native OpenAI Decisions are optional and need a deployment in a region where the Decisions API is available; Responses is not a replacement.";
    /// <summary>Keyless local OpenAI-compatible runtime (Ollama, Foundry Local, Llama.app) on a loopback <c>/v1</c> endpoint.</summary>
    public const string LocalAuthentication = "none";
    public bool IsLocal => Authentication == LocalAuthentication;
    public string NativeDecisionsStatus => Provider == ChatProviderKind.Anthropic
        ? "Anthropic is a chat provider. Native OpenAI Decisions, realtime speech and image generation require a separate OpenAI connection."
        : IsLocal ? "Local runtimes do not serve native OpenAI Decisions; Julia handles decisions locally."
        : NativeDecisionsAvailable ? "Native Decisions availability is explicitly configured for this resource."
        : "Native Decisions availability is not configured for this resource. Enable it only for a deployment in a region that supports the Decisions API.";
    public string SecretName => $"MediaStudio:Providers:{Id}:ApiKey";
    public void Validate()
    {
        if (!Regex.IsMatch(Id ?? "", @"\A[a-z][a-z0-9-]{1,39}\z") || Id == "copilot"
            || string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Name.Any(char.IsControl)
            || !Enum.IsDefined(Provider)
            || Authentication is not ("azure-cli" or "user-secrets" or LocalAuthentication) || Models is not { Length: > 0 and <= 80 }
            || Models.Distinct().Count() != Models.Length
            || Models.Any(m => string.IsNullOrWhiteSpace(m) || m.Length > 120 || m.Any(char.IsControl))
            || !Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (IsLocal
                ? Provider != ChatProviderKind.OpenAI || !uri.IsLoopback || uri.Scheme is not ("http" or "https")
                    || uri.AbsolutePath.TrimEnd('/') != "/v1"
                : uri.Scheme != "https" || (Provider == ChatProviderKind.Anthropic
                    ? uri.AbsolutePath.TrimEnd('/') != "/anthropic"
                        || !uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)
                    : uri.AbsolutePath.TrimEnd('/') != "/openai/v1"
                        || !(uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                            || uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)
                            || uri.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase))))
            || Region is null || Region.Length > 80 || Region.Any(char.IsControl))
            throw new InvalidDataException("Configure a loopback OpenAI-compatible /v1 endpoint with no key (local), or an explicit HTTPS Foundry endpoint matching its provider: OpenAI /openai/v1 or Anthropic /anthropic, with supported authentication and exact deployment names.");
        if (Provider == ChatProviderKind.Anthropic && (NativeDecisionsAvailable || SpeechModels is { Length: > 0 } || ImageModels is { Length: > 0 }))
            throw new InvalidDataException("Anthropic connections support chat/writing only, not OpenAI Decisions, realtime speech or native image generation.");
        if (IsLocal && (NativeDecisionsAvailable || SpeechModels is { Length: > 0 } || ImageModels is { Length: > 0 }))
            throw new InvalidDataException("Local runtime connections support chat/writing only; speech and decisions use Whisper, Kokoro and Julia locally.");
        foreach (var capability in new[] { ChatModels, SpeechModels, ImageModels }.OfType<string[]>())
            if (capability.Length > Models.Length || capability.Distinct().Count() != capability.Length || capability.Any(m => !Models.Contains(m)))
                throw new InvalidDataException("Capability declarations must reference exact configured deployments without duplicates.");
    }
}

public sealed record ModelSelection(string Connection, string Model);
public sealed record ModelProviderState(int SchemaVersion, long Version, ModelConnection[] Connections,
    ModelSelection? Authoring = null, ModelSelection? General = null, ModelSelection? Speech = null,
    ModelSelection? ImageFallback = null, ModelSelection? NativeDecision = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool NativeImages = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PlannerConfiguration? LocalImage = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool SpeechUnassigned = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ModelSelection? ChatIllustration = null,
    [property: System.Text.Json.Serialization.JsonIgnore] string? SpeechPermissionSha256 = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RoutedAuthoring = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RoutedIllustration = false);

/// <summary>Lends a DI-owned singleton client to an owner that disposes its client; disposal is not forwarded.</summary>
internal sealed class SharedChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    protected override void Dispose(bool disposing) { }
}

public sealed class ModelProviders
{
    public const string SecretsId = ProviderUserSecrets.Id;
    private readonly string file;
    private readonly PipelineTransport? chatTransport;
    public ModelProviderState State { get; private set; }
    public ModelProviders(string stateRoot, PipelineTransport? chatTransport = null)
    {
        this.chatTransport = chatTransport;
        file = Path.Combine(stateRoot, "settings", "model-providers.json");
        State = File.Exists(file) ? PrivateFiles.Read<ModelProviderState>(file) : new(1, 0, []);
        if (State.Connections?.All(c => c is not null) == true)
            State = State with {
                Connections = State.Connections.Select(c => c with {
                    ChatModels = c.ChatModels ?? Assigned(c.Id, State.Authoring, State.General, State.ChatIllustration),
                    SpeechModels = c.SpeechModels ?? Assigned(c.Id, State.Speech),
                    ImageModels = c.ImageModels ?? Assigned(c.Id, State.ImageFallback)
                }).ToArray()
            };
        Validate(State);
        string[] Assigned(string connection, params ModelSelection?[] roles) => roles.OfType<ModelSelection>()
            .Where(s => s.Connection == connection).Select(s => s.Model).Distinct().ToArray();
    }
    private static void Validate(ModelProviderState state)
    {
        if (state.SchemaVersion != 1 || state.Version < 0 || state.Connections is null || state.Connections.Length > 20
            || state.Connections.Any(c => c is null)
            || state.Connections.Select(c => c.Id).Distinct().Count() != state.Connections.Length)
            throw new InvalidDataException("Unsupported or duplicate model connection configuration.");
        foreach (var connection in state.Connections) connection.Validate();
        foreach (var selection in new[] { state.Authoring, state.General, state.Speech, state.ImageFallback, state.NativeDecision, state.ChatIllustration }.OfType<ModelSelection>())
            if (!state.Connections.Any(c => c.Id == selection.Connection && c.Models.Contains(selection.Model)))
                throw new InvalidDataException("A selected deployment is absent from its explicit connection.");
        foreach (var selection in new[] { state.Authoring, state.General, state.ChatIllustration }.OfType<ModelSelection>())
            if (state.Connections.Single(c => c.Id == selection.Connection).ChatModels?.Contains(selection.Model) != true)
                throw new InvalidDataException("The selected writing/illustration model needs an explicit chat capability declaration.");
        if (state.Speech is { } speech && state.Connections.Single(c => c.Id == speech.Connection).SpeechModels?.Contains(speech.Model) != true)
            throw new InvalidDataException("Speech input requires an explicit realtime-transcription deployment, not a chat or TTS model.");
        if (state.SpeechUnassigned && state.Speech is not null)
            throw new InvalidDataException("An explicitly unassigned speech input cannot also select a cloud deployment.");
        if (state.ImageFallback is { } image && state.Connections.Single(c => c.Id == image.Connection).ImageModels?.Contains(image.Model) != true)
            throw new InvalidDataException("An optional native image choice requires an explicit image-generation capability declaration.");
        if (state.NativeDecision is { } decision
            && !state.Connections.Single(c => c.Id == decision.Connection).NativeDecisionsAvailable)
            throw new InvalidDataException(state.Connections.Single(c => c.Id == decision.Connection).NativeDecisionsStatus);
        if (state.NativeImages && state.ImageFallback is null)
            throw new InvalidDataException("Choose an explicit native image deployment before selecting that backend.");
        if (state.LocalImage is { } local && (!Uri.TryCreate(local.Endpoint, UriKind.Absolute, out var localEndpoint)
            || !localEndpoint.IsLoopback || local.AllowRemote || string.IsNullOrWhiteSpace(local.Model)))
            throw new InvalidDataException("The preferred local illustration connection must remain an explicit loopback IChatClient endpoint/model.");
    }
    public void Save(ModelProviderState value)
    {
        if (value.Version != State.Version)
            throw new InvalidOperationException("Provider settings changed while this form was open. Reopen it before saving.");
        Validate(value);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        PrivateFiles.Protect(Path.GetDirectoryName(file)!, directory: true);
        using var transaction = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(file) ? PrivateFiles.Read<ModelProviderState>(file).Version != State.Version : State.Version != 0)
            throw new InvalidOperationException("Provider settings changed in another window. Reopen before replacing them.");
        value = value with { Version = State.Version + 1 };
        Json.Write(file, value); PrivateFiles.Protect(file); State = value;
    }
    public ModelConnection Connection(ModelSelection selection) =>
        State.Connections.Single(c => c.Id == selection.Connection && c.Models.Contains(selection.Model));
    private string Key(ModelConnection connection) => ProviderUserSecrets.Read(connection.SecretName);
    public static string? SpeechIdentity(ModelProviderState state)
    {
        if (state.Speech is not { } speech) return null;
        var connection = state.Connections.Single(c => c.Id == speech.Connection && c.Models.Contains(speech.Model));
        return MediaStudio.Sources.SourceJson.Fingerprint(new { speech, connection.Endpoint, connection.Authentication });
    }
    public bool SpeechUploadAllowed => State.Speech is not null;
    public PlannerConfiguration ChatIllustrationConfiguration()
    {
        var selection = State.ChatIllustration ?? throw new InvalidOperationException("Choose a chat illustration model.");
        return ChatConfiguration(selection);
    }
    public PlannerConfiguration ChatConfiguration(ModelSelection selection)
    {
        var connection = Connection(selection);
        return new(connection.Endpoint, selection.Model, null, AllowRemote: !connection.IsLocal,
            Authentication: connection.Authentication, Responses: !connection.IsLocal,
            ApiKeySecret: connection.Authentication == "user-secrets" ? connection.SecretName : null, Provider: connection.Provider);
    }
    public IChatClient Chat(ModelSelection selection, ModelProviderState? candidate = null)
    {
        if (candidate is not null) Validate(candidate);
        var connection = candidate is null ? Connection(selection)
            : candidate.Connections.Single(c => c.Id == selection.Connection && c.Models.Contains(selection.Model));
        IChatClient Create() => ChatVideoPlanner.CreateClient(new(connection.Endpoint, selection.Model, null, AllowRemote: !connection.IsLocal,
                Authentication: connection.Authentication switch { "azure-cli" => "azure-cli", ModelConnection.LocalAuthentication => ModelConnection.LocalAuthentication, _ => "api-key" },
                Provider: connection.Provider),
                transport: chatTransport, explicitApiKey: connection.Authentication == "user-secrets" ? Key(connection) : null, responses: !connection.IsLocal);
        return connection.Authentication == "user-secrets" ? new DeferredProviderChat(Create) : Create();
    }
#pragma warning disable MEAI001, OPENAI001, OPENAI002
    public IRealtimeClient Realtime(ModelSelection selection, ModelProviderState? candidate = null)
    {
        if (candidate is not null) Validate(candidate);
        var connection = candidate is null ? Connection(selection)
            : candidate.Connections.Single(c => c.Id == selection.Connection && c.Models.Contains(selection.Model));
        return connection.Authentication == "user-secrets"
            ? new DeferredProviderRealtime(() => CreateRealtime(connection, selection.Model))
            : CreateRealtime(connection, selection.Model);
    }
    private IRealtimeClient CreateRealtime(ModelConnection connection, string model)
    {
        Guard.Require(connection.Provider == ChatProviderKind.OpenAI && !connection.IsLocal, "speech.config",
            "Realtime speech requires a cloud OpenAI connection, not an Anthropic chat or local runtime connection.");
        var options = new OpenAI.Realtime.RealtimeClientOptions {
            Endpoint = new(connection.Endpoint), RetryPolicy = new ClientRetryPolicy(0),
            NetworkTimeout = TimeSpan.FromSeconds(30)
        };
        OpenAI.Realtime.RealtimeClient client;
        if (connection.Authentication == "user-secrets") client = new(new ApiKeyCredential(Key(connection)), options);
        else
        {
            var credential = MediaStudio.Configuration.CredentialFactory.CreateEntra();
            client = new EntraRealtimeClient(credential, options);
        }
        return new OpenAIRealtimeClient(client, model);
    }
    internal sealed class EntraRealtimeClient(TokenCredential credential, OpenAI.Realtime.RealtimeClientOptions options)
        : OpenAI.Realtime.RealtimeClient(new BearerTokenPolicy(credential, Scope), options)
    {
        internal const string Scope = "https://cognitiveservices.azure.com/.default";
        public override async Task<OpenAI.Realtime.RealtimeSessionClient> StartSessionAsync(string model, string intent,
            OpenAI.Realtime.RealtimeSessionClientOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(options?.ClientSecret))
                return await base.StartSessionAsync(model, intent, options, cancellationToken).ConfigureAwait(false);
            var token = await credential.GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken).ConfigureAwait(false);
            Guard.Require(!string.IsNullOrWhiteSpace(token.Token), "speech.authentication", "Cloud speech did not receive a usable access token.");
            var authenticated = new OpenAI.Realtime.RealtimeSessionClientOptions {
                QueryString = options?.QueryString, ClientSecret = options?.ClientSecret
            };
            if (options is not null)
                foreach (var header in options.Headers) authenticated.Headers[header.Key] = header.Value;
            // The SDK's WebSocket bypasses its HTTP authentication pipeline.
            authenticated.Headers["Authorization"] = "Bearer " + token.Token;
            return await base.StartSessionAsync(model, intent, authenticated, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class SuccessfulProvider<T>(Func<T> create) : IDisposable where T : class, IDisposable
    {
        private readonly object gate = new();
        private T? client;
        private bool disposed;
        public T Get()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return client ??= create();
            }
        }
        public object? GetService(Func<T, object?> lookup)
        {
            lock (gate) return client is not null && !disposed ? lookup(client) : null;
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                client?.Dispose();
            }
        }
    }

    internal sealed class DeferredProviderChat(Func<IChatClient> create) : IChatClient
    {
        private readonly SuccessfulProvider<IChatClient> client = new(create);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return client.Get().GetResponseAsync(messages, options, cancellationToken);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return client.Get().GetStreamingResponseAsync(messages, options, cancellationToken);
        }
        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this
                : client.GetService(value => value.GetService(serviceType, serviceKey));
        }
        public void Dispose() => client.Dispose();
    }

    internal sealed class DeferredProviderRealtime(Func<IRealtimeClient> create) : IRealtimeClient
    {
        private readonly SuccessfulProvider<IRealtimeClient> client = new(create);
        public Task<IRealtimeClientSession> CreateSessionAsync(RealtimeSessionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return client.Get().CreateSessionAsync(options, cancellationToken);
        }
        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this
                : client.GetService(value => value.GetService(serviceType, serviceKey));
        }
        public void Dispose() => client.Dispose();
    }
#pragma warning restore MEAI001, OPENAI001, OPENAI002
    public DecisionProviderConfiguration? NativeDecisions(ModelProviderState? candidate = null)
    {
        var state = candidate ?? State;
        Validate(state);
        if (state.NativeDecision is not { } selection) return null;
        var connection = state.Connections.Single(c => c.Id == selection.Connection);
        if (connection.Authentication != "azure-cli")
            throw new InvalidDataException("Native Decisions role uses the verified Entra connection; a user-secret choice is not silently substituted for the existing native key-file contract.");
        return new(null, new(connection.Endpoint, selection.Model, null, AllowRemote: true,
            Authentication: "azure-cli", StateAsText: true));
    }
}
