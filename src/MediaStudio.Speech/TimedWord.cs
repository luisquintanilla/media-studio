namespace MediaStudio.Speech;

/// <summary>
/// A recognized word with its position in the audio. Local (Whisper) and cloud (MAI-Transcribe) speech-to-text clients
/// both publish an ordered <c>IReadOnlyList&lt;TimedWord&gt;</c> under <see cref="WordsKey"/> in
/// <c>SpeechToTextResponse.AdditionalProperties</c>, so captions read a single key.
/// </summary>
public sealed record TimedWord(string Text, TimeSpan Offset, TimeSpan Duration, int? Speaker = null)
{
    public const string WordsKey = "words";

    public TimeSpan End => Offset + Duration;

    /// <summary>Reads the word list from a response, or an empty list when the client returned none.</summary>
    public static IReadOnlyList<TimedWord> From(Microsoft.Extensions.AI.SpeechToTextResponse response) =>
        response.AdditionalProperties?.TryGetValue(WordsKey, out var value) == true && value is IReadOnlyList<TimedWord> words
            ? words
            : [];
}
