using System.Text;
using MediaStudio.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VideoProduction;
using VideoProduction.Providers;
using VideoProduction.Providers.Illustrations;

namespace MediaStudio.Tests.Illustrations;

public sealed class SvgSanitizerTests
{
    [Fact]
    public void StripsScriptsForeignObjectsLinksAndStyleSheets()
    {
        var raw = """
            Here you go:
            ```svg
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="1920" height="1080" viewBox="0 0 1920 1080" onload="alert(1)">
              <style>rect { fill: red }</style>
              <script>alert(1)</script>
              <foreignObject><div>html</div></foreignObject>
              <image xlink:href="https://example.com/x.png" width="10" height="10"/>
              <a href="https://example.com"><rect x="1" y="1" width="2" height="2"/></a>
              <rect x="10" y="20" width="300" height="200" fill="navy" onclick="evil()" xlink:href="#x"/>
              <text x="40" y="80" style="fill: white; font-size: 48px">Hello</text>
            </svg>
            ```
            """;

        var svg = SvgSanitizer.Sanitize(raw);

        Assert.DoesNotContain("script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("foreignObject", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.com", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fill=\"#000080\"", svg);
        Assert.Contains("fill=\"#FFFFFF\"", svg);
        Assert.Contains("font-size=\"48\"", svg);
        Assert.Equal(svg, SafeSvg.Validate(svg));
    }

    [Fact]
    public void RescalesADifferentViewBoxOntoTheCanvas()
    {
        var svg = SvgSanitizer.Sanitize("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 960 540"><rect x="100" y="50" width="200" height="100" fill="#abc"/></svg>""");

        Assert.Contains("viewBox=\"0 0 1920 1080\"", svg);
        Assert.Contains("x=\"200\" y=\"100\" width=\"400\" height=\"200\" fill=\"#AABBCC\"", svg);
    }

    [Theory]
    [InlineData("no svg here")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script><path d=\"M0 0\"/></svg>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x 'y'>]><svg xmlns=\"http://www.w3.org/2000/svg\">&x;</svg>")]
    public void RejectsUnrecoverableOutput(string raw) =>
        Assert.Equal("image.svg", Assert.Throws<VideoException>(() => SvgSanitizer.Sanitize(raw)).Code);

    [Fact]
    public void RepairsNearXmlFromSmallModels()
    {
        var svg = SvgSanitizer.Sanitize("""
            <svg width="1920" height="1080" viewBox="0 0 1920 1080" xmlns="http://www.w3.org/2000/svg">
            <defs><filter id="glow"><fecolormatrix in2.SourceAlpha x="1"/></filter></defs>
            <g fill="#FFDDA2"><rect x="10" y="-20" width="720" height="720"/></g>
            <text x="960" y="900" font-size="80" fill="#FFFFFF">Solar &amp; wind < 5</text>
            </p>
            <circle cx="540" cy="270" r="80" fill="#FFEEC0"/>
            </svg>
            """);

        Assert.Contains("<circle cx=\"540\" cy=\"270\" r=\"80\" fill=\"#FFEEC0\"", svg);
        Assert.Contains("y=\"0\"", svg);
        Assert.Contains("Solar &amp; wind", svg);
        Assert.DoesNotContain("filter", svg);
        var truncated = SvgSanitizer.Sanitize("""<svg xmlns="http://www.w3.org/2000/svg"><rect x="1" y="1" width="2" height="2"/><text x="1" y""");
        Assert.Contains("<rect x=\"1\"", truncated);
    }

    [Fact]
    public void EnforcesElementAndTextBounds()
    {
        var rects = string.Concat(Enumerable.Range(0, 400).Select(i => $"<rect x=\"{i}\" y=\"0\" width=\"1\" height=\"1\"/>"));
        var svg = SvgSanitizer.Sanitize($"""<svg xmlns="http://www.w3.org/2000/svg">{rects}<text x="1" y="1">{new string('a', 1000)}</text></svg>""");

        Assert.True(svg.Split("<rect").Length - 1 < SvgSanitizer.MaxElements);
        Assert.DoesNotContain(new string('a', SvgSanitizer.MaxTextCharacters + 1), svg);
    }
}

public sealed class VectorIndexTests
{
    [Fact]
    public void SearchRanksByCosineAndRespectsFilter()
    {
        var index = new VectorIndex<string>("m");
        index.Add(new float[] { 1, 0, 0 }, "x");
        index.Add(new float[] { 0.7f, 0.7f, 0 }, "xy");
        index.Add(new float[] { 0, 1, 0 }, "y");
        index.Add(new float[] { -1, 0, 0 }, "-x");

        var top = index.Search(new float[] { 2, 0.1f, 0 }, 3);
        Assert.Equal(["x", "xy", "y"], top.Select(m => m.Item));
        Assert.True(top[0].Score > top[1].Score && top[1].Score > top[2].Score);
        Assert.Equal(["xy", "y"], index.Search(new float[] { 1, 0, 0 }, 2, i => i.Contains('y')).Select(m => m.Item));
    }

    [Fact]
    public void LoadIgnoresAnIndexBuiltWithAnotherModel()
    {
        var path = Path.Combine(Path.GetTempPath(), "ms-vec-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var index = new VectorIndex<string>("local/embeddinggemma");
            index.Add(new float[] { 1, 2, 3 }, "a");
            index.Save(path);

            Assert.Equal(1, VectorIndex<string>.Load(path, "local/embeddinggemma").Count);
            Assert.Equal(0, VectorIndex<string>.Load(path, "cloud/text-embedding-3-small").Count);
            Assert.Equal(0, VectorIndex<string>.Load(path, "local/embeddinggemma", dimensions: 768).Count);
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Equal(0, VectorIndex<string>.Load(path, "local/embeddinggemma").Count);
        }
        finally { File.Delete(path); }
    }
}

public sealed class GroundingTests
{
    private const string Square = """<svg xmlns="http://www.w3.org/2000/svg" width="1920" height="1080" viewBox="0 0 1920 1080"><rect x="0" y="0" width="100" height="100" fill="#112233"/></svg>""";

    [Fact]
    public async Task SourceIndexReturnsTheMostRelevantPassages()
    {
        var embeddings = new KeywordEmbeddings();
        var index = new SourceIndex(embeddings, "fake");
        await index.AddAsync([new("doc", 0, "Revenue grew 40% in 2024."), new("doc", 1, "The team hired a chef."),
            new("doc", 2, "Revenue forecast for 2025 is flat.")]);

        var top = await index.TopKAsync("revenue chart", 2);

        Assert.Equal([0, 2], top.Select(m => m.Item.Index).Order());
    }

    [Fact]
    public void PromptPutsBoundedFactsAndExamplesBeforeTheBrief()
    {
        var prompt = GroundedImageGenerator.BuildPrompt("Bar chart of revenue",
            ["Revenue grew 40%   in\n2024.", new string('z', 3000)], [Square]);

        Assert.StartsWith("Facts from the source", prompt);
        Assert.Contains("- Revenue grew 40% in 2024.", prompt);
        Assert.Contains("Style examples", prompt);
        Assert.EndsWith("Scene brief:\nBar chart of revenue", prompt);
        var facts = prompt[..prompt.IndexOf("Style examples", StringComparison.Ordinal)];
        Assert.True(facts.Length < GroundedImageGenerator.MaxFactCharacters + 200);
        Assert.Equal("brief", GroundedImageGenerator.BuildPrompt("brief", [], []));
    }

    [Fact]
    public async Task GroundedGeneratorAddsFactsAndKeepsTheOriginalBrief()
    {
        var embeddings = new KeywordEmbeddings();
        var index = new SourceIndex(embeddings, "fake");
        await index.AddAsync([new("doc", 0, "Revenue grew 40% in 2024."), new("doc", 1, "The team hired a chef.")]);
        var inner = new RecordingImageGenerator();
        var generator = new GroundedImageGenerator(inner, embeddings, () => index);

        var response = await generator.GenerateAsync(new ImageGenerationRequest("revenue chart"));

        Assert.Contains("Revenue grew 40% in 2024.", inner.Prompts.Single());
        Assert.Equal("revenue chart", inner.Options.Single()!.AdditionalProperties![IllustrationProperties.Brief]);
        var receipt = Assert.IsType<GroundingReceipt>(response.Contents[0].AdditionalProperties![IllustrationProperties.Grounding]);
        Assert.Equal(2, receipt.Facts);
    }

    [Fact]
    public async Task WithoutEmbeddingsTheGeneratorPassesThrough()
    {
        var inner = new RecordingImageGenerator();
        await new GroundedImageGenerator(inner, null).GenerateAsync(new ImageGenerationRequest("plain brief"));
        Assert.Equal("plain brief", inner.Prompts.Single());
    }

    [Fact]
    public async Task ExemplarReuseRequiresTheThresholdAndTheSamePalette()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ms-svg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = new SvgExemplarLibrary(new KeywordEmbeddings(), "fake", directory);
            await library.AddAsync("stored", "default", Square, new float[] { 1, 0 });

            Assert.NotNull(await library.TryReuseAsync(Unit(0.98f), "default"));
            Assert.Null(await library.TryReuseAsync(Unit(0.969f), "default"));
            Assert.Null(await library.TryReuseAsync(new float[] { 1, 0 }, "dark"));
            Assert.Equal(1, new SvgExemplarLibrary(new KeywordEmbeddings(), "fake", directory).Count);
            Assert.Equal(0, new SvgExemplarLibrary(new KeywordEmbeddings(), "other-model", directory).Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

        static float[] Unit(float cosine) => [cosine, MathF.Sqrt(1 - cosine * cosine)];
    }

    [Fact]
    public async Task PassedImagesBecomeExemplarsAndNearIdenticalBriefsReuseThem()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ms-svg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var embeddings = new KeywordEmbeddings();
            var library = new SvgExemplarLibrary(embeddings, "fake", directory);
            var chat = new SequenceChatClient(Square);
            var svg = new ChatSvgImageGenerator(chat, "m", null, svgOptions: new ChatSvgOptions());
            var critic = new SequenceChatClient("""{"score":0.9,"issues":[]}""");
            var generator = IllustrationPipeline.Create(svg, new ImageSettings(), embeddings, critic,
                new IllustrationSources(library));

            var first = await generator.GenerateAsync(new ImageGenerationRequest("revenue chart"));
            var second = await generator.GenerateAsync(new ImageGenerationRequest("revenue chart"));

            Assert.Equal(ChatSvgImageGenerator.SvgMediaType, Assert.IsType<DataContent>(first.Contents[0]).MediaType);
            Assert.Equal(1, chat.Calls);
            Assert.Equal(1, library.Count);
            var receipt = Assert.IsType<GroundingReceipt>(second.Contents[0].AdditionalProperties![IllustrationProperties.Grounding]);
            Assert.NotNull(receipt.ReusedSha256);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}

public sealed class CritiqueTests
{
    [Fact]
    public async Task RetriesOnceWithTheIssuesAndKeepsTheBestResult()
    {
        var inner = new RecordingImageGenerator();
        var critic = new SequenceChatClient("""{"score":0.6,"issues":["Label is unreadable"]}""", """{"score":0.4,"issues":["worse"]}""");
        var generator = new CritiquingImageGenerator(inner, critic, new CritiqueSettings { Threshold = 0.7, MaxRetries = 1 });

        var response = await generator.GenerateAsync(new ImageGenerationRequest("brief"));

        Assert.Equal(2, inner.Prompts.Count);
        Assert.Contains("Label is unreadable", inner.Prompts[1]);
        Assert.Equal(2, critic.Calls);
        var properties = response.Contents[0].AdditionalProperties!;
        Assert.Equal(0.6, properties[IllustrationProperties.CritiqueScore]);
        Assert.Equal(false, properties[IllustrationProperties.CritiquePassed]);
        var receipt = Assert.IsType<CritiqueReceipt>(properties[IllustrationProperties.Critique]);
        Assert.Equal(0, receipt.SelectedAttempt);
        Assert.Equal(2, receipt.Attempts.Length);
        Assert.Equal("image-0", Assert.IsType<DataContent>(response.Contents[0]).Name);
    }

    [Fact]
    public async Task StopsAsSoonAsTheThresholdIsMet()
    {
        var inner = new RecordingImageGenerator();
        var critic = new SequenceChatClient("```json\n{\"score\": 0.95, \"issues\": []}\n```");
        var response = await new CritiquingImageGenerator(inner, critic, new CritiqueSettings { MaxRetries = 3 })
            .GenerateAsync(new ImageGenerationRequest("brief"));

        Assert.Single(inner.Prompts);
        Assert.Equal(true, response.Contents[0].AdditionalProperties![IllustrationProperties.CritiquePassed]);
    }

    [Fact]
    public async Task UnparseableCritiqueEndsTheLoopAndKeepsTheImage()
    {
        var inner = new RecordingImageGenerator();
        var critic = new SequenceChatClient("looks fine to me");
        var response = await new CritiquingImageGenerator(inner, critic, new CritiqueSettings { MaxRetries = 3 })
            .GenerateAsync(new ImageGenerationRequest("brief"));

        Assert.Single(inner.Prompts);
        var receipt = Assert.IsType<CritiqueReceipt>(response.Contents[0].AdditionalProperties![IllustrationProperties.Critique]);
        Assert.Null(receipt.Score);
        Assert.Equal("critique-unparseable", receipt.Attempts.Single().Error);
    }

    [Fact]
    public async Task WithoutVisionOrWhenDisabledItPassesThrough()
    {
        var inner = new RecordingImageGenerator();
        await new CritiquingImageGenerator(inner, null).GenerateAsync(new ImageGenerationRequest("a"));
        await new CritiquingImageGenerator(inner, new SequenceChatClient("{}"), new CritiqueSettings { Enabled = false })
            .GenerateAsync(new ImageGenerationRequest("b"));
        Assert.Equal(["a", "b"], inner.Prompts);
    }

    [Theory]
    [InlineData("""{"score": 7, "issues": ["x"]}""", 0.7)]
    [InlineData("""{"Score": 1.5}""", 0.15)]
    [InlineData("""{"score": -1}""", 0.0)]
    public void ParseNormalisesScores(string text, double expected) => Assert.Equal(expected, CritiquingImageGenerator.Parse(text)!.Score, 3);
}

public sealed class IllustrationDiTests
{
    private static ProviderSettings Settings() => new() {
        Privacy = new() { AllowCloud = false },
        Connections = [new() { Id = "local", Kind = ConnectionKind.OpenAICompatible, Endpoint = ProviderSettingsDefaults.OllamaEndpoint.ToString() }],
        Chat = new() { Tiers = [new() { Name = "fast", Connection = "local", Model = "qwen3.5:4b" }] },
        Embeddings = new() { Connection = "local", Model = "embeddinggemma" },
        Decisions = new() { Julia = JuliaMode.Off }
    };

    [Fact]
    public void RegistersTheIllustrationGeneratorOverTheIllustrationChatClient()
    {
        using var services = new ServiceCollection().AddMediaStudioAI(Settings()).BuildServiceProvider();

        var generator = services.GetRequiredKeyedService<IImageGenerator>(MediaStudioAIKeys.Illustration);

        Assert.NotNull(generator.GetService<GroundedImageGenerator>());
        Assert.Null(generator.GetService<CritiquingImageGenerator>()); // no vision model configured
        Assert.NotNull(generator.GetService<ChatSvgImageGenerator>());
        Assert.NotNull(services.GetRequiredService<IllustrationSources>().Exemplars);
    }

    [Fact]
    public async Task AnEarlierIllustrationChatClientSuchAsCopilotIsUsed()
    {
        var copilot = new SequenceChatClient("""<svg xmlns="http://www.w3.org/2000/svg"><rect x="1" y="1" width="5" height="5" fill="red"/></svg>""");
        var collection = new ServiceCollection();
        collection.AddKeyedChatClient(MediaStudioAIKeys.Illustration, copilot);
        using var services = collection.AddMediaStudioAI(Settings() with { Embeddings = null }).BuildServiceProvider();

        var response = await services.GetRequiredKeyedService<IImageGenerator>(MediaStudioAIKeys.Illustration)
            .GenerateAsync(new ImageGenerationRequest("a red square"));

        Assert.Equal(1, copilot.Calls);
        Assert.Contains("fill=\"#FF0000\"", Encoding.UTF8.GetString(Assert.IsType<DataContent>(response.Contents[0]).Data.Span));
    }

    [Fact]
    public void NothingIsRegisteredWithoutAChatOrCloudImageModel()
    {
        using var services = new ServiceCollection().AddMediaStudioAI(new ProviderSettings()).BuildServiceProvider();
        Assert.Null(services.GetKeyedService<IImageGenerator>(MediaStudioAIKeys.Illustration));
    }

    [Fact]
    public void PngModeNeedsCloudAndAFoundryConnection()
    {
        var foundry = new ConnectionSettings { Id = "foundry", Kind = ConnectionKind.Foundry, Endpoint = "https://example.services.ai.azure.com" };
        var png = Settings() with { Images = new() { Mode = ImageMode.Png }, Connections = [.. Settings().Connections, foundry] };

        Assert.Null(IllustrationPipeline.CloudImageConnection(png));
        Assert.Equal("foundry", IllustrationPipeline.CloudImageConnection(png with { Privacy = new() { AllowCloud = true } })!.Id);
        Assert.Null(IllustrationPipeline.CloudImageConnection(Settings() with { Privacy = new() { AllowCloud = true } }));
    }
}

public sealed class RoutedIllustrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ms-routed-" + Guid.NewGuid().ToString("N"));

    public RoutedIllustrationTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "settings"));
        var tools = Path.Combine(root, "tools.json");
        var executable = Environment.ProcessPath!;
        File.WriteAllText(tools, Json.Text(new Tools(root, executable, executable, executable)));
        PrivateFiles.Protect(tools);
        Video = new GenerationConfiguration(tools, Path.Combine(root, "font.ttf"), new string('0', 64), "", "") {
            Illustration = new(new PlannerConfiguration("http://localhost:11434/v1", "old", null))
        };
    }

    private GenerationConfiguration Video { get; }

    private static ProviderSettings Settings(AgentRuntime runtime = AgentRuntime.Maf) => new() {
        Connections = [new() { Id = "local", Kind = ConnectionKind.OpenAICompatible, Endpoint = ProviderSettingsDefaults.OllamaEndpoint.ToString() }],
        Chat = new() { Tiers = [new() { Name = "fast", Connection = "local", Model = "qwen3.5:4b" }] },
        Decisions = new() { Julia = JuliaMode.Off },
        Agents = new() { Illustration = new() { Runtime = runtime } }
    };

    [Fact]
    public async Task ProductionResolvesTheKeyedIllustrationGeneratorFromThePinnedSnapshot()
    {
        var routed = IllustrationProviders.RouteThroughSettings(Video, Settings(), root);

        Assert.Null(routed.Illustration);
        Assert.StartsWith(Path.Combine(root, "settings"), routed.RoutedIllustration!.SettingsFile);
        Assert.Equal(routed.RoutedIllustration, IllustrationProviders.RouteThroughSettings(Video, Settings(), root).RoutedIllustration);
        var fake = new RecordingImageGenerator();
        using var generator = await IllustrationProviders.CreateRoutedAsync(routed.RoutedIllustration, routed, root,
            services => services.AddKeyedSingleton<IImageGenerator>(MediaStudioAIKeys.Illustration, fake));

        await generator.GenerateAsync(new ImageGenerationRequest("revenue chart"));

        Assert.Equal(["revenue chart"], fake.Prompts);
    }

    [Fact]
    public async Task WithoutAnInjectedGeneratorTheSettingsPipelineIsBuilt()
    {
        var routed = IllustrationProviders.RouteThroughSettings(Video, Settings(), root);
        using var generator = await IllustrationProviders.CreateRoutedAsync(routed.RoutedIllustration!, routed, root);

        Assert.NotNull(generator.GetService<ChatSvgImageGenerator>());
    }

    [Fact]
    public async Task PinnedSourcePassagesGroundTheProducerIllustrations()
    {
        var passages = SourceIndex.FromChunks("doc", ["Revenue grew 40% in 2024.", "", "The team hired a chef."]).ToArray();
        Assert.Equal([0, 1], passages.Select(p => p.Index));
        var settings = Settings() with { Embeddings = new() { Connection = "local", Model = "embeddinggemma" } };
        var routed = IllustrationProviders.RouteThroughSettings(Video, settings, root, passages);
        Assert.NotNull(routed.RoutedIllustration!.SourcesSha256);
        var chat = new SequenceChatClient("""<svg xmlns="http://www.w3.org/2000/svg"><rect x="1" y="1" width="5" height="5" fill="red"/></svg>""");
        var embeddings = new KeywordEmbeddings();

        using var generator = await IllustrationProviders.CreateRoutedAsync(routed.RoutedIllustration, routed, root, services =>
        {
            services.RemoveAll<SvgRasterConfiguration>();
            services.AddKeyedChatClient(MediaStudioAIKeys.Illustration, chat);
            services.AddKeyedEmbeddingGenerator(MediaStudioAIKeys.Embedding, embeddings);
        });
        var response = await generator.GenerateAsync(new ImageGenerationRequest("revenue chart"));

        var receipt = Assert.IsType<GroundingReceipt>(response.Contents[0].AdditionalProperties![IllustrationProperties.Grounding]);
        Assert.True(receipt.Facts > 0);
        Assert.Equal("image.routed-sources", (await Assert.ThrowsAsync<VideoException>(() => IllustrationProviders.CreateRoutedAsync(
            routed.RoutedIllustration with { SourcesSha256 = new string('a', 64) }, routed, root))).Code);
    }

    [Fact]
    public async Task AChangedSnapshotOrTheCopilotRuntimeIsRejected()
    {
        var routed = IllustrationProviders.RouteThroughSettings(Video, Settings(), root).RoutedIllustration!;
        Assert.Equal("image.routed-settings", (await Assert.ThrowsAsync<VideoException>(() =>
            IllustrationProviders.CreateRoutedAsync(routed with { SettingsSha256 = new string('a', 64) }, Video, root))).Code);

        var copilot = IllustrationProviders.RouteThroughSettings(Video,
            Settings(AgentRuntime.Copilot) with { Privacy = new() { AllowCloud = true } }, root).RoutedIllustration!;
        Assert.Equal("image.routed-copilot", (await Assert.ThrowsAsync<VideoException>(() =>
            IllustrationProviders.CreateRoutedAsync(copilot, Video, root))).Code);
        using var injected = await IllustrationProviders.CreateRoutedAsync(copilot, Video, root,
            services => services.AddKeyedChatClient(MediaStudioAIKeys.Illustration, new SequenceChatClient("<svg/>")));
        Assert.NotNull(injected);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

/// <summary>Bag-of-words embedding over a fixed vocabulary: deterministic and cheap.</summary>
internal sealed class KeywordEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
{
    private static readonly string[] Vocabulary = ["revenue", "chart", "2024", "2025", "team", "chef", "forecast", "grew"];
    public int Calls { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v =>
        {
            var words = v.ToLowerInvariant().Split([' ', '.', ',', '%'], StringSplitOptions.RemoveEmptyEntries);
            var vector = Vocabulary.Select(w => (float)words.Count(x => x == w)).Append(0.01f).ToArray();
            return new Embedding<float>(vector);
        })));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("fake", null, "keywords") : null;

    public void Dispose() { }
}

internal sealed class SequenceChatClient(params string[] replies) : IChatClient
{
    public int Calls { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var reply = replies[Math.Min(Calls++, replies.Length - 1)];
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)) { ModelId = "fake" });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}

internal sealed class RecordingImageGenerator : IImageGenerator
{
    public List<string> Prompts { get; } = [];
    public List<ImageGenerationOptions?> Options { get; } = [];

    public Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request, ImageGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Options.Add(options);
        var name = "image-" + Prompts.Count;
        Prompts.Add(request.Prompt!);
        return Task.FromResult(new ImageGenerationResponse([new DataContent(new byte[] { 1, 2, 3 }, "image/png") { Name = name }]));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
