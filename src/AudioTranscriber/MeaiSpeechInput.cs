using Microsoft.Extensions.AI;

#pragma warning disable MEAI001
namespace AudioTranscriber;

public static class MeaiSpeechInput
{
    public static async Task<string> RecognizeAsync(ISpeechToTextClient client, Stream audio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(audio);
        cancellationToken.ThrowIfCancellationRequested();
        // Spoken input is editable text, not a sample-clocked production transcript.
        var response = await client.GetTextAsync(audio,
            new SpeechToTextOptions { SpeechLanguage = "en", SpeechSampleRate = 16000 }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (response.Contents.Any(c => c is not TextContent))
            throw new InvalidDataException("Speech input returned unsupported content. Nothing was inserted.");
        var text = response.Text.Trim();
        if (text.Length is 0 or > 8000)
            throw new InvalidDataException("Speech input returned no usable speech or an overlong utterance. Nothing was inserted.");
        return text;
    }
}
