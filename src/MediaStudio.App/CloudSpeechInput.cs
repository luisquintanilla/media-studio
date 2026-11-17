using System.IO;
using System.Text;
using Microsoft.Extensions.AI;
using Sdk = OpenAI.Realtime;

#pragma warning disable MEAI001, OPENAI002
namespace MediaStudio.App;

internal static class CloudSpeechInput
{
    public const int SampleRate = 24000;
    public static RealtimeSessionOptions Options(string model) => new() {
        SessionKind = RealtimeSessionKind.Transcription,
        InputAudioFormat = new("audio/pcm", SampleRate),
        TranscriptionOptions = new() { ModelId = model },
        VoiceActivityDetection = new() { Enabled = false }
    };

    public static async Task<string> ReadAsync(IRealtimeClientSession session, Action<string> provisional,
        Func<bool> stopping, Action configured, CancellationToken cancellationToken)
    {
        string? item = null;
        var text = new StringBuilder();
        var acknowledged = false;
        var turnCommitted = false;
        var controls = 0;
        await foreach (var message in session.GetStreamingResponseAsync(cancellationToken).ConfigureAwait(false))
        {
            if (message is ErrorRealtimeServerMessage failure)
                throw new IOException("Cloud transcription failed: " + failure.Error?.Message);
            if (message is InputAudioTranscriptionRealtimeServerMessage speech)
            {
                if (!acknowledged || speech.ContentIndex != 0 || string.IsNullOrWhiteSpace(speech.ItemId))
                    throw new InvalidDataException("Cloud transcription arrived before setup or without its content identity.");
                item ??= speech.ItemId;
                if (item != speech.ItemId)
                    throw new InvalidDataException("Cloud transcription changed its recording item identity.");
                if (speech.Type == RealtimeServerMessageType.InputAudioTranscriptionFailed)
                    throw new IOException("Cloud transcription failed: " + speech.Error?.Message);
                if (speech.Type == RealtimeServerMessageType.InputAudioTranscriptionDelta)
                {
                    if (speech.Transcription is null || text.Length + speech.Transcription.Length > 8000)
                        throw new InvalidDataException("Cloud transcription returned an invalid or overlong delta.");
                    text.Append(speech.Transcription);
                    provisional(text.ToString());
                }
                else if (speech.Type == RealtimeServerMessageType.InputAudioTranscriptionCompleted)
                {
                    if (!stopping() || !turnCommitted)
                        throw new InvalidDataException("Cloud transcription completed before the recording's explicit Stop/commit acknowledgement.");
                    if (string.IsNullOrWhiteSpace(speech.Transcription) || speech.Transcription.Length > 8000)
                        throw new InvalidDataException("Cloud transcription returned an empty or overlong final result.");
                    return speech.Transcription;
                }
                else throw new InvalidDataException("Unexpected cloud transcription message type.");
            }
            else
            {
                if (++controls > 256) throw new InvalidDataException("Cloud speech exceeded the bounded control-message count.");
                if (message.RawRepresentation is Sdk.RealtimeServerUpdateSessionUpdated)
                {
                    if (acknowledged) throw new InvalidDataException("Cloud speech received an unsolicited session update.");
                    acknowledged = true; configured();
                }
                else if (message.RawRepresentation is Sdk.RealtimeServerUpdateSessionCreated) { }
                else if (message.RawRepresentation is Sdk.RealtimeServerUpdateInputAudioBufferCommitted commit)
                {
                    if (!acknowledged || !stopping() || turnCommitted || string.IsNullOrWhiteSpace(commit.ItemId)
                        || !string.IsNullOrEmpty(commit.PreviousItemId) || item is not null && item != commit.ItemId)
                        throw new InvalidDataException("Cloud audio commit did not correlate to this single recording turn.");
                    item = commit.ItemId; turnCommitted = true;
                }
                else if (message.Type != RealtimeServerMessageType.ConversationItemAdded
                    && message.Type != RealtimeServerMessageType.ConversationItemDone)
                    throw new InvalidDataException("Unexpected cloud speech control event; no conversational response or tool call is authorized.");
            }
        }
        throw new InvalidDataException("Cloud speech ended without an authoritative completed transcription.");
    }
}
#pragma warning restore MEAI001, OPENAI002
