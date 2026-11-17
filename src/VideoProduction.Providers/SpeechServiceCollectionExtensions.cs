using MediaStudio.Configuration;
using MediaStudio.Speech;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace VideoProduction;

#pragma warning disable MEAI001 // MEAI speech abstractions are experimental.
public static class SpeechServiceCollectionExtensions
{
    /// <summary>
    /// Registers the settings-selected speech clients as keyed MEAI services: <c>"narration"</c> (<see cref="ITextToSpeechClient"/>)
    /// and <c>"transcription"</c> (<see cref="ISpeechToTextClient"/>). Clients are created lazily on first resolve, so a missing
    /// model or a cloud provider refused by <c>privacy.allowCloud</c> surfaces as an actionable error only when speech is used.
    /// </summary>
    public static IServiceCollection AddMediaStudioSpeech(this IServiceCollection services,
        ProviderSettings? settings = null, ModelAssets? assets = null)
    {
        services.AddSingleton(_ => new SpeechClientFactory(
            settings ?? new ProviderSettingsStore().TryLoad() ?? new ProviderSettings(),
            assets ?? new ModelAssets(ModelManifest.LoadDefault())));
        services.AddKeyedSingleton<ITextToSpeechClient>(SpeechClientFactory.NarrationKey,
            (sp, _) => sp.GetRequiredService<SpeechClientFactory>().CreateTextToSpeech());
        services.AddKeyedSingleton<ISpeechToTextClient>(SpeechClientFactory.TranscriptionKey,
            (sp, _) => sp.GetRequiredService<SpeechClientFactory>().CreateSpeechToText());
        return services;
    }
}
#pragma warning restore MEAI001
