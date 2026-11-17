using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VideoProduction;
using VideoProduction.Providers;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace MediaStudio.App;

public sealed record NativeWhisperConfiguration(int SchemaVersion, string ModelPath, string RuntimeDirectory);

public sealed class LocalWhisper
{
    private readonly string modelPath;
    private readonly string libraryPath;
    private readonly ArtifactHash[] dependencies;
    private static readonly SemaphoreSlim Inference = new(1, 1);
    public const string Disclosure = "Whisper.net 1.9.0 / local Whisper Base / CPU. Batch recognition after Stop; no cloud speech or live partial transcript.";
    private static readonly string[] NativeNames =
        ["ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-whisper.dll", "whisper.dll"];
    private static readonly string[] NativeHashes =
        ["49db5887b550feb27f06043b101a833cf8633da5bd8d8922dc898eaccd2d17bb",
         "e520b5d988e63a238e63b2398cf05b7811ad9d64c30d297a6ba0924ab4100097",
         "102c4f62b20e68c512ab638966fef28be2f03d04b2d8f625551c213ae5dc0e92",
         "cbf76b398336c057457692595cdf1cab7011c97dc22216d7de56a5256f3fcb35"];
    private const string ManagedHash = "42112ae16a8c869cc60b008f1d3f343fb52c12d5a2d89bba19f40de057b5ebc6";

    public LocalWhisper(string configurationFile)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(configurationFile));
        if (document.RootElement.TryGetProperty("schemaVersion", out _))
        {
            (modelPath, libraryPath, dependencies) = Native(document.RootElement.Deserialize<NativeWhisperConfiguration>(StoryboardJson.Options)
                ?? throw new InvalidDataException("Local Whisper configuration is missing."));
            return;
        }
        var config = document.RootElement.Deserialize<TranscriberConfig>(StoryboardJson.Options)
            ?? throw new InvalidDataException("Local Whisper configuration is missing.");
        if (config.ModelSha256 != Transcription.ModelSha256 || !Path.IsPathFullyQualified(config.ModelPath)
            || !Path.IsPathFullyQualified(config.Executable)
            || config.Dependencies is null)
            throw new InvalidDataException("Local voice requires the already provisioned pinned Whisper Base model and native dependencies.");
        var root = Path.GetDirectoryName(config.Executable) ?? throw new InvalidDataException("Missing local runtime directory.");
        modelPath = config.ModelPath;
        libraryPath = config.Executable;
        dependencies = config.Dependencies.Select(a => a with { Path = Path.GetFullPath(Path.Combine(root, a.Path.Replace('/', Path.DirectorySeparatorChar))) }).ToArray();
    }

    /// <summary>Uses an existing ggml model with the Whisper.net runtime that ships beside the app.</summary>
    public LocalWhisper(NativeWhisperConfiguration native) => (modelPath, libraryPath, dependencies) = Native(native);

    private static (string Model, string Library, ArtifactHash[] Dependencies) Native(NativeWhisperConfiguration native)
    {
        if (native.SchemaVersion != 1 || string.IsNullOrWhiteSpace(native.ModelPath)
            || string.IsNullOrWhiteSpace(native.RuntimeDirectory) || !Path.IsPathFullyQualified(native.ModelPath)
            || !Path.IsPathFullyQualified(native.RuntimeDirectory))
            throw new InvalidDataException("Local voice requires a supported configuration and absolute model/runtime paths.");
        var library = Path.Combine(native.RuntimeDirectory, "Whisper.net.dll");
        return (native.ModelPath, library, NativeNames.Select((name, index) => new ArtifactHash(
            Path.Combine(native.RuntimeDirectory, "runtimes", "win-x64", name), NativeHashes[index]))
            .Append(new(library, ManagedHash)).ToArray());
    }

    public static string SetUp(string privateRoot, string existingModel)
    {
        var configuration = new NativeWhisperConfiguration(1, PrivateFiles.LocalPath(existingModel), AppContext.BaseDirectory);
        var path = Path.Combine(privateRoot, "local-whisper.json");
        var candidate = Path.Combine(privateRoot, "local-whisper-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(privateRoot);
        PrivateFiles.Protect(privateRoot, directory: true);
        try
        {
            VideoProduction.Json.Write(candidate, configuration);
            PrivateFiles.Protect(candidate);
            new LocalWhisper(candidate).VerifyAssets();
            File.Move(candidate, path, overwrite: true);
            return path;
        }
        finally { File.Delete(candidate); }
    }

    public void VerifyAssets()
    {
        var held = new List<FileStream>();
        try { OpenAssets(held); }
        finally { foreach (var stream in held) stream.Dispose(); }
    }

    private void OpenAssets(List<FileStream> held)
    {
        held.Add(OpenVerified(modelPath, Transcription.ModelSha256));
        var nativeRoot = Path.Combine(Path.GetDirectoryName(libraryPath)!, "runtimes", "win-x64");
        var native = NativeNames.Select(name => dependencies.SingleOrDefault(a =>
            a.Path.Equals(Path.Combine(nativeRoot, name), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("Missing pinned local Whisper dependency: " + name)).ToArray();
        if (Directory.EnumerateFiles(nativeRoot, "*.dll").Any(path =>
            !native.Any(asset => asset.Path.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("The selected CPU runtime directory contains additional unreviewed DLLs.");
        foreach (var asset in native) held.Add(OpenVerified(asset.Path, asset.Sha256));
        var managed = dependencies.Single(a => Path.GetFileName(a.Path) == "Whisper.net.dll");
        held.Add(OpenVerified(typeof(WhisperFactory).Assembly.Location, managed.Sha256));
    }

    public Task<string> RecognizeAsync(string waveFile, CancellationToken cancellationToken) =>
        WithClientAsync(waveFile, AudioTranscriber.MeaiSpeechInput.RecognizeAsync, cancellationToken);

#pragma warning disable MEAI001
    private async Task<T> WithClientAsync<T>(string waveFile,
        Func<ISpeechToTextClient, Stream, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        WaveCapture.ValidateWave(waveFile);
        await Inference.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(async () =>
            {
                var held = new List<FileStream>();
                try
                {
                    OpenAssets(held);
                    cancellationToken.ThrowIfCancellationRequested();
                    // 1.9.0 searches "runtimes" beside LibraryPath, not beside the native DLL.
                    RuntimeOptions.LibraryPath = libraryPath;
                    RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
#pragma warning disable MEAI001
                    using ISpeechToTextClient client = new WhisperSpeechToTextClient(modelPath);
                    await using var audio = File.OpenRead(waveFile);
                    var result = await action(client, audio, cancellationToken);
#pragma warning restore MEAI001
                    cancellationToken.ThrowIfCancellationRequested();
                    return result;
                }
                catch (WhisperProcessingException error) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Local Whisper cancelled.", error, cancellationToken);
                }
                finally { foreach (var stream in held) stream.Dispose(); }
            }, cancellationToken);
        }
        finally { Inference.Release(); }
    }
#pragma warning restore MEAI001

    private static FileStream OpenVerified(string path, string expected)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Whisper asset paths must be absolute.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Local Whisper asset verification failed: " + Path.GetFileName(path));
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
}
