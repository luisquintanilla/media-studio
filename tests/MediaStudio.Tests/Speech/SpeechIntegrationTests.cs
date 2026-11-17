using MediaStudio.Configuration;
using MediaStudio.Speech;
using MediaStudio.Speech.Audio;
using MediaStudio.Speech.Kokoro;
using MediaStudio.Speech.Whisper;
using Microsoft.Extensions.AI;

namespace MediaStudio.Tests.Speech;

/// <summary>Skips unless Kokoro assets (MEDIASTUDIO_KOKORO_DIR or verified ModelAssets) and espeak-ng are available.</summary>
public sealed class KokoroFactAttribute : FactAttribute
{
    public KokoroFactAttribute()
    {
        if (SpeechIntegrationTests.KokoroPaths() is null) Skip = "Kokoro assets not found (set MEDIASTUDIO_KOKORO_DIR or run `assets ensure --id kokoro-v1.0-q8`).";
        else if (EspeakPhonemizer.Resolve() is null) Skip = "espeak-ng is not installed.";
    }
}

/// <summary>Additionally requires the Whisper ggml model (MEDIASTUDIO_WHISPER_MODEL or verified whisper-base asset).</summary>
public sealed class WhisperFactAttribute : FactAttribute
{
    public WhisperFactAttribute()
    {
        if (SpeechIntegrationTests.WhisperModel() is null) Skip = "Whisper model not found (set MEDIASTUDIO_WHISPER_MODEL or run `assets ensure --id whisper-base`).";
        else if (SpeechIntegrationTests.KokoroPaths() is null || EspeakPhonemizer.Resolve() is null) Skip = "Kokoro assets or espeak-ng missing.";
    }
}

public sealed class SpeechIntegrationTests
{
    private const string Sentence = "Hello world. This is Media Studio.";

    internal static KokoroAssetPaths? KokoroPaths()
    {
        var dir = Environment.GetEnvironmentVariable("MEDIASTUDIO_KOKORO_DIR");
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return KokoroAssetPaths.FromDirectory(dir);
        try { return KokoroAssetPaths.FromModelAssets(new ModelAssets(ModelManifest.LoadDefault())); }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    internal static string? WhisperModel()
    {
        var file = Environment.GetEnvironmentVariable("MEDIASTUDIO_WHISPER_MODEL");
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file)) return file;
        try
        {
            var dir = new ModelAssets(ModelManifest.LoadDefault()).GetPath(WhisperNetSpeechToTextClient.BaseAssetId);
            return dir is null ? null : Path.Combine(dir, "ggml-base.bin");
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    [KokoroFact]
    [Trait("Category", "Kokoro")]
    public async Task KokoroSynthesizesAudibleSpeech()
    {
        using var client = new KokoroTextToSpeechClient(KokoroPaths()!);
        var response = await client.GetAudioAsync(Sentence);

        var wav = WavFile.Read(Assert.IsType<DataContent>(Assert.Single(response.Contents)).Data.Span);
        Assert.Equal(24000, wav.SampleRate);
        var seconds = wav.Samples.Length / 24000.0;
        Assert.InRange(seconds, 1.5, 5.0);
        Assert.True(AudioMath.Rms(wav.Samples) > 0.01f, $"RMS {AudioMath.Rms(wav.Samples)}");
        var result = Assert.IsType<KokoroSynthesisResult>(response.RawRepresentation);
        Assert.All(result.Chunks, c => Assert.Equal(0, c.UnknownSymbols));
    }

    [WhisperFact]
    [Trait("Category", "Whisper")]
    public async Task KokoroSpeechRoundTripsThroughWhisper()
    {
        using var tts = new KokoroTextToSpeechClient(KokoroPaths()!);
        var audio = Assert.IsType<DataContent>(Assert.Single((await tts.GetAudioAsync(Sentence)).Contents));

        using var stt = new WhisperNetSpeechToTextClient(WhisperModel()!, "whisper-base");
        var response = await stt.GetTextAsync(new MemoryStream(audio.Data.ToArray()), new SpeechToTextOptions { SpeechLanguage = "en" });

        Assert.Contains("hello", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("studio", response.Text, StringComparison.OrdinalIgnoreCase);
        var words = TimedWord.From(response);
        Assert.Contains(words, w => w.Text.Contains("Hello", StringComparison.OrdinalIgnoreCase));
        Assert.True(words[^1].End <= TimeSpan.FromSeconds(audio.Data.Length / 48000.0 + 0.5), $"last word ends {words[^1].End}");
        Assert.Equal(words.OrderBy(w => w.Offset).Select(w => w.Text), words.Select(w => w.Text));
    }
}
