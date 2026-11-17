using System.Text;
using MediaStudio.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using VideoProduction.Providers;
using VideoProduction.Providers.Illustrations;

namespace MediaStudio.Tests.Illustrations;

/// <summary>Real local Ollama (http://localhost:11434) with qwen3.5:4b and embeddinggemma pulled. Excluded from the default run.</summary>
public sealed class LocalRuntimeIllustrationTests
{
    private static ProviderSettings Settings() => new() {
        Privacy = new() { AllowCloud = false },
        Connections = [new() { Id = "local", Kind = ConnectionKind.OpenAICompatible, Endpoint = ProviderSettingsDefaults.OllamaEndpoint.ToString() }],
        Chat = new() { Tiers = [new() { Name = "fast", Connection = "local", Model = "qwen3.5:4b" }] },
        Vision = new() { Connection = "local", Model = "qwen3.5:4b" },
        Embeddings = new() { Connection = "local", Model = "embeddinggemma" },
        Images = new() { Critique = new() { Threshold = 0.7, MaxRetries = 1 } },
        Decisions = new() { Julia = JuliaMode.Off }
    };

    [Fact, Trait("Category", "LocalRuntime")]
    public async Task GroundedCritiquedSvgIllustrationFromLocalModels()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ms-illus-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = Settings();
            var collection = new ServiceCollection();
            collection.AddSingleton(sp => new IllustrationSources(new SvgExemplarLibrary(
                sp.GetRequiredKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(MediaStudioAIKeys.Embedding),
                IllustrationPipeline.EmbeddingModelId(settings)!, directory)));
            using var services = collection.AddMediaStudioAI(settings).BuildServiceProvider();
            var embeddings = services.GetRequiredKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(MediaStudioAIKeys.Embedding);
            var sources = new SourceIndex(embeddings, IllustrationPipeline.EmbeddingModelId(settings)!);
            await sources.AddAsync([
                new("notes", 0, "Solar panels on the school roof produce 42 kWh on a sunny day."),
                new("notes", 1, "The cafeteria serves pasta on Fridays."),
                new("notes", 2, "Battery storage keeps the lights on for 6 hours after sunset.")]);
            services.GetRequiredService<IllustrationSources>().Sources = sources;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));

            var facts = await sources.TopKAsync("solar energy output of the school roof", 2, timeout.Token);
            Assert.Contains(facts, f => f.Item.Index == 0);

            var generator = services.GetRequiredKeyedService<IImageGenerator>(MediaStudioAIKeys.Illustration);
            var response = await generator.GenerateAsync(new ImageGenerationRequest(
                "A simple diagram: a sun, solar panels on a roof, and a label with the daily energy output."),
                new ImageGenerationOptions { ImageSize = new(1920, 1080) }, timeout.Token);

            var image = Assert.IsType<DataContent>(response.Contents[0]);
            Assert.Equal(ChatSvgImageGenerator.SvgMediaType, image.MediaType);
            var svg = Encoding.UTF8.GetString(image.Data.Span);
            Assert.Equal(svg, SafeSvg.Validate(svg));
            var grounding = Assert.IsType<GroundingReceipt>(image.AdditionalProperties![IllustrationProperties.Grounding]);
            Assert.True(grounding.Facts > 0);
            var critique = Assert.IsType<CritiqueReceipt>(image.AdditionalProperties[IllustrationProperties.Critique]);
            Assert.InRange(critique.Attempts.Length, 1, 2);
            Console.WriteLine($"LocalRuntime illustration: {svg.Length} chars, facts {grounding.Facts}, critique " +
                string.Join(" → ", critique.Attempts.Select(a => a.Score?.ToString("0.00") ?? a.Error)) + $", passed {critique.Passed}");
            foreach (var a in critique.Attempts) Console.WriteLine($"attempt {a.Attempt}: {a.Score} {string.Join(" | ", a.Issues)} {a.Error}");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
