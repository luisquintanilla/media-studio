using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MediaStudio.Configuration;

public sealed record ModelAssetProgress(string AssetId, string File, long BytesDone, long BytesTotal)
{
    public double Fraction => BytesTotal <= 0 ? 0 : Math.Clamp((double)BytesDone / BytesTotal, 0, 1);
}

public enum ModelAssetState { Missing, Partial, Present, Verified, Corrupt }

public sealed record ModelAssetStatus(ModelAsset Asset, ModelAssetState State, string Directory, string? Detail = null);

/// <summary>
/// Downloads and verifies pinned model files under <c>%LOCALAPPDATA%\MediaStudio\models</c>. Every file is fetched
/// to a <c>.partial</c> sibling (resumable via HTTP Range), checked for size and SHA-256, then atomically renamed.
/// A per-asset <c>.verified</c> marker caches the size+timestamp of verified files so later checks are cheap.
/// </summary>
public sealed class ModelAssets
{
    private const string PartialSuffix = ".partial";
    private readonly HttpClient _http;

    public ModelAssets(ModelManifest manifest, string? root = null, HttpClient? httpClient = null)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Root = Path.GetFullPath(root ?? MediaStudioPaths.ModelsRoot);
        _http = httpClient ?? CreateDefaultClient();
    }

    public ModelManifest Manifest { get; }
    public string Root { get; }

    public string GetDirectory(string id) => DirectoryFor(Manifest.GetAsset(id));

    /// <summary>Asset directory when every file is present and verified (cached marker or full hash); otherwise null.</summary>
    public string? GetPath(string id)
    {
        var asset = Manifest.GetAsset(id);
        return Status(asset, hash: false).State == ModelAssetState.Verified ? DirectoryFor(asset) : null;
    }

    /// <summary>Like <see cref="GetPath"/> but throws an actionable error naming the command that fixes it.</summary>
    public string RequirePath(string id) =>
        GetPath(id) ?? throw new InvalidOperationException(
            $"Model asset '{id}' is not installed or failed verification. Run: video-production assets ensure --id {id} (or use the Setup wizard).");

    public IReadOnlyList<ModelAssetStatus> List() => Manifest.Assets.Select(a => Status(a, hash: false)).ToArray();

    /// <summary>Re-hashes every file of the asset (ignores the marker cache) and refreshes the marker.</summary>
    public Task<ModelAssetStatus> VerifyAsync(string id, CancellationToken cancellationToken = default) =>
        Task.Run(() => Status(Manifest.GetAsset(id), hash: true, cancellationToken), cancellationToken);

    /// <summary>Plan signature: reports the overall fraction (0..1) for the asset.</summary>
    public Task<string> EnsureAsync(string id, IProgress<double>? progress, CancellationToken cancellationToken = default) =>
        EnsureAsync(id, progress is null ? null : new SynchronousProgress<ModelAssetProgress>(p => progress.Report(p.Fraction)), cancellationToken);

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Downloads missing files and verifies all of them; returns the asset directory.</summary>
    /// <exception cref="InvalidDataException">A file's size or SHA-256 does not match the manifest.</exception>
    public async Task<string> EnsureAsync(string id, IProgress<ModelAssetProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var asset = Manifest.GetAsset(id);
        var directory = DirectoryFor(asset);
        if (Status(asset, hash: false).State == ModelAssetState.Verified)
        {
            progress?.Report(new(asset.Id, "", asset.TotalBytes, asset.TotalBytes));
            return directory;
        }
        Directory.CreateDirectory(directory);
        long completedBefore = 0;
        foreach (var file in asset.Files)
        {
            var target = FilePath(directory, file);
            if (File.Exists(target) && new FileInfo(target).Length == file.Size &&
                await HashMatchesAsync(target, file.Sha256, cancellationToken).ConfigureAwait(false))
            {
                completedBefore += file.Size;
                progress?.Report(new(asset.Id, file.Path, completedBefore, asset.TotalBytes));
                continue;
            }
            var offset = completedBefore;
            await DownloadFileAsync(asset, file, target,
                progress is null ? null : new SynchronousProgress<long>(done => progress.Report(new(asset.Id, file.Path, offset + done, asset.TotalBytes))),
                cancellationToken).ConfigureAwait(false);
            completedBefore += file.Size;
            progress?.Report(new(asset.Id, file.Path, completedBefore, asset.TotalBytes));
        }
        WriteMarker(asset, directory);
        return directory;
    }

    private async Task DownloadFileAsync(ModelAsset asset, ModelAssetFile file, string target, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + PartialSuffix;
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing > file.Size) { File.Delete(partial); existing = 0; }

        if (existing < file.Size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // The partial no longer matches the remote file; restart without a Range header.
                File.Delete(partial);
                await DownloadFileAsync(asset, file, target, progress, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Downloading '{file.Path}' for asset '{asset.Id}' failed with HTTP {(int)response.StatusCode} from {file.Url}.", null, response.StatusCode);
            var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append) existing = 0;
            await using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                var buffer = new byte[1 << 16];
                var done = existing;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > file.Size) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(done);
                }
            }
        }

        var length = new FileInfo(partial).Length;
        if (length != file.Size)
        {
            File.Delete(partial);
            throw new InvalidDataException($"Asset '{asset.Id}' file '{file.Path}' has {length} bytes; the manifest expects {file.Size}. The download was discarded.");
        }
        if (!await HashMatchesAsync(partial, file.Sha256, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(partial);
            throw new InvalidDataException($"Asset '{asset.Id}' file '{file.Path}' failed SHA-256 verification (expected {file.Sha256}). The download was discarded.");
        }
        File.Move(partial, target, overwrite: true);
    }

    private ModelAssetStatus Status(ModelAsset asset, bool hash, CancellationToken cancellationToken = default)
    {
        var directory = DirectoryFor(asset);
        if (!hash && MarkerIsCurrent(asset, directory)) return new(asset, ModelAssetState.Verified, directory);
        var missing = 0;
        var partial = false;
        foreach (var file in asset.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = FilePath(directory, file);
            if (!File.Exists(path))
            {
                missing++;
                partial |= File.Exists(path + PartialSuffix);
                continue;
            }
            var length = new FileInfo(path).Length;
            if (length != file.Size)
                return new(asset, ModelAssetState.Corrupt, directory, $"{file.Path} has {length} bytes, expected {file.Size}.");
            if (hash && !HashMatches(path, file.Sha256))
                return new(asset, ModelAssetState.Corrupt, directory, $"{file.Path} failed SHA-256 verification.");
        }
        if (missing == asset.Files.Length) return new(asset, partial ? ModelAssetState.Partial : ModelAssetState.Missing, directory);
        if (missing > 0) return new(asset, ModelAssetState.Partial, directory, $"{missing} of {asset.Files.Length} files missing.");
        if (hash)
        {
            WriteMarker(asset, directory);
            return new(asset, ModelAssetState.Verified, directory);
        }
        return new(asset, ModelAssetState.Present, directory, "Files present but not yet verified. Run: video-production assets verify");
    }

    private string DirectoryFor(ModelAsset asset)
    {
        var directory = Path.GetFullPath(Path.Combine(Root, asset.Dir.Replace('/', Path.DirectorySeparatorChar)));
        if (!directory.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Asset '{asset.Id}' directory escapes the models root.");
        return directory;
    }

    private static string FilePath(string directory, ModelAssetFile file) =>
        Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar));

    private static string MarkerPath(ModelAsset asset, string directory) => Path.Combine(directory, $".{asset.Id}.verified");

    private sealed record MarkerEntry(string Path, long Size, long LastWriteUtcTicks, string Sha256);

    private static bool MarkerIsCurrent(ModelAsset asset, string directory)
    {
        var marker = MarkerPath(asset, directory);
        if (!File.Exists(marker)) return false;
        try
        {
            var entries = JsonSerializer.Deserialize<MarkerEntry[]>(File.ReadAllText(marker));
            if (entries is null || entries.Length != asset.Files.Length) return false;
            foreach (var file in asset.Files)
            {
                var entry = entries.FirstOrDefault(e => e.Path == file.Path);
                var path = FilePath(directory, file);
                if (entry is null || !File.Exists(path) || !string.Equals(entry.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
                var info = new FileInfo(path);
                if (info.Length != file.Size || entry.Size != file.Size || info.LastWriteTimeUtc.Ticks != entry.LastWriteUtcTicks) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteMarker(ModelAsset asset, string directory)
    {
        var entries = asset.Files.Select(file =>
        {
            var info = new FileInfo(FilePath(directory, file));
            return new MarkerEntry(file.Path, info.Length, info.LastWriteTimeUtc.Ticks, file.Sha256);
        }).ToArray();
        File.WriteAllText(MarkerPath(asset, directory), JsonSerializer.Serialize(entries));
    }

    private static bool HashMatches(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> HashMatchesAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(hash), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MediaStudio-ModelAssets/1.0");
        return client;
    }
}
