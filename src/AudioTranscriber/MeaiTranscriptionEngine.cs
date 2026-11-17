using Microsoft.Extensions.AI;

#pragma warning disable MEAI001
namespace AudioTranscriber;

public sealed class MeaiTranscriptionEngine(ISpeechToTextClient client, ModelProvenance provenance)
    : ITranscriptionEngine
{
    public ModelProvenance Provenance { get; } = provenance ?? throw new ArgumentNullException(nameof(provenance));

    public async ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        AudioClip audio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        AudioRequirements.Validate(audio);
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = File.OpenRead(audio.SourcePath);
        var segments = new List<TranscriptSegment>();
        await foreach (var update in client.GetStreamingTextAsync(stream,
            new SpeechToTextOptions { SpeechLanguage = "en", SpeechSampleRate = 16000 },
            cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(update.Text)) continue;
            if (update.StartTime is not { } start || update.EndTime is not { } end)
                throw new InvalidDataException("Speech provider returned text without actual segment timestamps.");
            var segment = new TranscriptSegment(update.Text, start, end);
            if (segment.End > audio.Duration || segments.Count > 0 && segment.Start < segments[^1].End)
                throw new InvalidDataException("Speech provider returned out-of-range or overlapping segments.");
            segments.Add(segment);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return segments.AsReadOnly();
    }
}
