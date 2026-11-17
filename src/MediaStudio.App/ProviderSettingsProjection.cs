using MediaStudio.Configuration;
using VideoProduction.Providers;

namespace MediaStudio.App;

/// <summary>
/// Bridges settings v2 (<see cref="ProviderSettings"/>, written by the setup wizard and <c>configure</c>) into the app's
/// existing role state (<see cref="ModelProviderState"/>) until the routing phases consume v2 directly.
/// </summary>
public static class ProviderSettingsProjection
{
    /// <summary>Replaces the connections managed by <paramref name="settings"/> and re-points writing roles at its tiers.</summary>
    public static ModelProviderState Apply(ProviderSettings settings, ModelProviderState state)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);
        var projected = Connections(settings).ToArray();
        var managed = settings.Connections.Select(c => c.Id).Concat(projected.Select(c => c.Id)).ToHashSet(StringComparer.Ordinal);
        var connections = state.Connections.Where(c => !managed.Contains(c.Id)).Concat(projected).ToArray();

        ModelSelection? Keep(ModelSelection? selection) =>
            selection is not null && connections.Any(c => c.Id == selection.Connection && c.Models.Contains(selection.Model)) ? selection : null;
        ModelSelection? Tier(string name) => settings.FindTier(name) is { } tier && Find(tier.Connection, tier.Model, tier.Api) is { } id
            ? new(id, tier.Model) : null;
        string? Find(string connection, string model, ChatApi? api)
        {
            var id = api == ChatApi.Anthropic ? connection + "-anthropic" : connection;
            return projected.Any(c => c.Id == id && c.Models.Contains(model)) ? id : null;
        }

        var balanced = Tier(ChatTierNames.Balanced) ?? Tier(ChatTierNames.Cloud) ?? Tier(ChatTierNames.Fast);
        var fast = Tier(ChatTierNames.Fast) ?? balanced;
        ModelSelection? speech = null;
        if (settings.Speech.Live is { Provider: LiveSpeechProvider.FoundryOpenAI, Connection: { } liveConnection, Deployment: { } liveDeployment })
            speech = Keep(new(liveConnection, liveDeployment));
        ModelSelection? image = null;
        if (settings.Images is { Mode: ImageMode.Png } images && (images.Connection ?? FirstFoundry(settings)) is { } imageConnection)
            image = Keep(new(imageConnection, images.Deployment));
        return state with {
            Connections = connections,
            Authoring = balanced ?? Keep(state.Authoring),
            General = fast ?? Keep(state.General),
            ChatIllustration = Keep(state.ChatIllustration) ?? balanced,
            Speech = speech ?? Keep(state.Speech),
            SpeechUnassigned = speech is null && state.SpeechUnassigned,
            ImageFallback = image ?? Keep(state.ImageFallback),
            NativeImages = (image ?? Keep(state.ImageFallback)) is not null && state.NativeImages,
            NativeDecision = Keep(state.NativeDecision)
        };
    }

    /// <summary>Settings v2 connections expressed as app connections. Cloud connections are omitted while cloud is off.</summary>
    public static IEnumerable<ModelConnection> Connections(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var connection in settings.Connections)
        {
            var tiers = settings.Chat.Tiers.Where(t => t.Connection == connection.Id).ToArray();
            if (connection.Kind == ConnectionKind.OpenAICompatible && !connection.IsCloud && connection.Endpoint is { } endpoint)
            {
                var models = tiers.Select(t => t.Model).Append(settings.Vision is { } vision && vision.Connection == connection.Id ? vision.Model : null)
                    .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
                if (models.Length == 0) continue;
                var uri = new Uri(endpoint);
                var v1 = uri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal) ? uri : new Uri(uri, uri.AbsolutePath.TrimEnd('/') + "/v1");
                yield return new(connection.Id, $"Local · {uri.Authority}", v1.GetLeftPart(UriPartial.Path).TrimEnd('/'), models,
                    ModelConnection.LocalAuthentication, ChatModels: models);
            }
            else if (connection.Kind == ConnectionKind.Foundry && settings.Privacy.AllowCloud && connection.Auth == AuthMode.Entra
                     && connection.Endpoint is not null)
            {
                var endpoints = FoundryEndpoints.From(connection);
                var chat = tiers.Where(t => t.Api != ChatApi.Anthropic).Select(t => t.Model).Distinct(StringComparer.Ordinal).ToArray();
                string[] speech = settings.Speech.Live is { Provider: LiveSpeechProvider.FoundryOpenAI, Deployment: { } live } && settings.Speech.Live.Connection == connection.Id ? [live] : [];
                string[] image = settings.Images.Mode == ImageMode.Png && (settings.Images.Connection ?? connection.Id) == connection.Id ? [settings.Images.Deployment] : [];
                var models = chat.Concat(speech).Concat(image).Distinct(StringComparer.Ordinal).ToArray();
                if (models.Length > 0)
                    yield return new(connection.Id, $"Foundry · {endpoints.Root.Host.Split('.')[0]}", endpoints.OpenAIv1.ToString().TrimEnd('/'), models,
                        "azure-cli", connection.Region ?? "", ChatModels: chat, SpeechModels: speech, ImageModels: image);
                var anthropic = tiers.Where(t => t.Api == ChatApi.Anthropic).Select(t => t.Model).Distinct(StringComparer.Ordinal).ToArray();
                if (anthropic.Length > 0 && endpoints.Anthropic.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
                    yield return new(connection.Id + "-anthropic", $"Foundry Claude · {endpoints.Root.Host.Split('.')[0]}",
                        endpoints.Anthropic.ToString().TrimEnd('/'), anthropic, "azure-cli", connection.Region ?? "",
                        ChatModels: anthropic, Provider: ChatProviderKind.Anthropic);
            }
        }
    }

    private static string? FirstFoundry(ProviderSettings settings) =>
        settings.Connections.FirstOrDefault(c => c.Kind == ConnectionKind.Foundry)?.Id;
}
