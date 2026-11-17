using AudioTranscriber;
using Microsoft.Extensions.AI;
using System.Threading.Channels;
using System.IO;

#pragma warning disable MEAI001
namespace MediaStudio.App;

internal sealed class RealtimeDictation : IAsyncDisposable
{
    private readonly IRealtimeClientSession session;
    private readonly CancellationTokenSource stop = new(TimeSpan.FromMinutes(2));
    private readonly Channel<byte[]> input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task sending;
    private readonly Task<string> reading;
    private Exception? failure;
    private Task? disposal;
    private readonly object lifecycle = new();
    private bool disposed;
    private bool committed;
    private readonly string stopCommitId = "stop-" + Guid.NewGuid().ToString("N");
    private readonly bool cloud;
    private int sentBytes;
    private readonly TaskCompletionSource configured = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Exception? Failure => Volatile.Read(ref failure);

    private RealtimeDictation(IRealtimeClientSession session, Action<string> provisional, bool cloud)
    {
        this.session = session;
        this.cloud = cloud;
        reading = Read();
        sending = Task.Run(Send);
        async Task<string> Read()
        {
            try
            {
                return cloud
                ? await CloudSpeechInput.ReadAsync(session, provisional, () => Volatile.Read(ref committed),
                    () => configured.TrySetResult(), stop.Token).ConfigureAwait(false)
                : await RealtimeSpeechInput.ReadAsync(session, provisional, stop.Token).ConfigureAwait(false);
            }
            catch (Exception error) { Fail(error); throw; }
        }
        async Task Send()
        {
            try
            {
                await foreach (var data in input.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                {
                    await session.SendAsync(new InputAudioBufferAppendRealtimeClientMessage(new DataContent(data, "audio/pcm")), stop.Token).ConfigureAwait(false);
                    sentBytes += data.Length;
                }
                if (cloud && sentBytes < CloudSpeechInput.SampleRate / 5)
                    throw new InvalidDataException("Cloud dictation needs at least100ms of captured PCM. No padding or fabricated audio was sent.");
                Volatile.Write(ref committed, true);
                await session.SendAsync(new InputAudioBufferCommitRealtimeClientMessage { MessageId = cloud ? stopCommitId : null }, stop.Token).ConfigureAwait(false);
            }
            catch (Exception error) { Fail(error); throw; }
        }
    }
    public static async Task<RealtimeDictation> StartAsync(IRealtimeClient client, Action<string> provisional, CancellationToken cancellationToken,
        string? cloudModel = null)
    {
        var session = await client.CreateSessionAsync(cloudModel is null ? RealtimeSpeechInput.Options : CloudSpeechInput.Options(cloudModel), cancellationToken).ConfigureAwait(false);
        var active = new RealtimeDictation(session, provisional, cloudModel is not null);
        if (cloudModel is not null)
        {
            try { await active.configured.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false); }
            catch { await active.DisposeAsync().ConfigureAwait(false); throw; }
        }
        return active;
    }
    private void Fail(Exception error)
    {
        Interlocked.CompareExchange(ref failure, error, null);
        if (cloud) configured.TrySetException(error);
        Cancel();
        input.Writer.TryComplete(error);
    }
    public void Offer(ReadOnlyMemory<byte> pcm)
    {
        if (Failure is not null) return;
        for (var offset = 0; offset < pcm.Length; offset += RealtimePcm.MaximumChunkBytes)
        {
            var chunk = pcm.Slice(offset, Math.Min(RealtimePcm.MaximumChunkBytes, pcm.Length - offset)).ToArray();
            if (!input.Writer.TryWrite(chunk))
            {
                Fail(new IOException("Streaming recognition could not keep up with bounded capture. Recording is retained for retry; no audio was silently dropped into a successful transcript."));
                return;
            }
        }
    }
    public async Task<string> FinishAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(Cancel);
        input.Writer.TryComplete();
        try { await Task.WhenAll(sending, reading).ConfigureAwait(false); return await reading.ConfigureAwait(false); }
        catch when (Failure is { } error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw(); throw; }
    }
    public static async Task<string> ReplayCloudAsync(IRealtimeClient client, string model, Stream pcm,
        Action<string> provisional, CancellationToken cancellationToken)
    {
        await using var active = await StartAsync(client, provisional, cancellationToken, model).ConfigureAwait(false);
        using var registration = cancellationToken.Register(active.Cancel);
        var buffer = new byte[RealtimePcm.MaximumChunkBytes];
        int count, buffered = 0;
        while ((count = await pcm.ReadAsync(buffer.AsMemory(buffered), cancellationToken).ConfigureAwait(false)) != 0)
        {
            buffered += count;
            var aligned = buffered & ~1;
            if (aligned > 0) await active.input.Writer.WriteAsync(buffer.AsSpan(0, aligned).ToArray(), active.stop.Token).ConfigureAwait(false);
            if (buffered != aligned) buffer[0] = buffer[aligned];
            buffered &= 1;
        }
        if (buffered != 0) throw new InvalidDataException("Cloud PCM replay ended on an incomplete sample.");
        return await active.FinishAsync(cancellationToken).ConfigureAwait(false);
    }
    public void Cancel()
    {
        lock (lifecycle) { if (!disposed) stop.Cancel(); }
        input.Writer.TryComplete();
    }
    public ValueTask DisposeAsync()
    {
        lock (lifecycle) return new(disposal ??= DisposeCore());
    }
    private async Task DisposeCore()
    {
        Cancel();
        try { await Task.WhenAll(sending, reading).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        // FinishAsync surfaces recorded worker failure; disposal observes both workers without replacing that cause.
        catch (Exception) when (Failure is not null) { }
        finally
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            finally { lock (lifecycle) { disposed = true; stop.Dispose(); } }
        }
    }
}
#pragma warning restore MEAI001
