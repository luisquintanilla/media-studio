using System.Text.Json;
using System.Text.Json.Nodes;

namespace MediaStudio.Configuration;

public sealed record MigrationResult(ProviderSettings Settings, IReadOnlyList<string> Changes);

/// <summary>
/// Maps the v1 app settings (<c>schemaVersion: 1</c>, Foundry-only connections with role selections) to v2 (plan P3.5).
/// Azure hosts become <c>foundry</c> connections (one per account root), loopback endpoints become
/// <c>openai-compatible</c>, Authoring → <c>strong</c> tier, General → <c>cloud</c> tier, Speech → live
/// <c>foundry-openai</c>, ImageFallback → PNG illustrations, NativeDecision → <c>decisions.cloudDeployment</c>.
/// </summary>
public static class ProviderSettingsMigrator
{
    public const string BackupFileName = "model-providers.v1.bak.json";

    public static int ReadSchemaVersion(JsonNode document) =>
        Get(document, "schemaVersion") is JsonValue value && value.TryGetValue<int>(out var version) ? version : 0;

    /// <param name="v1">The parsed v1 document.</param>
    /// <param name="baseline">Local-first defaults the migrated cloud choices are layered onto.</param>
    public static MigrationResult Migrate(JsonNode v1, ProviderSettings baseline)
    {
        ArgumentNullException.ThrowIfNull(v1);
        ArgumentNullException.ThrowIfNull(baseline);
        var version = ReadSchemaVersion(v1);
        if (version != 1) throw new InvalidDataException($"Expected a v1 settings document; found schemaVersion {version}.");

        var changes = new List<string>();
        var connections = baseline.Connections.ToList();
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var providerOf = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var node in Get(v1, "connections") as JsonArray ?? [])
        {
            if (node is null) continue;
            var id = Text(node, "id") ?? throw new InvalidDataException("A v1 connection has no id.");
            var endpoint = Text(node, "endpoint") ?? "";
            var provider = Text(node, "provider") ?? "openAI";
            providerOf[id] = provider;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            {
                changes.Add($"Skipped v1 connection '{id}': endpoint '{endpoint}' is not a URL.");
                continue;
            }
            if (uri.IsLoopback)
            {
                var localId = EnsureLocal(connections, uri, changes);
                idMap[id] = localId;
                continue;
            }
            if (!FoundryEndpoints.TryNormalizeRoot(endpoint, out var root, out var error))
            {
                changes.Add($"Skipped v1 connection '{id}': {error}");
                continue;
            }
            var auth = Text(node, "authentication") == "user-secrets" ? AuthMode.ApiKey : AuthMode.Entra;
            var existing = connections.FirstOrDefault(c => c.Kind == ConnectionKind.Foundry &&
                FoundryEndpoints.TryNormalizeRoot(c.Endpoint, out var r, out _) && r == root);
            if (existing is not null)
            {
                idMap[id] = existing.Id;
                changes.Add($"Merged v1 connection '{id}' ({provider}) into Foundry connection '{existing.Id}' (same account {root.Host}).");
                continue;
            }
            var newId = connections.Any(c => c.Id == id) ? id + "-foundry" : id;
            var region = Text(node, "region");
            connections.Add(new ConnectionSettings {
                Id = newId,
                Kind = ConnectionKind.Foundry,
                Endpoint = root.AbsoluteUri,
                Auth = auth,
                Region = string.IsNullOrWhiteSpace(region) ? null : region.ToLowerInvariant()
            });
            idMap[id] = newId;
            changes.Add($"v1 connection '{id}' ({uri.Host}) → Foundry connection '{newId}' at {root} with {(auth == AuthMode.Entra ? "Entra (DefaultAzureCredential)" : "api-key")} auth.");
            if (auth == AuthMode.ApiKey)
                changes.Add($"API key for '{newId}' now reads user-secret '{CredentialFactory.ApiKeyUserSecretName(newId)}' or env {CredentialFactory.ApiKeyEnvironmentVariable(newId)} (was 'MediaStudio:Providers:{id}:ApiKey').");
        }

        var tiers = baseline.Chat.Tiers.ToList();
        void AddTier(string name, JsonNode? selection, string role)
        {
            if (selection is null) return;
            var connection = Text(selection, "connection");
            var model = Text(selection, "model");
            if (connection is null || model is null || !idMap.TryGetValue(connection, out var mapped))
            {
                changes.Add($"Dropped v1 {role} selection: its connection was not migrated.");
                return;
            }
            var target = connections.First(c => c.Id == mapped);
            var api = target.Kind != ConnectionKind.Foundry ? (ChatApi?)null
                : string.Equals(providerOf.GetValueOrDefault(connection), "anthropic", StringComparison.OrdinalIgnoreCase) ? ChatApi.Anthropic : ChatApi.Responses;
            tiers.RemoveAll(t => t.Name == name);
            tiers.Add(new ChatTier { Name = name, Connection = mapped, Model = model, Api = api });
            changes.Add($"v1 {role} ({connection}/{model}) → chat tier '{name}'.");
        }
        AddTier(ChatTierNames.Cloud, Get(v1, "general"), "General");
        AddTier(ChatTierNames.Strong, Get(v1, "authoring"), "Authoring");
        if (Get(v1, "chatIllustration") is not null)
            changes.Add("v1 ChatIllustration selection is now served by the chat router's 'illustration' role; no separate setting is kept.");

        var settings = baseline with { Connections = [.. connections], Chat = baseline.Chat with { Tiers = [.. OrderTiers(tiers)] } };

        if (Get(v1, "speech") is { } speech && Text(speech, "model") is { } liveModel &&
            Text(speech, "connection") is { } speechConnection && idMap.TryGetValue(speechConnection, out var liveConnection))
        {
            settings = settings with {
                Speech = settings.Speech with { Live = new LiveSpeechSettings { Provider = LiveSpeechProvider.FoundryOpenAI, Connection = liveConnection, Deployment = liveModel } }
            };
            changes.Add($"v1 Speech ({speechConnection}/{liveModel}) → speech.live foundry-openai.");
        }

        if (Get(v1, "imageFallback") is { } image && Text(image, "model") is { } imageModel &&
            Text(image, "connection") is { } imageConnection && idMap.TryGetValue(imageConnection, out var mappedImage))
        {
            var png = Get(v1, "nativeImages") is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
            settings = settings with { Images = settings.Images with { Deployment = imageModel, Connection = mappedImage, Mode = png ? ImageMode.Png : ImageMode.Svg } };
            changes.Add($"v1 ImageFallback ({imageModel}) → images.deployment{(png ? " with mode png" : " (mode stays svg)")}.");
        }

        if (Get(v1, "nativeDecision") is { } decision && Text(decision, "model") is { } decisionModel)
        {
            settings = settings with { Decisions = settings.Decisions with { CloudDeployment = decisionModel } };
            changes.Add($"v1 NativeDecision ({decisionModel}) → decisions.cloudDeployment.");
        }

        if (Get(v1, "localImage") is { } localImage && Text(localImage, "endpoint") is { } localEndpoint &&
            Uri.TryCreate(localEndpoint, UriKind.Absolute, out var localUri) && localUri.IsLoopback)
        {
            settings = settings with { Connections = [.. EnsureLocalList(settings.Connections, localUri, changes)] };
        }

        if (settings.Connections.Any(c => c.IsCloud) && tiers.Any(t => settings.IsCloud(t.Connection)))
        {
            settings = settings with { Privacy = settings.Privacy with { AllowCloud = true } };
            changes.Add("privacy.allowCloud = true because the v1 file selected cloud deployments. Turn it off in Settings to stay local-only.");
        }

        return new(settings, changes);
    }

    private static IEnumerable<ChatTier> OrderTiers(IEnumerable<ChatTier> tiers)
    {
        string[] order = [ChatTierNames.Fast, ChatTierNames.Balanced, ChatTierNames.Cloud, ChatTierNames.Strong, ChatTierNames.Copilot];
        return tiers.OrderBy(t => Array.IndexOf(order, t.Name) is var i && i < 0 ? int.MaxValue : i);
    }

    private static string EnsureLocal(List<ConnectionSettings> connections, Uri uri, List<string> changes)
    {
        var updated = EnsureLocalList([.. connections], uri, changes);
        connections.Clear();
        connections.AddRange(updated);
        return updated.First(c => c.Kind == ConnectionKind.OpenAICompatible && SameEndpoint(c.Endpoint, uri)).Id;
    }

    private static ConnectionSettings[] EnsureLocalList(ConnectionSettings[] connections, Uri uri, List<string> changes)
    {
        if (connections.Any(c => c.Kind == ConnectionKind.OpenAICompatible && SameEndpoint(c.Endpoint, uri))) return connections;
        var id = connections.Any(c => c.Id == ProviderSettingsDefaults.LocalConnectionId) ? "local-2" : ProviderSettingsDefaults.LocalConnectionId;
        changes.Add($"Loopback endpoint {uri} → openai-compatible connection '{id}'.");
        return [.. connections, new ConnectionSettings { Id = id, Kind = ConnectionKind.OpenAICompatible, Endpoint = uri.AbsoluteUri.TrimEnd('/'), Auth = AuthMode.None }];
    }

    private static bool SameEndpoint(string? endpoint, Uri uri) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var other) &&
        string.Equals(other.AbsoluteUri.TrimEnd('/'), uri.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static JsonNode? Get(JsonNode node, string name) =>
        node is JsonObject obj ? obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value : null;

    private static string? Text(JsonNode node, string name) =>
        Get(node, name) is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
