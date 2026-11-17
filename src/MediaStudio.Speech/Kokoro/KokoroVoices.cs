using System.Collections.Concurrent;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;

namespace MediaStudio.Speech.Kokoro;

/// <summary>A Kokoro voice pack: float32 little-endian style vectors shaped <c>[510, 1, 256]</c>.</summary>
public sealed class KokoroVoice
{
    public const int Rows = 510;
    public const int StyleDimensions = 256;

    private readonly float[] data;

    public KokoroVoice(string name, float[] data)
    {
        if (data.Length != Rows * StyleDimensions)
            throw new InvalidDataException($"Voice '{name}' has {data.Length} floats; expected {Rows}×1×{StyleDimensions}.");
        Name = name;
        this.data = data;
        Styles = Tensor.Create(data, [Rows, 1, StyleDimensions]);
    }

    public string Name { get; }

    /// <summary>The style table as a tensor with shape [510, 1, 256].</summary>
    public Tensor<float> Styles { get; }

    /// <summary>Row index used for a phoneme sequence of <paramref name="tokenCount"/> ids (excluding pads): <c>min(n, 509)</c>.</summary>
    public static int RowFor(int tokenCount) => Math.Clamp(tokenCount, 0, Rows - 1);

    /// <summary>The 256-float style vector for <paramref name="tokenCount"/> tokens.</summary>
    public ReadOnlySpan<float> GetStyle(int tokenCount) => data.AsSpan(RowFor(tokenCount) * StyleDimensions, StyleDimensions);

    public static KokoroVoice Load(string name, string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length % sizeof(float) != 0) throw new InvalidDataException($"Voice file '{Path.GetFileName(path)}' is truncated.");
        var floats = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        if (!BitConverter.IsLittleEndian)
            for (var i = 0; i < floats.Length; i++)
                floats[i] = BitConverter.Int32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(BitConverter.SingleToInt32Bits(floats[i])));
        return new KokoroVoice(name, floats);
    }
}

/// <summary>Lazily loads voice packs from <c>voices/&lt;name&gt;.bin</c> (or flattened <c>voices_&lt;name&gt;.bin</c>).</summary>
public sealed class KokoroVoices(string directory)
{
    private readonly ConcurrentDictionary<string, Lazy<KokoroVoice>> cache = new(StringComparer.OrdinalIgnoreCase);

    public string Directory { get; } = directory;

    public KokoroVoice Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
            throw new ArgumentException($"Invalid Kokoro voice name '{name}'.", nameof(name));
        return cache.GetOrAdd(name, n => new Lazy<KokoroVoice>(() => KokoroVoice.Load(n, PathFor(n)))).Value;
    }

    public string? FindFile(string name)
    {
        foreach (var candidate in new[] { Path.Combine(Directory, "voices", name + ".bin"), Path.Combine(Directory, "voices_" + name + ".bin") })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    public IReadOnlyList<string> ListVoices()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var nested = Path.Combine(Directory, "voices");
        if (System.IO.Directory.Exists(nested))
            foreach (var file in System.IO.Directory.EnumerateFiles(nested, "*.bin")) names.Add(Path.GetFileNameWithoutExtension(file));
        if (System.IO.Directory.Exists(Directory))
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "voices_*.bin")) names.Add(Path.GetFileNameWithoutExtension(file)["voices_".Length..]);
        return names.ToArray();
    }

    private string PathFor(string name) => FindFile(name) ?? throw new FileNotFoundException(
        $"Kokoro voice '{name}' is not installed. Available: {string.Join(", ", ListVoices())}.", Path.Combine(Directory, "voices", name + ".bin"));
}
