using MediaStudio.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace VideoProduction.Providers.Illustrations;

/// <summary>
/// Grounding inputs shared by the illustration pipeline: the open project's <see cref="SourceIndex"/> (set when a
/// project is opened) and the user-level <see cref="SvgExemplarLibrary"/>.
/// </summary>
public sealed class IllustrationSources
{
    private SourceIndex? sources;
    public IllustrationSources(SvgExemplarLibrary? exemplars = null) => Exemplars = exemplars;
    public SvgExemplarLibrary? Exemplars { get; }
    public SourceIndex? Sources { get => Volatile.Read(ref sources); set => Volatile.Write(ref sources, value); }
}

/// <summary>Builds the illustration <see cref="IImageGenerator"/>: grounding (outermost) → critique → base generator.</summary>
public static class IllustrationPipeline
{
    /// <summary>
    /// Wraps <paramref name="baseGenerator"/>: <see cref="GroundedImageGenerator"/> runs first (it embeds the brief once and
    /// can skip generation entirely on exemplar reuse), then <see cref="CritiquingImageGenerator"/> scores and retries.
    /// </summary>
    public static IImageGenerator Create(IImageGenerator baseGenerator, ImageSettings settings,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null, IChatClient? vision = null,
        IllustrationSources? sources = null, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(baseGenerator);
        ArgumentNullException.ThrowIfNull(settings);
        // The first Use() is the outermost decorator.
        var builder = baseGenerator.AsBuilder();
        if (settings.Grounding && embeddings is not null)
            builder.Use(inner => new GroundedImageGenerator(inner, embeddings, () => sources?.Sources,
                settings.Exemplars ? sources?.Exemplars : null, loggerFactory?.CreateLogger<GroundedImageGenerator>()));
        if (settings.Critique.Enabled && vision is not null)
            builder.Use(inner => new CritiquingImageGenerator(inner, vision, settings.Critique,
                loggerFactory?.CreateLogger<CritiquingImageGenerator>()));
        return builder.Build();
    }

    /// <summary>Identifier stored with persisted vectors so a changed embedding model never mixes indexes.</summary>
    public static string? EmbeddingModelId(ProviderSettings settings) =>
        settings.Embeddings is { } role ? $"{role.Connection}/{role.Model}" : null;

    /// <summary>
    /// Registers the keyed <see cref="IImageGenerator"/> <c>"illustration"</c> and <see cref="IllustrationSources"/>.
    /// Png mode with cloud allowed and a Foundry connection uses the gpt-image deployment; otherwise the local-first
    /// <see cref="ChatSvgImageGenerator"/> over the keyed <c>"illustration"</c> chat client (the router, or the Copilot
    /// runtime when configured), rasterized only when a <see cref="SvgRasterConfiguration"/> is registered.
    /// Grounding uses the keyed <c>"embedding"</c> generator and critique the keyed <c>"vision"</c> client when present.
    /// </summary>
    public static IServiceCollection AddMediaStudioIllustrations(this IServiceCollection services, ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        if (services.Any(d => d.ServiceType == typeof(IImageGenerator) && Equals(d.ServiceKey, MediaStudioAIKeys.Illustration)))
            return services;
        var cloudImage = CloudImageConnection(settings);
        var svgChat = services.Any(d => d.ServiceType == typeof(IChatClient) && Equals(d.ServiceKey, MediaStudioAIKeys.Illustration));
        if (cloudImage is null && !svgChat) return services;
        var modelId = EmbeddingModelId(settings);
        services.TryAddSingleton(sp => new IllustrationSources(
            settings.Images.Exemplars && modelId is not null
            && sp.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(MediaStudioAIKeys.Embedding) is { } embedding
                ? new SvgExemplarLibrary(embedding, modelId) : null));
        services.AddKeyedImageGenerator(MediaStudioAIKeys.Illustration, sp =>
        {
            var baseGenerator = cloudImage is not null
                ? CreateCloudImageGenerator(cloudImage, settings.Images.Deployment)
                : new ChatSvgImageGenerator(sp.GetRequiredKeyedService<IChatClient>(MediaStudioAIKeys.Illustration),
                    MediaStudioAIKeys.Illustration, sp.GetService<SvgRasterConfiguration>(), svgOptions: new ChatSvgOptions());
            return Create(baseGenerator, settings.Images,
                sp.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(MediaStudioAIKeys.Embedding),
                sp.GetKeyedService<IChatClient>(MediaStudioAIKeys.Vision), sp.GetRequiredService<IllustrationSources>(),
                sp.GetService<ILoggerFactory>());
        }, ServiceLifetime.Singleton).UseLogging().UseOpenTelemetry();
        return services;
    }

    /// <summary>The Foundry connection for gpt-image, or null when png mode is off, cloud is disabled or none is configured.</summary>
    public static ConnectionSettings? CloudImageConnection(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Images.Mode != ImageMode.Png || !settings.Privacy.AllowCloud) return null;
        var connection = settings.Images.Connection is { } id ? settings.FindConnection(id)
            : settings.Connections.FirstOrDefault(c => c.Kind == ConnectionKind.Foundry);
        return connection is { Kind: ConnectionKind.Foundry } ? connection : null;
    }

    /// <summary>Opt-in cloud illustrations: the Foundry gpt-image deployment as an <see cref="IImageGenerator"/>.</summary>
    public static IImageGenerator CreateCloudImageGenerator(ConnectionSettings connection, string deployment)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Guard.Require(connection.Kind == ConnectionKind.Foundry && !string.IsNullOrWhiteSpace(deployment), "image.config",
            "PNG illustrations need a Foundry connection and an image deployment.");
        return ProviderFactory.Foundry(connection).GetImageClient(deployment).AsIImageGenerator();
    }
}
