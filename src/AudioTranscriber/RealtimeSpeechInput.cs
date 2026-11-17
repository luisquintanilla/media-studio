using Microsoft.Extensions.AI;

#pragma warning disable MEAI001
namespace AudioTranscriber;

public static class RealtimeSpeechInput
{
    public static RealtimeSessionOptions Options => new() {
        SessionKind = RealtimeSessionKind.Transcription,
        InputAudioFormat = new(RealtimePcm.MediaType, RealtimePcm.SampleRate),
        TranscriptionOptions = new() { SpeechLanguage = "en" }
    };

    public static async Task<string> ReadAsync(IRealtimeClientSession session, Action<string> provisional,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(provisional);
        string? item = null, final = null;
        await foreach (var message in session.GetStreamingResponseAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message is not InputAudioTranscriptionRealtimeServerMessage transcription || final is not null
                || transcription.ContentIndex != 0 || string.IsNullOrWhiteSpace(transcription.ItemId))
                throw new InvalidDataException("Unexpected realtime speech content, item identity or post-final message.");
            item ??= transcription.ItemId;
            if (item != transcription.ItemId) throw new InvalidDataException("The realtime transcription item changed during this recording.");
            if (transcription.Type == RealtimeServerMessageType.InputAudioTranscriptionFailed)
                throw new InvalidDataException("Local realtime speech failed: " + transcription.Error?.Message);
            if (transcription.Type == RealtimeServerMessageType.InputAudioTranscriptionDelta)
            {
                if (transcription.RawRepresentation is not string snapshot || snapshot.Length > 8000)
                    throw new InvalidDataException("The local realtime provider did not supply a bounded revisable hypothesis.");
                provisional(snapshot);
            }
            else if (transcription.Type == RealtimeServerMessageType.InputAudioTranscriptionCompleted)
            {
                final = transcription.Transcription;
                if (string.IsNullOrWhiteSpace(final) || final.Length > 8000)
                    throw new InvalidDataException("The local realtime provider returned empty or overlong final speech.");
            }
            else throw new InvalidDataException("Unexpected realtime transcription message type.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return final ?? throw new InvalidDataException("Realtime speech ended without a completed transcription; provisional text was not inserted.");
    }

    public static async Task<string> RecognizePcmAsync(IRealtimeClient client, Stream pcm, Action<string> provisional,
        CancellationToken cancellationToken = default)
    {
        await using var session = await client.CreateSessionAsync(Options, cancellationToken).ConfigureAwait(false);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reading = ObserveRead();
        async Task<string> ObserveRead()
        {
            try { return await ReadAsync(session, provisional, stop.Token).ConfigureAwait(false); }
            catch { stop.Cancel(); throw; }
        }
        try
        {
            var buffer = new byte[RealtimePcm.MaximumChunkBytes];
            int length, buffered = 0;
            while ((length = await pcm.ReadAsync(buffer.AsMemory(buffered), stop.Token).ConfigureAwait(false)) != 0)
            {
                buffered += length;
                var aligned = buffered & ~1;
                if (aligned > 0)
                    await session.SendAsync(new InputAudioBufferAppendRealtimeClientMessage(
                        new DataContent(buffer.AsMemory(0, aligned), RealtimePcm.MediaType)), stop.Token).ConfigureAwait(false);
                if (buffered != aligned) buffer[0] = buffer[aligned];
                buffered &= 1;
            }
            if (buffered != 0) throw new InvalidDataException("PCM16 ended with an incomplete sample; no final speech was committed.");
            await session.SendAsync(new InputAudioBufferCommitRealtimeClientMessage(), stop.Token).ConfigureAwait(false);
            return await reading.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            stop.Cancel();
            Exception? readerFailure = null;
            try { await reading.ConfigureAwait(false); }
            catch (Exception failure) { readerFailure = failure; }
            if (readerFailure is not null and not OperationCanceledException && !ReferenceEquals(error, readerFailure))
                throw new AggregateException("Local realtime input and its response consumer failed; no transcript was accepted.", error, readerFailure);
            throw;
        }
    }
}
#pragma warning restore MEAI001
