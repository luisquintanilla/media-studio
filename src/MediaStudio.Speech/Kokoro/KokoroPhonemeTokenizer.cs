using System.Buffers;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace MediaStudio.Speech.Kokoro;

/// <summary>
/// Character-level Kokoro tokenizer over the IPA vocabulary in the model's <c>tokenizer.json</c> (<c>model.vocab</c>).
/// Symbols outside the vocabulary are dropped and counted in <see cref="LastUnknownCount"/>.
/// </summary>
public sealed class KokoroPhonemeTokenizer : Tokenizer
{
    /// <summary>The model accepts at most 512 ids including the pad at each end.</summary>
    public const int MaxTokens = 510;
    public const int PadId = 0;

    private readonly Dictionary<char, int> vocab;
    private readonly Dictionary<int, char> reverse;

    public KokoroPhonemeTokenizer(IReadOnlyDictionary<char, int> vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        if (vocabulary.Count == 0) throw new ArgumentException("The Kokoro vocabulary is empty.", nameof(vocabulary));
        vocab = new Dictionary<char, int>(vocabulary);
        reverse = vocab.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key);
    }

    public IReadOnlyDictionary<char, int> Vocabulary => vocab;

    /// <summary>Unknown symbols dropped by the most recent encode call on this instance.</summary>
    public int LastUnknownCount { get; private set; }

    public static KokoroPhonemeTokenizer FromFile(string tokenizerJsonPath)
    {
        using var stream = File.OpenRead(tokenizerJsonPath);
        return FromJson(stream);
    }

    /// <summary>Reads <c>model.vocab</c> (a map of single characters to ids) from a Hugging Face <c>tokenizer.json</c>.</summary>
    public static KokoroPhonemeTokenizer FromJson(Stream tokenizerJson)
    {
        using var document = JsonDocument.Parse(tokenizerJson);
        if (!document.RootElement.TryGetProperty("model", out var model) || !model.TryGetProperty("vocab", out var vocabElement))
            throw new InvalidDataException("tokenizer.json has no model.vocab map.");
        var vocabulary = new Dictionary<char, int>();
        foreach (var entry in vocabElement.EnumerateObject())
            if (entry.Name.Length == 1) vocabulary[entry.Name[0]] = entry.Value.GetInt32();
        return new KokoroPhonemeTokenizer(vocabulary);
    }

    /// <summary>Encodes phonemes and wraps them as <c>[0, ids…, 0]</c> for the model.</summary>
    public long[] EncodeForModel(string phonemes)
    {
        var ids = EncodeToIds(phonemes);
        if (ids.Count > MaxTokens) throw new ArgumentException($"Kokoro accepts at most {MaxTokens} tokens per chunk; got {ids.Count}. Chunk the text first.");
        var input = new long[ids.Count + 2];
        for (var i = 0; i < ids.Count; i++) input[i + 1] = ids[i];
        return input;
    }

    /// <summary>
    /// Splits an over-long token sequence into pieces of at most <paramref name="max"/> ids, preferring to break after
    /// clause punctuation, then after a space.
    /// </summary>
    public IReadOnlyList<int[]> Chunk(IReadOnlyList<int> ids, int max = MaxTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        var chunks = new List<int[]>();
        var start = 0;
        var strong = new HashSet<int>(".,;:!?".Where(vocab.ContainsKey).Select(c => vocab[c]));
        var space = vocab.TryGetValue(' ', out var spaceId) ? spaceId : -1;
        while (ids.Count - start > max)
        {
            var end = start + max;
            var cut = LastIndex(ids, start, end, strong.Contains);
            if (cut < 0) cut = LastIndex(ids, start, end, id => id == space);
            var stop = cut < 0 ? end : cut + 1;
            chunks.Add(Slice(ids, start, stop));
            start = stop;
            while (start < ids.Count && ids[start] == space) start++;
        }
        if (start < ids.Count) chunks.Add(Slice(ids, start, ids.Count));
        return chunks;
    }

    protected override EncodeResults<EncodedToken> EncodeToTokens(string? text, ReadOnlySpan<char> textSpan, EncodeSettings settings)
    {
        var source = text is null ? textSpan : text.AsSpan();
        var tokens = new List<EncodedToken>(source.Length);
        var unknown = 0;
        var consumed = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (settings.MaxTokenCount > 0 && tokens.Count >= settings.MaxTokenCount) break;
            consumed = i + 1;
            if (vocab.TryGetValue(source[i], out var id)) tokens.Add(new EncodedToken(id, source[i].ToString(), new Range(i, i + 1)));
            else unknown++;
        }
        LastUnknownCount = unknown;
        return new EncodeResults<EncodedToken> { Tokens = tokens, CharsConsumed = consumed, NormalizedText = null };
    }

    public override OperationStatus Decode(IEnumerable<int> ids, Span<char> destination, out int idsConsumed, out int charsWritten)
    {
        idsConsumed = 0;
        charsWritten = 0;
        foreach (var id in ids)
        {
            if (!reverse.TryGetValue(id, out var c) || id == PadId && c == '$') { idsConsumed++; continue; }
            if (charsWritten >= destination.Length) return OperationStatus.DestinationTooSmall;
            destination[charsWritten++] = c;
            idsConsumed++;
        }
        return OperationStatus.Done;
    }

    private static int LastIndex(IReadOnlyList<int> ids, int start, int end, Func<int, bool> match)
    {
        for (var i = end - 1; i > start; i--) if (match(ids[i])) return i;
        return -1;
    }

    private static int[] Slice(IReadOnlyList<int> ids, int start, int stop)
    {
        var slice = new int[stop - start];
        for (var i = 0; i < slice.Length; i++) slice[i] = ids[start + i];
        return slice;
    }
}
