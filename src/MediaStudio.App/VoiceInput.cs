using System.IO;
using System.Diagnostics;
using System.Windows.Threading;
using VideoProduction.Providers;
using AudioTranscriber;
using Microsoft.Extensions.AI;
using System.Text.Json;

#pragma warning disable MEAI001
namespace MediaStudio.App;

public sealed class VoiceInput : IDisposable, IAsyncDisposable
{
    private LocalWhisper? whisper;
    private IRealtimeClient? realtime;
    private RealtimeDictation? streaming;
    private CancellationTokenSource? opening;
    private Task? closing;
    private TaskCompletionSource? operation;
    private Func<string, Action<ReadOnlyMemory<byte>>, IVoiceCapture> realtimeCaptureFactory = (file, pcm) => new WaveCapture(file, pcm);
    private string? whisperConfiguration;
    private string? cloudModel, cloudConnection;
    private readonly string root;
    private readonly string setupRoot;
    private sealed record Selection(int SchemaVersion, string ConfigurationFile, string? BatchConfigurationFile = null, string? RealtimeConfigurationFile = null);
    private IVoiceCapture? capture;
    private Func<string, IVoiceCapture> captureFactory;
    private Func<uint> deviceCount;
    private Func<string, CancellationToken, Task<string>>? recognize;
    private bool disposed;
    private string? pendingWave;
    private CancellationTokenSource? inference;
    private readonly object lifecycle = new();
    private readonly DispatcherTimer timer;
    private readonly Stopwatch started = new();
    public bool Recording => capture is not null;
    public bool Transcribing => inference is not null;
    public bool Initializing => opening is not null;
    public bool Busy => Recording || Transcribing || Initializing || closing is { IsCompleted: false };
    public bool AwaitingInsertion { get; private set; }
    public bool InputPending => Busy || AwaitingInsertion || HasRecording;
    public string Provider { get; private set; } = "Whisper";
    public bool IsCloud => Provider == "LiveTranscribe";
    public bool CloudUploadAllowed { get; private set; }
    public bool IsRealtime => IsCloud;
    public string ProviderLabel => IsCloud ? Configured ? $"{cloudModel} · cloud speech" : "Speech off"
        : "Whisper · local speech";
    public string ProviderDetails => IsCloud ? $"{cloudConnection} · {cloudModel}. Configured provider; live authentication/quality is not verified by setup. Transcription-only MEAI realtime session; audio is uploaded and billed by duration."
        : LocalWhisper.Disclosure;
    public string CloudNotice => "Words appear as you speak. Stop adds final text to your prompt. Max 60 seconds; nothing is sent as a prompt.";
    public string? ProvisionalText { get; private set; }
    public bool Configured => IsRealtime ? realtime is not null : whisper is not null;
    public bool HasRecording => pendingWave is not null && !Recording;
    public uint Devices => deviceCount();
    public string? Transcript { get; private set; }
    public long TranscriptVersion { get; private set; }
    public long RecordingVersion { get; private set; }
    public string? InsertionTarget { get; private set; }
    public TimeSpan Elapsed { get; private set; }
    public string? Error { get; private set; }
    public string Status { get; private set; } = "Idle. Microphone is not recording.";
    public string? UnavailableReason => Error ?? (!Configured
        ? "Set up the selected speech provider in Settings. No model is downloaded or alternate provider used automatically."
        : Devices == 0 ? "No microphone is available. Check Windows microphone access and your input device." : null);
    public event Action? Changed;

    public VoiceInput(string privateRoot, string? configFile)
        : this(privateRoot, configFile, file => new WaveCapture(file), () => WaveCapture.DeviceCount) { }

    internal VoiceInput(string privateRoot, string? configFile, Func<string, IVoiceCapture> captureFactory, Func<uint> deviceCount,
        Func<string, CancellationToken, Task<string>>? recognize = null)
    {
        this.captureFactory = captureFactory;
        this.deviceCount = deviceCount;
        this.recognize = recognize;
        setupRoot = Path.Combine(privateRoot, "local-voice");
        root = Path.Combine(privateRoot, "voice-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (File.Exists(Path.Combine(setupRoot, "selection.json")))
            {
                var selection = PrivateFiles.Read<Selection>(Path.Combine(setupRoot, "selection.json"));
                if (selection.SchemaVersion is not (1 or 2)) throw new InvalidDataException("Unsupported local voice selection.");
                whisperConfiguration = selection.BatchConfigurationFile;
                configFile ??= selection.ConfigurationFile;
            }
            if (configFile is not null) LoadConfiguration(PrivateFiles.LocalPath(configFile));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException
            or VideoProduction.VideoException)
        { Error = error.Message; }
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += async (_, _) =>
        {
            try { await CheckLimitAsync(); }
            catch (Exception error) { Error = error.Message; Changed?.Invoke(); }
        };
    }

    public void Configure(string configurationFile)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (InputPending) throw new InvalidOperationException("Stop recording/transcription and finish or discard dictation before changing voice setup.");
        var path = PrivateFiles.LocalPath(configurationFile);
        LoadConfiguration(path);
        Directory.CreateDirectory(setupRoot);
        PrivateFiles.Protect(setupRoot, directory: true);
        VideoProduction.Json.Write(Path.Combine(setupRoot, "selection.json"), new Selection(2, path, whisperConfiguration));
        PrivateFiles.Protect(Path.Combine(setupRoot, "selection.json"));
        Error = null;
        Status = Provider + " local voice selected. Choose Record to start; nothing is sent automatically.";
        Changed?.Invoke();
    }
    private void LoadConfiguration(string path)
    {
        PrivateFiles.RequirePrivate(path);
        if (new FileInfo(path).Length is < 2 or > 65536) throw new InvalidDataException("Local voice configuration must be a bounded JSON document.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        whisper = new(path); whisperConfiguration = path; Provider = "Whisper";
    }
    public void SelectProvider(string provider)
    {
        if (provider is not "Whisper") throw new InvalidOperationException("Choose Whisper local recognition.");
        var path = whisperConfiguration;
        if (path is null) throw new InvalidOperationException(provider + " needs explicit local setup. Choose its setup control; nothing was downloaded or switched automatically.");
        Configure(path);
    }
    public void ConfigureCloud(IRealtimeClient client, string model, string connection, bool uploadAllowed = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (InputPending) throw new InvalidOperationException("Finish or discard pending speech before changing the provider.");
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(connection))
            throw new InvalidDataException("Cloud speech requires an explicit deployment and connection.");
        realtime?.Dispose(); realtime = client;
        cloudModel = model; cloudConnection = connection; Provider = "LiveTranscribe";
        CloudUploadAllowed = uploadAllowed;
        realtimeCaptureFactory = (file, pcm) => new WaveCapture(file, pcm, CloudSpeechInput.SampleRate);
        Error = null; Status = uploadAllowed ? "Cloud speech selected. Ready to record." : "Select this speech model in Settings to allow audio upload and cost.";
        Changed?.Invoke();
    }
    public void ClearCloud()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (InputPending) throw new InvalidOperationException("Finish or discard pending speech before clearing the provider.");
        realtime?.Dispose(); realtime = null; cloudModel = null; cloudConnection = null;
        CloudUploadAllowed = false;
        Provider = "LiveTranscribe";
        Error = null; Status = "Cloud speech is unassigned. Choose a provider explicitly; no local fallback or recording was started.";
        Changed?.Invoke();
    }
    internal void AllowConfiguredCloudUpload(bool allowed)
    {
        if (InputPending) throw new InvalidOperationException("Finish pending speech before changing upload permission.");
        CloudUploadAllowed = allowed;
        Changed?.Invoke();
    }

    public void SetUp(string existingModel)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (InputPending) throw new InvalidOperationException("Finish or discard dictation before changing voice setup.");
        Configure(LocalWhisper.SetUp(setupRoot, existingModel));
        Status = "Whisper Base model and this app's CPU runtime verified. Setup saved; choose Record to start.";
        Changed?.Invoke();
    }

    internal void UseVerificationCapture(Func<string, IVoiceCapture> factory,
        Func<string, CancellationToken, Task<string>> recognition)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Busy) throw new InvalidOperationException("Finish the voice operation before installing an explicit verification fixture.");
        captureFactory = factory;
        recognize = recognition;
        deviceCount = () => 1;
    }

    internal async Task CheckLimitAsync()
    {
        if (capture is not { } recording) return;
        if (recording.ReachedLimit || recording.Error is not null || streaming?.Failure is not null || started.Elapsed >= TimeSpan.FromSeconds(WaveCapture.MaximumSeconds))
        {
            await StopAndTranscribeAsync();
        }
        else { Elapsed = started.Elapsed; Changed?.Invoke(); }
    }

    public void Start() => Start(null);
    public void Start(string? insertionTarget)
    {
        if (IsRealtime) throw new InvalidOperationException("Realtime speech starts asynchronously. Use the shared speech-input control.");
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Configured || InputPending) throw new InvalidOperationException("Configure local Whisper and finish or discard the current voice action first.");
        if (Devices == 0) throw new InvalidOperationException("No Windows microphone device is available. Nothing was recorded.");
        Discard();
        InsertionTarget = insertionTarget;
        Directory.CreateDirectory(root);
        PrivateFiles.Protect(root, directory: true);
        pendingWave = Path.Combine(root, Guid.NewGuid().ToString("N") + ".wav");
        try { capture = captureFactory(pendingWave); }
        catch { DeleteWave(); throw; }
        started.Restart();
        RecordingVersion++;
        Elapsed = TimeSpan.Zero;
        Status = "Recording locally. Stop when finished; automatic limit: 60 seconds.";
        timer.Start();
        Changed?.Invoke();
    }

    public async Task StartAsync(string? insertionTarget, bool cloudConsent = false)
    {
        if (!IsRealtime) { Start(insertionTarget); return; }
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsCloud && !cloudConsent) throw new InvalidOperationException("Confirm cloud audio upload and transcription billing before Record.");
        if (!Configured || InputPending) throw new InvalidOperationException("Configure the selected realtime speech provider and finish or discard the current voice action first.");
        if (Devices == 0) throw new InvalidOperationException("No Windows microphone device is available. Nothing was recorded.");
        Discard();
        InsertionTarget = insertionTarget; RecordingVersion++;
        var version = RecordingVersion;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (lifecycle) { opening = cancellation; operation = completion; }
        Status = "Connecting the configured cloud transcription-only session. Microphone is not recording yet.";
        Changed?.Invoke();
        try
        {
            streaming = await RealtimeDictation.StartAsync(realtime!, text =>
            {
                if (disposed || RecordingVersion != version) return;
                ProvisionalText = text; Changed?.Invoke();
            }, cancellation.Token, IsCloud ? cloudModel : null);
            cancellation.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposed, this);
            Directory.CreateDirectory(root); PrivateFiles.Protect(root, directory: true);
            pendingWave = Path.Combine(root, Guid.NewGuid().ToString("N") + ".wav");
            capture = realtimeCaptureFactory(pendingWave, streaming.Offer);
            started.Restart(); Elapsed = TimeSpan.Zero;
            Status = "Recording and uploading PCM to cloud speech. Live transcript deltas are provisional; Stop finalizes them. No prompt is sent.";
            timer.Start();
        }
        catch (Exception error)
        {
            Error = error.Message; Status = "Realtime input could not start. No prompt was sent.";
            try { if (streaming is { } failed) { streaming = null; await failed.DisposeAsync(); } }
            catch (Exception cleanup) { throw new AggregateException("Local realtime startup and cleanup failed.", error, cleanup); }
            finally { DeleteWave(); }
            throw;
        }
        finally { lock (lifecycle) opening = null; completion.TrySetResult(); Changed?.Invoke(); }
    }

    public void Stop()
    {
        timer.Stop();
        var recording = capture ?? throw new InvalidOperationException("The microphone is not recording.");
        capture = null;
        started.Stop();
        Elapsed = TimeSpan.FromSeconds(Math.Clamp(started.Elapsed.TotalSeconds, 0, WaveCapture.MaximumSeconds));
        try { recording.Dispose(); Status = "Microphone stopped."; }
        catch (Exception error)
        {
            Error = error.Message;
            Status = "Recording stopped with a capture error. Nothing was transcribed; record again.";
            try { DeleteWave(); }
            finally { Changed?.Invoke(); }
            throw;
        }
        Changed?.Invoke();
    }

    public Task TranscribeAsync(bool cloudConsent = false) => RecognizeAsync(pendingWave
        ?? throw new InvalidOperationException("Record and stop before transcribing."), owned: true, cloudConsent: cloudConsent);

    public async Task StopAndTranscribeAsync()
    {
        Stop();
        await TranscribeAsync(cloudConsent: IsCloud);
    }

    public Task RecognizeKnownWaveAsync(string wave) => RecognizeAsync(wave, owned: false);

    private async Task RecognizeAsync(string wave, bool owned, bool cloudConsent = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsCloud && !cloudConsent) throw new InvalidOperationException("Confirm cloud upload/cost before replaying retained audio.");
        if (Busy || !Configured) throw new InvalidOperationException("Stop recording and configure the selected local speech provider before transcribing.");
        Error = null;
        Transcript = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (lifecycle) { inference = cancellation; operation = completion; }
        Status = IsCloud ? "Finalizing cloud speech. Audio was uploaded; no prompt has been sent to the authoring agent."
            : "Transcribing on this machine with Whisper.net. Nothing has been sent to Copilot.";
        Changed?.Invoke();
        var completed = false;
        try
        {
            if (IsRealtime)
            {
                WaveCapture.ValidateWave(wave, IsCloud ? CloudSpeechInput.SampleRate : 16000);
                if (streaming is { } active) Transcript = await active.FinishAsync(cancellation.Token).ConfigureAwait(false);
                else
                {
                    using var pcm = File.OpenRead(wave); pcm.Position = 44;
                    Transcript = IsCloud ? await RealtimeDictation.ReplayCloudAsync(realtime!, cloudModel!, pcm,
                        text => { if (!disposed) { ProvisionalText = text; Changed?.Invoke(); } }, cancellation.Token).ConfigureAwait(false)
                        : await RealtimeSpeechInput.RecognizePcmAsync(realtime!, pcm,
                        text => { if (!disposed) { ProvisionalText = text; Changed?.Invoke(); } }, cancellation.Token).ConfigureAwait(false);
                }
            }
            else Transcript = await (recognize is null ? whisper!.RecognizeAsync(wave, cancellation.Token)
                : recognize(wave, cancellation.Token)).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(Transcript) || Transcript.Length > 8000)
                throw new InvalidDataException("Speech input returned no usable speech or an overlong utterance.");
            TranscriptVersion++;
            AwaitingInsertion = owned;
            ProvisionalText = null;
            completed = true;
            Status = "Text ready for your editable prompt. Review before Send; nothing is sent automatically.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Transcript = null; Error = IsCloud ? "Cloud transcription cancelled or timed out. Uploaded audio cannot be recalled; no prompt was sent."
            : "Local transcription cancelled or timed out. Retry or discard the recording; nothing was sent."; Status = "Recording kept privately for retry.";
        }
        catch (Exception error)
        {
            Transcript = null; Error = error.Message; Status = IsCloud
            ? "Cloud transcription failed. Recording kept privately for an explicit upload retry or discard. No prompt was sent."
            : "Local transcription failed. Recording kept privately; retry or discard. Nothing was sent."; throw;
        }
        finally
        {
            try
            {
                if (streaming is { } active) { streaming = null; await active.DisposeAsync().ConfigureAwait(false); }
                if (owned && (completed || disposed)) DeleteWave();
            }
            finally
            {
                lock (lifecycle) inference = null;
                completion.TrySetResult();
                Changed?.Invoke();
            }
        }
    }

    public void Cancel() { lock (lifecycle) { opening?.Cancel(); inference?.Cancel(); streaming?.Cancel(); } }

    public void MarkInserted(long version)
    {
        if (version != TranscriptVersion || Transcript is null)
            throw new InvalidOperationException("The dictation changed before it could be inserted.");
        AwaitingInsertion = false;
        Changed?.Invoke();
    }

    public void Discard()
    {
        if (Transcribing) throw new InvalidOperationException("Cancel transcription and wait for it to stop before discarding.");
        if (Recording) Stop();
        if (streaming is { } active) { streaming = null; closing = CloseStream(active); }
        DeleteWave();
        Transcript = null;
        ProvisionalText = null;
        AwaitingInsertion = false;
        Error = null;
        InsertionTarget = null;
        Status = "Idle. Microphone is not recording.";
        Changed?.Invoke();
    }
    private async Task CloseStream(RealtimeDictation active)
    {
        try { await active.DisposeAsync().ConfigureAwait(false); }
        finally { Changed?.Invoke(); }
    }
    public async Task DiscardAsync() { Discard(); if (closing is { } completion) await completion; }
    internal void UseVerificationRealtime(IRealtimeClient client, Func<string, Action<ReadOnlyMemory<byte>>, IVoiceCapture> factory,
        bool cloud = false)
    {
        if (InputPending) throw new InvalidOperationException("Finish the current speech operation before installing a fixture.");
        if (!cloud) throw new InvalidOperationException("Only cloud realtime speech fixtures are supported.");
        ConfigureCloud(client, "gpt-live-transcribe", "Explicit deterministic cloud protocol fixture; no network");
        realtimeCaptureFactory = factory; deviceCount = () => 1;
        Changed?.Invoke();
    }
    internal void UseGeneratedCapture(Func<string, Action<ReadOnlyMemory<byte>>, IVoiceCapture> factory)
    {
        if (InputPending || !IsCloud || !Configured)
            throw new InvalidOperationException("Generated-audio acceptance requires idle, configured real cloud speech.");
        realtimeCaptureFactory = factory;
        deviceCount = () => 1;
        Changed?.Invoke();
    }

    private void DeleteWave()
    {
        if (pendingWave is not null) File.Delete(pendingWave);
        pendingWave = null;
        if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        Cancel();
        if (Recording) Stop();
        if (streaming is { } active && !Transcribing) { streaming = null; closing = CloseStream(active); }
        if (!Transcribing) DeleteWave();
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (operation is { } active) await active.Task.ConfigureAwait(false);
        if (closing is { } completion) await completion.ConfigureAwait(false);
        realtime?.Dispose();
    }
}
#pragma warning restore MEAI001
