using MediaStudio.Configuration;
using MediaStudio.Speech.Azure;
using MediaStudio.Speech.Kokoro;
using MediaStudio.Speech.Whisper;
using Microsoft.Extensions.AI;

namespace MediaStudio.Speech;

/// <summary>
/// Creates the narration (<see cref="ITextToSpeechClient"/>) and transcription (<see cref="ISpeechToTextClient"/>) clients from
/// <see cref="ProviderSettings.Speech"/>. Local Kokoro / Whisper are the defaults; Azure Speech (MAI-Voice / MAI-Transcribe-2) is
/// opt-in and refused unless <c>privacy.allowCloud</c> is true. Consumers depend only on the MEAI interfaces (keys
/// <see cref="NarrationKey"/> and <see cref="TranscriptionKey"/>).
/// </summary>
public sealed class SpeechClientFactory(ProviderSettings settings, ModelAssets assets, HttpMessageHandler? azureInnerHandler = null)
{
    public const string NarrationKey = "narration";
    public const string TranscriptionKey = "transcription";

    public ProviderSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));
    public ModelAssets Assets { get; } = assets ?? throw new ArgumentNullException(nameof(assets));

    public bool IsTextToSpeechCloud => Settings.Speech.Tts.Provider != TtsProvider.Kokoro;
    public bool IsSpeechToTextCloud => Settings.Speech.Stt.Provider != SttProvider.Whisper;

    public ITextToSpeechClient CreateTextToSpeech()
    {
        var tts = Settings.Speech.Tts;
        switch (tts.Provider)
        {
            case TtsProvider.Kokoro:
                var fp32 = string.Equals(tts.Quality, "fp32", StringComparison.OrdinalIgnoreCase);
                return new KokoroTextToSpeechClient(KokoroAssetPaths.FromModelAssets(Assets, fp32), options: new() {
                    DefaultVoice = string.IsNullOrWhiteSpace(tts.Voice) ? "af_heart" : tts.Voice,
                    DefaultSpeed = (float)tts.Speed
                });
            case TtsProvider.AzureSpeech:
                var connection = CloudConnection(tts.Connection, "speech.tts");
                var options = AzureSpeechTextToSpeechOptions.FromManifest(Assets.Manifest, AzureVoice(tts.Voice), tts.Style);
                return AzureSpeechTextToSpeechClient.Create(connection, options, azureInnerHandler);
            default:
                throw new NotSupportedException($"speech.tts.provider '{tts.Provider}' is not supported; use kokoro or azure-speech.");
        }
    }

    public ISpeechToTextClient CreateSpeechToText()
    {
        var stt = Settings.Speech.Stt;
        switch (stt.Provider)
        {
            case SttProvider.Whisper:
                return WhisperNetSpeechToTextClient.FromModelAssets(Assets, WhisperNetSpeechToTextClient.AssetIdFor(stt.Model));
            case SttProvider.AzureSpeech:
                var connection = CloudConnection(stt.Connection, "speech.stt");
                return AzureSpeechTranscriptionClient.Create(connection, new() {
                    Model = string.IsNullOrWhiteSpace(stt.Model) || stt.Model.StartsWith("large", StringComparison.OrdinalIgnoreCase) || stt.Model == "base"
                        ? AzureSpeechTranscriptionOptions.DefaultModel : stt.Model,
                    Diarization = stt.Diarization,
                    TranscribeStyle = string.IsNullOrWhiteSpace(stt.Style) ? "clean" : stt.Style
                }, azureInnerHandler);
            default:
                throw new NotSupportedException($"speech.stt.provider '{stt.Provider}' is not supported; use whisper or azure-speech.");
        }
    }

    // Kokoro voice ids (af_heart) are not Azure voices; fall back to the manifest default.
    private static string? AzureVoice(string? voice) => voice is not null && voice.Contains('-') ? voice : null;

    private ConnectionSettings CloudConnection(string? connectionId, string setting)
    {
        CloudGuard.Require(Settings, setting, alternative: $"switch {setting}.provider to the local engine");
        var connection = Settings.FindConnection(connectionId)
            ?? Settings.Connections.FirstOrDefault(c => c.Kind == ConnectionKind.Foundry)
            ?? throw new InvalidOperationException($"{setting}.connection '{connectionId}' was not found; add a Foundry connection (run `configure`).");
        if (connection.Kind != ConnectionKind.Foundry)
            throw new InvalidOperationException($"{setting}.connection '{connection.Id}' must be a Foundry connection for Azure Speech.");
        return connection;
    }
}
