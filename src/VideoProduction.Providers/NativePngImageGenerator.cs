using System.Drawing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace VideoProduction.Providers;

public sealed record NativeImageEvidence(string Kind, string Prompt, string ClientType, string ConfiguredModel,
    string Endpoint, string NativePngSha256, string PngSha256, string FfmpegSha256,
    int NativeWidth, int NativeHeight, int OutputWidth, int OutputHeight, int CaptionSafePixels,
    string? ReturnedModel = null);

public static class IllustrationProviders
{
    public static IImageGenerator Create(IllustrationConfiguration configuration, GenerationConfiguration setup, string scratch)
    {
        var tools = ProductionVideoPipeline.ResolveTools(PrivateFiles.Read<Tools>(setup.ToolsFile));
        if (!configuration.NativeImage)
            return new ConfiguredSvgImageGenerator(configuration.Connection,
                new SvgRasterConfiguration(tools.SourceRoot, tools.Node, scratch, setup.FontFile, setup.FontSha256));
        Guard.Require(!string.IsNullOrWhiteSpace(configuration.Connection.Model)
            && Uri.TryCreate(configuration.Connection.Endpoint, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme == "https" && endpoint.AbsolutePath.TrimEnd('/') == "/openai/v1"
            && (endpoint.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                || endpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)
                || endpoint.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase)),
            "image.configuration", "Native images require an explicitly selected Foundry image deployment on an HTTPS /openai/v1 endpoint; no fallback or model substitution.");
        return new NativePngImageGenerator(
            ChatVideoPlanner.CreateOpenAIClient(configuration.Connection).GetImageClient(configuration.Connection.Model).AsIImageGenerator(),
            configuration.Connection.Model, configuration.Connection.Endpoint, tools.Ffmpeg, scratch);
    }

    /// <summary>
    /// Switches <paramref name="video"/> to the settings-routed illustration pipeline: writes an immutable settings snapshot
    /// under <c>&lt;stateRoot&gt;/settings</c> (connections, tiers and image options only; keys stay in user secrets) and
    /// pins it, so a later settings edit cannot silently change an approved production. Non-empty
    /// <paramref name="passages"/> (already-extracted project source text) are pinned alongside for grounding.
    /// </summary>
    public static GenerationConfiguration RouteThroughSettings(GenerationConfiguration video,
        MediaStudio.Configuration.ProviderSettings settings, string stateRoot,
        IReadOnlyCollection<Illustrations.SourcePassage>? passages = null)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(settings);
        var folder = Path.Combine(PrivateFiles.LocalPath(stateRoot), "settings");
        var text = MediaStudio.Configuration.ProviderSettingsJson.Serialize(settings);
        var path = Path.Combine(folder, "illustration-settings-" + Files.HashText(text)[..16] + ".json");
        if (!File.Exists(path))
        {
            new MediaStudio.Configuration.ProviderSettingsStore(path).Save(settings);
            PrivateFiles.Protect(path);
        }
        string? sources = null;
        if (passages is { Count: > 0 })
        {
            Guard.Require(passages.Count <= MaximumGroundingPassages, "image.routed-sources",
                $"Grounding is limited to {MaximumGroundingPassages} source passages.");
            var json = Json.Text(passages.ToArray());
            sources = Path.Combine(folder, "illustration-sources-" + Files.HashText(json)[..16] + ".json");
            if (!File.Exists(sources))
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(sources, json);
                PrivateFiles.Protect(sources);
            }
        }
        return video with {
            Illustration = null,
            RoutedIllustration = new(path, Files.Hash(path),
            sources, sources is null ? null : Files.Hash(sources))
        };
    }

    /// <summary>Upper bound on pinned grounding passages (each is embedded once per production).</summary>
    public const int MaximumGroundingPassages = 1024;

    /// <summary>
    /// Builds the keyed <c>"illustration"</c> <see cref="IImageGenerator"/> (<see cref="Illustrations.IllustrationPipeline"/>)
    /// from the reviewed settings snapshot, rasterizing SVG with the production tools and pinned font. Pinned source
    /// passages are embedded with the keyed <c>"embedding"</c> generator into the pipeline's <see cref="Illustrations.SourceIndex"/>.
    /// <paramref name="configure"/> runs before the settings registrations, so tests and hosts can supply clients first.
    /// The returned generator owns, and disposes, its service provider.
    /// </summary>
    public static async Task<IImageGenerator> CreateRoutedAsync(RoutedIllustrationConfiguration configuration, GenerationConfiguration setup,
        string scratch, Action<IServiceCollection>? configure = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(setup);
        var file = PrivateFiles.LocalPath(configuration.SettingsFile);
        Guard.Require(File.Exists(file) && Files.ValidHash(configuration.SettingsSha256) && Files.Hash(file) == configuration.SettingsSha256,
            "image.routed-settings", "The reviewed provider settings snapshot is missing or changed. Review generation again.");
        Illustrations.SourcePassage[] passages = [];
        if (configuration.SourcesFile is { } sourcesFile)
        {
            var local = PrivateFiles.LocalPath(sourcesFile);
            Guard.Require(File.Exists(local) && configuration.SourcesSha256 is { } pin && Files.ValidHash(pin) && Files.Hash(local) == pin,
                "image.routed-sources", "The reviewed grounding sources are missing or changed. Review generation again.");
            passages = PrivateFiles.Read<Illustrations.SourcePassage[]>(local);
            Guard.Require(passages.Length <= MaximumGroundingPassages, "image.routed-sources",
                $"Grounding is limited to {MaximumGroundingPassages} source passages.");
        }
        var settings = new MediaStudio.Configuration.ProviderSettingsStore(file).Load();
        var services = new ServiceCollection();
        var tools = ProductionVideoPipeline.ResolveTools(PrivateFiles.Read<Tools>(setup.ToolsFile));
        services.AddSingleton(new SvgRasterConfiguration(tools.SourceRoot, tools.Node, scratch, setup.FontFile, setup.FontSha256));
        configure?.Invoke(services);
        Guard.Require(settings.Agents.Illustration.Runtime != MediaStudio.Configuration.AgentRuntime.Copilot
            || services.Any(d => d.ServiceType == typeof(IChatClient) && Equals(d.ServiceKey, MediaStudioAIKeys.Illustration)),
            "image.routed-copilot", "The GitHub Copilot illustration runtime is not available to background production. Choose settings tiers or a chat model for illustrations.");
        ProviderFactory.AddMediaStudioAI(services, settings);
        var provider = services.BuildServiceProvider();
        try
        {
            var generator = provider.GetKeyedService<IImageGenerator>(MediaStudioAIKeys.Illustration);
            Guard.Require(generator is not null, "image.routed-unconfigured",
                "Settings route no illustration model: add chat tiers (or a cloud image connection) in Settings.");
            if (passages.Length > 0 && settings.Images.Grounding
                && Illustrations.IllustrationPipeline.EmbeddingModelId(settings) is { } modelId
                && provider.GetKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(MediaStudioAIKeys.Embedding) is { } embeddings
                && provider.GetService<Illustrations.IllustrationSources>() is { } sources)
            {
                var index = new Illustrations.SourceIndex(embeddings, modelId);
                await index.AddAsync(passages, cancellationToken).ConfigureAwait(false);
                sources.Sources = index;
            }
            return new OwningImageGenerator(generator!, provider);
        }
        catch { await provider.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private sealed class OwningImageGenerator(IImageGenerator inner, IDisposable owner) : DelegatingImageGenerator(inner)
    {
        protected override void Dispose(bool disposing)
        {
            // The provider owns (and disposes) the keyed singleton pipeline.
            if (disposing) owner.Dispose();
        }
    }
}

public sealed class NativePngImageGenerator(IImageGenerator native, string model, string endpoint,
    string ffmpeg, string scratch) : IImageGenerator
{
    public async Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request, ImageGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Guard.Require(request.OriginalImages is null && request.Prompt is { Length: > 0 and <= 2000 }
            && !string.IsNullOrWhiteSpace(request.Prompt) && !request.Prompt.Contains('\0'),
            "image.request", "Native image generation accepts one bounded text illustration, not editing/uploads.");
        Guard.Require(options is null || (options.Count is null or 1)
            && (options.ImageSize is null || options.ImageSize == new Size(1920, 1080))
            && (options.MediaType is null or "image/png") && (options.ModelId is null || options.ModelId == model)
            && (options.ResponseFormat is null or ImageGenerationResponseFormat.Data)
            && options.StreamingCount is null && options.RawRepresentationFactory is null && options.AdditionalProperties is null,
            "image.options", "Only one PNG with the fixed bounded native/composition sizes is supported.");
        var executable = Files.FindExecutable(ffmpeg);
        var work = Path.Combine(PrivateFiles.LocalPath(scratch), "native-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work); PrivateFiles.Protect(work, directory: true);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            ImageGenerationResponse response;
            try
            {
                response = await native.GenerateAsync(new ImageGenerationRequest(request.Prompt!),
                    new ImageGenerationOptions {
                        Count = 1, MediaType = "image/png", ModelId = model, ImageSize = new Size(1536, 1024),
                        RawRepresentationFactory = _ => new OpenAI.Images.ImageGenerationOptions {
                            Quality = new("low")
                        }
                    }, timeout.Token);
            }
            catch (System.ClientModel.ClientResultException error)
            {
                throw new VideoException("image.http-" + error.Status, "The explicitly selected native image provider failed. No retry, alternative provider or raw response was exposed.");
            }
            catch (Azure.Identity.CredentialUnavailableException)
            {
                throw new VideoException("image.credential-unavailable", "SDK Entra credentials are unavailable for the selected native image connection; no alternate authentication was used.");
            }
            catch (Azure.Identity.AuthenticationFailedException)
            {
                throw new VideoException("image.authentication", "Native image SDK authentication failed; no alternate credential or provider was used.");
            }
            catch (HttpRequestException)
            {
                throw new VideoException("image.transport", "The selected native image transport failed; no alternative provider was used.");
            }
            Guard.Require(response.Contents is [DataContent { MediaType: "image/png" }],
                "image.contract", "The native SDK must return one bounded PNG data result, not an external URL.");
            var original = ((DataContent)response.Contents[0]).Data.ToArray();
            ChatSvgImageGenerator.ValidatePng(original, 1536, 1024);
            var source = Path.Combine(work, "native.png");
            var output = Path.Combine(work, "composition.png");
            await File.WriteAllBytesAsync(source, original, timeout.Token);
            await Processes.Run(executable,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-i", source, "-vf",
                    "scale=1920:864:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:0:black",
                    "-frames:v", "1", output], work, timeout.Token, 30);
            var png = await File.ReadAllBytesAsync(output, timeout.Token);
            ChatSvgImageGenerator.ValidatePng(png);
            return new([new DataContent(png, "image/png")]) {
                RawRepresentation = new NativeImageEvidence("native-openai-image-locally-composed",
                    request.Prompt!, native.GetType().FullName!, model, endpoint,
                    Files.Hash(source), Files.Hash(output), Files.Hash(executable), 1536, 1024, 1920, 1080, 216),
                Usage = response.Usage
            };
        }
        finally { Directory.Delete(work, recursive: true); }
    }
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }
    public void Dispose() => native.Dispose();
}
