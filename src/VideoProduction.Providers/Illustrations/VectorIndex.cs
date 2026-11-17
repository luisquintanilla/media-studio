using System.Numerics.Tensors;
using System.Text;
using System.Text.Json;

namespace VideoProduction.Providers.Illustrations;

public readonly record struct VectorMatch<T>(T Item, float Score);

/// <summary>
/// A small in-memory vector store: (vector, item) pairs searched with <see cref="TensorPrimitives.CosineSimilarity(ReadOnlySpan{float}, ReadOnlySpan{float})"/>.
/// The binary file records the embedding model id and dimensions; <see cref="Load"/> returns an empty index when either
/// differs, so vectors from different models are never mixed (callers re-embed).
/// </summary>
public sealed class VectorIndex<T>
{
    private const uint Magic = 0x4958564D; // "MVXI"
    private const int Version = 1;
    private readonly List<(float[] Vector, T Item)> entries = [];

    /// <param name="modelId">Embedding model that produced every vector.</param>
    /// <param name="dimensions">Vector length; 0 adopts the length of the first added vector.</param>
    public VectorIndex(string modelId, int dimensions = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfNegative(dimensions);
        ModelId = modelId;
        Dimensions = dimensions;
    }

    public string ModelId { get; }
    public int Dimensions { get; private set; }
    public int Count => entries.Count;
    public IEnumerable<T> Items => entries.Select(e => e.Item);

    public void Add(ReadOnlyMemory<float> vector, T item)
    {
        if (Dimensions == 0) Dimensions = vector.Length;
        if (vector.Length != Dimensions || vector.Length == 0)
            throw new ArgumentException($"Expected a {Dimensions}-dimensional vector, got {vector.Length}.", nameof(vector));
        entries.Add((vector.ToArray(), item));
    }

    public int RemoveWhere(Func<T, bool> predicate) => entries.RemoveAll(e => predicate(e.Item));

    /// <summary>Replaces every item with <paramref name="map"/>(item), keeping its vector.</summary>
    public void Update(Func<T, T> map)
    {
        for (var i = 0; i < entries.Count; i++) entries[i] = (entries[i].Vector, map(entries[i].Item));
    }

    /// <summary>The <paramref name="k"/> most similar items, highest cosine similarity first (ties keep insertion order).</summary>
    public IReadOnlyList<VectorMatch<T>> Search(ReadOnlyMemory<float> query, int k, Func<T, bool>? filter = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        if (k == 0 || entries.Count == 0 || query.Length != Dimensions) return [];
        return entries
            .Select((e, i) => (e.Item, Score: TensorPrimitives.CosineSimilarity(query.Span, e.Vector), Index: i))
            .Where(e => filter is null || filter(e.Item))
            .OrderByDescending(e => e.Score).ThenBy(e => e.Index)
            .Take(k).Select(e => new VectorMatch<T>(e.Item, e.Score)).ToArray();
    }

    /// <summary>Writes the index atomically (temporary file, then replace).</summary>
    public void Save(string path, JsonSerializerOptions? itemOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(ModelId);
                writer.Write(Dimensions);
                writer.Write(entries.Count);
                foreach (var (vector, item) in entries)
                {
                    foreach (var value in vector) writer.Write(value);
                    writer.Write(JsonSerializer.Serialize(item, itemOptions));
                }
            }
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>
    /// Loads an index, or returns an empty one when the file is missing, unreadable, from another format version,
    /// or was built with a different <paramref name="modelId"/> or <paramref name="dimensions"/> (0 accepts any).
    /// </summary>
    public static VectorIndex<T> Load(string path, string modelId, int dimensions = 0, JsonSerializerOptions? itemOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var empty = new VectorIndex<T>(modelId, dimensions);
        if (!File.Exists(path)) return empty;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version) return empty;
            var storedModel = reader.ReadString();
            var storedDimensions = reader.ReadInt32();
            if (!string.Equals(storedModel, modelId, StringComparison.Ordinal)
                || dimensions != 0 && storedDimensions != dimensions || storedDimensions is < 0 or > 65_536) return empty;
            var count = reader.ReadInt32();
            if (count < 0 || (long)count * storedDimensions * sizeof(float) > stream.Length) return empty;
            var index = new VectorIndex<T>(modelId, storedDimensions);
            for (var i = 0; i < count; i++)
            {
                var vector = new float[storedDimensions];
                for (var d = 0; d < storedDimensions; d++) vector[d] = reader.ReadSingle();
                var item = JsonSerializer.Deserialize<T>(reader.ReadString(), itemOptions);
                if (item is null) return empty;
                index.entries.Add((vector, item));
            }
            return index;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or JsonException or UnauthorizedAccessException) { return empty; }
    }
}
