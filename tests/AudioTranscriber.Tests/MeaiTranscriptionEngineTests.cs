using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

#pragma warning disable MEAI001
namespace AudioTranscriber.Tests;

public sealed class MeaiTranscriptionEngineTests
{
    [Fact]
    public async Task Streaming_preserves_actual_timestamp_segments_without_service_discovery()
    {
        using var input = new Input();
        using var client = new Client();
        var result = await new MeaiTranscriptionEngine(client, WhisperNetIntegration.Provenance("fixture"))
            .TranscribeAsync(input.Audio);
        var segment = Assert.Single(result);
        Assert.Equal("Actual segment", segment.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(125), segment.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(750), segment.End);
    }

    [Fact]
    public async Task Cancelled_partial_enumeration_is_never_success()
    {
        using var input = new Input();
        using var cancel = new CancellationTokenSource();
        using var client = new Client(cancel: cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MeaiTranscriptionEngine(client, WhisperNetIntegration.Provenance("fixture"))
                .TranscribeAsync(input.Audio, cancel.Token).AsTask());
    }

    [Fact]
    public async Task Text_without_actual_timestamps_is_rejected()
    {
        using var input = new Input();
        using var client = new Client(missingClock: true);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new MeaiTranscriptionEngine(client, WhisperNetIntegration.Provenance("fixture"))
                .TranscribeAsync(input.Audio).AsTask());
    }

    [Fact]
    public async Task Editable_speech_input_uses_final_text_not_a_production_segment_ledger()
    {
        using var input = new Input();
        using var stream = File.OpenRead(input.Audio.SourcePath);
        using var client = new BatchClient("  Keep this spoken request.  ");
        var text = await MeaiSpeechInput.RecognizeAsync(client, stream);
        Assert.Equal("Keep this spoken request.", text);
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, client.StreamingCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_final_speech_is_an_explicit_failure(string text)
    {
        using var stream = new MemoryStream();
        using var client = new BatchClient(text);
        await Assert.ThrowsAsync<InvalidDataException>(() => MeaiSpeechInput.RecognizeAsync(client, stream));
    }

    [Fact]
    public async Task Overlong_final_speech_is_not_silently_truncated()
    {
        using var stream = new MemoryStream();
        using var client = new BatchClient(new string('x', 8001));
        await Assert.ThrowsAsync<InvalidDataException>(() => MeaiSpeechInput.RecognizeAsync(client, stream));
    }

    [Fact]
    public async Task Provider_returning_partial_text_after_cancel_cannot_insert_success()
    {
        using var stream = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        using var client = new BatchClient("Cancelled partial speech", cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MeaiSpeechInput.RecognizeAsync(client, stream, cancellation.Token));
    }

    [Fact]
    public async Task Production_transcripts_still_reject_out_of_capture_timestamps()
    {
        using var input = new Input();
        using var client = new BatchClient("Voice text with model timestamps beyond captured samples");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new MeaiTranscriptionEngine(client, WhisperNetIntegration.Provenance("fixture"))
                .TranscribeAsync(input.Audio).AsTask());
        Assert.Equal(1, client.StreamingCalls);
    }

    [Fact]
    public async Task Unsupported_final_content_is_not_silently_ignored()
    {
        using var stream = new MemoryStream();
        using var client = new BatchClient("Text mixed with unsupported content", unsupported: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => MeaiSpeechInput.RecognizeAsync(client, stream));
    }

    private sealed class BatchClient(string text, CancellationTokenSource? cancel = null, bool unsupported = false) : ISpeechToTextClient
    {
        public int Calls, StreamingCalls;
        public Task<SpeechToTextResponse> GetTextAsync(Stream audioSpeechStream,
            SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal("en", options?.SpeechLanguage);
            Assert.Equal(16000, options?.SpeechSampleRate);
            Calls++; cancel?.Cancel();
            return Task.FromResult(new SpeechToTextResponse {
                Contents = unsupported ? [new TextContent(text), new DataContent(new byte[1], "audio/wav")] : [new TextContent(text)],
                StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromSeconds(2)
            });
        }
        public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(Stream audioSpeechStream,
            SpeechToTextOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamingCalls++; await Task.Yield();
            yield return new(text) { StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromSeconds(2) };
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => throw new NotSupportedException();
        public void Dispose() { }
    }

    [Fact]
    public async Task Production_transcripts_still_reject_overlapping_segments()
    {
        using var input = new Input();
        using var client = new Client(overlap: true);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new MeaiTranscriptionEngine(client, WhisperNetIntegration.Provenance("fixture"))
                .TranscribeAsync(input.Audio).AsTask());
    }

    private sealed class Client(bool missingClock = false, CancellationTokenSource? cancel = null, bool overlap = false) : ISpeechToTextClient
    {
        public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(Stream audioSpeechStream,
            SpeechToTextOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Assert.Equal("en", options?.SpeechLanguage);
            Assert.Equal(16000, options?.SpeechSampleRate);
            await Task.Yield();
            yield return new("Actual segment") {
                StartTime = missingClock ? null : TimeSpan.FromMilliseconds(125),
                EndTime = missingClock ? null : TimeSpan.FromMilliseconds(750)
            };
            if (overlap)
                yield return new("Overlapping segment") { StartTime = TimeSpan.FromMilliseconds(500), EndTime = TimeSpan.FromMilliseconds(900) };
            cancel?.Cancel();
        }
        public Task<SpeechToTextResponse> GetTextAsync(Stream audioSpeechStream,
            SpeechToTextOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Aggregate text is not a segment ledger.");
        public object? GetService(Type serviceType, object? serviceKey = null) =>
            throw new NotImplementedException("Provider discovery is not supported.");
        public void Dispose() { }
    }

    private sealed class Input : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        public AudioClip Audio { get; }
        public Input()
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                VideoProduction.Wav.WriteHeader(writer, 16000, 16000);
                writer.Write(new byte[32000]);
            }
            Audio = new WavAudioReader().Load(path);
        }
        public void Dispose() => File.Delete(path);
    }
}
