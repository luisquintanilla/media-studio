using MediaStudio.Configuration;
using Microsoft.Extensions.AI;

namespace VideoProduction.Providers.Illustrations;

/// <summary>A stored SVG that passed validation and critique, and its similarity to the query brief.</summary>
public sealed record SvgExemplar(string Sha256, string Brief, string Palette, float Similarity, string Svg);

/// <summary>
/// Library of illustrations that passed validation <b>and</b> critique, embedded by brief: <c>{sha}.svg</c> files plus
/// <c>index.bin</c> under <see cref="DefaultDirectory"/>. New scenes retrieve the nearest exemplars of the same palette as
/// few-shot style references, and a near-identical brief (cosine ≥ <see cref="ReuseThreshold"/>, same palette) reuses the
/// cached SVG outright. Capped at <see cref="DefaultCapacity"/> entries, evicting the least recently used.
/// </summary>
public sealed class SvgExemplarLibrary
{
    public const float ReuseThreshold = 0.97f;
    public const int DefaultCapacity = 2000;
    public const int DefaultNearest = 2;
    private readonly IEmbeddingGenerator<string, Embedding<float>> embeddings;
    private readonly string directory;
    private readonly int capacity;
    private readonly Func<DateTimeOffset> clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private VectorIndex<Entry>? index;
    private readonly string modelId;

    private sealed record Entry(string Sha256, string Brief, string Palette, long LastUsedTicks);

    public SvgExemplarLibrary(IEmbeddingGenerator<string, Embedding<float>> embeddings, string modelId,
        string? directory = null, int capacity = DefaultCapacity, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.embeddings = embeddings;
        this.modelId = modelId;
        this.directory = Path.GetFullPath(directory ?? DefaultDirectory);
        this.capacity = capacity;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public static string DefaultDirectory => Path.Combine(MediaStudioPaths.Root, "svg-library");
    public string Directory => directory;
    private string IndexPath => Path.Combine(directory, "index.bin");

    public int Count { get { gate.Wait(); try { return Index().Count; } finally { gate.Release(); } } }

    public async Task<ReadOnlyMemory<float>> EmbedAsync(string brief, CancellationToken cancellationToken = default) =>
        await embeddings.GenerateVectorAsync(brief, cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>Stores an SVG that passed validation and critique. <paramref name="vector"/> skips re-embedding the brief.</summary>
    public async Task AddAsync(string brief, string palette, string svg, ReadOnlyMemory<float>? vector = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brief);
        ArgumentException.ThrowIfNullOrWhiteSpace(palette);
        ArgumentException.ThrowIfNullOrWhiteSpace(svg);
        var embedding = vector ?? await EmbedAsync(brief, cancellationToken).ConfigureAwait(false);
        var sha = Files.HashText(svg);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Index();
            System.IO.Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, sha + ".svg"), svg, cancellationToken).ConfigureAwait(false);
            current.RemoveWhere(e => e.Sha256 == sha);
            current.Add(embedding, new Entry(sha, brief, palette, clock().UtcTicks));
            if (current.Count > capacity)
            {
                var evict = current.Items.OrderBy(e => e.LastUsedTicks).Take(current.Count - capacity).Select(e => e.Sha256)
                    .ToHashSet(StringComparer.Ordinal);
                current.RemoveWhere(e => evict.Contains(e.Sha256));
                foreach (var stale in evict) File.Delete(Path.Combine(directory, stale + ".svg"));
            }
            current.Save(IndexPath);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<SvgExemplar>> NearestAsync(string brief, string palette, int k = DefaultNearest,
        CancellationToken cancellationToken = default) =>
        await NearestAsync(await EmbedAsync(brief, cancellationToken).ConfigureAwait(false), palette, k, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The <paramref name="k"/> nearest exemplars with the same palette, most similar first.</summary>
    public async Task<IReadOnlyList<SvgExemplar>> NearestAsync(ReadOnlyMemory<float> query, string palette, int k = DefaultNearest,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var matches = Index().Search(query, k, e => e.Palette == palette);
            return await ReadAsync(matches, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<SvgExemplar?> TryReuseAsync(string brief, string palette, CancellationToken cancellationToken = default) =>
        await TryReuseAsync(await EmbedAsync(brief, cancellationToken).ConfigureAwait(false), palette, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The cached SVG for a near-identical brief (similarity ≥ <see cref="ReuseThreshold"/>, same palette), or null.</summary>
    public async Task<SvgExemplar?> TryReuseAsync(ReadOnlyMemory<float> query, string palette, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var best = Index().Search(query, 1, e => e.Palette == palette);
            if (best.Count == 0 || best[0].Score < ReuseThreshold) return null;
            var found = await ReadAsync(best, cancellationToken).ConfigureAwait(false);
            if (found.Count == 0) return null;
            var sha = found[0].Sha256;
            var now = clock().UtcTicks;
            Index().Update(e => e.Sha256 == sha ? e with { LastUsedTicks = now } : e);
            Index().Save(IndexPath);
            return found[0];
        }
        finally { gate.Release(); }
    }

    private VectorIndex<Entry> Index() => index ??= VectorIndex<Entry>.Load(IndexPath, modelId);

    private async Task<IReadOnlyList<SvgExemplar>> ReadAsync(IReadOnlyList<VectorMatch<Entry>> matches, CancellationToken cancellationToken)
    {
        var result = new List<SvgExemplar>(matches.Count);
        foreach (var match in matches)
        {
            var file = Path.Combine(directory, match.Item.Sha256 + ".svg");
            if (!File.Exists(file)) continue;
            var svg = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            if (Files.HashText(svg) != match.Item.Sha256) continue;
            result.Add(new(match.Item.Sha256, match.Item.Brief, match.Item.Palette, match.Score, svg));
        }
        return result;
    }
}
