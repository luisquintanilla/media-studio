using Microsoft.Extensions.AI;

namespace VideoProduction.Providers.Illustrations;

/// <summary>One retrievable passage of a project source (a document chunk, an outline item or a brief).</summary>
public sealed record SourcePassage(string SourceId, int Index, string Text);

/// <summary>
/// Per-project grounding index: source passages embedded with the configured <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>
/// (batches of 32) and stored as <c>&lt;project&gt;/.mediastudio/source.embeddings.bin</c>. A different embedding model id
/// yields an empty index on load, so passages are re-embedded rather than mixed.
/// </summary>
public sealed class SourceIndex
{
    public const int BatchSize = 32;
    public const int DefaultTopK = 4;
    public const string RelativePath = ".mediastudio/source.embeddings.bin";
    private readonly IEmbeddingGenerator<string, Embedding<float>> embeddings;
    private readonly SemaphoreSlim gate = new(1, 1);
    private VectorIndex<SourcePassage> index;

    public SourceIndex(IEmbeddingGenerator<string, Embedding<float>> embeddings, string modelId)
        : this(embeddings, new VectorIndex<SourcePassage>(modelId)) { }

    private SourceIndex(IEmbeddingGenerator<string, Embedding<float>> embeddings, VectorIndex<SourcePassage> index)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        this.embeddings = embeddings;
        this.index = index;
    }

    public string ModelId => index.ModelId;
    public int Count => index.Count;

    public static string PathFor(string projectDirectory) =>
        Path.Combine(Path.GetFullPath(projectDirectory), ".mediastudio", "source.embeddings.bin");

    /// <summary>Loads the project index; empty (needs <see cref="AddAsync"/>) when missing or built by another model.</summary>
    public static SourceIndex Load(string path, IEmbeddingGenerator<string, Embedding<float>> embeddings, string modelId) =>
        new(embeddings, VectorIndex<SourcePassage>.Load(path, modelId));

    public void Save(string path) => index.Save(path);

    /// <summary>Embeds and adds passages, replacing earlier passages of the same sources.</summary>
    public async Task AddAsync(IEnumerable<SourcePassage> passages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passages);
        var items = passages.Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToArray();
        if (items.Length == 0) return;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sources = items.Select(p => p.SourceId).ToHashSet(StringComparer.Ordinal);
            index.RemoveWhere(p => sources.Contains(p.SourceId));
            foreach (var batch in items.Chunk(BatchSize))
            {
                var vectors = await embeddings.GenerateAsync(batch.Select(p => p.Text), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                Guard.Require(vectors.Count == batch.Length, "illustration.embedding", "The embedding model returned a different number of vectors.");
                for (var i = 0; i < batch.Length; i++) index.Add(vectors[i].Vector, batch[i]);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>The <paramref name="k"/> passages most relevant to <paramref name="query"/>.</summary>
    public async Task<IReadOnlyList<VectorMatch<SourcePassage>>> TopKAsync(string query, int k = DefaultTopK,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (index.Count == 0) return [];
        var vector = await embeddings.GenerateVectorAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false);
        return TopK(vector, k);
    }

    public IReadOnlyList<VectorMatch<SourcePassage>> TopK(ReadOnlyMemory<float> query, int k = DefaultTopK)
    {
        gate.Wait();
        try { return index.Search(query, k); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Passages of one source from its extractor's chunks (for example a document projection's chunk texts), in order;
    /// chunks longer than <paramref name="maxCharacters"/> are split with <see cref="Chunk"/>.
    /// </summary>
    public static IEnumerable<SourcePassage> FromChunks(string sourceId, IEnumerable<string?> chunks, int maxCharacters = 800)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(chunks);
        var index = 0;
        foreach (var chunk in chunks)
        {
            if (string.IsNullOrWhiteSpace(chunk)) continue;
            foreach (var passage in Chunk(sourceId, chunk, maxCharacters))
                yield return passage with { Index = index++ };
        }
    }

    /// <summary>
    /// Splits plain text into paragraph-aligned passages of at most <paramref name="maxCharacters"/> characters
    /// (callers with a token-aware document chunker should pass its chunks to <see cref="AddAsync"/> instead).
    /// </summary>
    public static IEnumerable<SourcePassage> Chunk(string sourceId, string text, int maxCharacters = 800)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCharacters, 64);
        var index = 0;
        var current = new System.Text.StringBuilder();
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var remaining = paragraph;
            while (remaining.Length > 0)
            {
                if (current.Length > 0 && current.Length + 1 + remaining.Length > maxCharacters)
                {
                    yield return new(sourceId, index++, current.ToString());
                    current.Clear();
                }
                var take = Math.Min(remaining.Length, maxCharacters - current.Length - (current.Length > 0 ? 1 : 0));
                if (take < remaining.Length)
                {
                    var space = remaining.LastIndexOf(' ', take - 1);
                    if (space > take / 2) take = space;
                }
                if (current.Length > 0) current.Append(' ');
                current.Append(remaining.AsSpan(0, take).Trim());
                remaining = remaining[take..].TrimStart();
            }
        }
        if (current.Length > 0) yield return new(sourceId, index, current.ToString());
    }
}
