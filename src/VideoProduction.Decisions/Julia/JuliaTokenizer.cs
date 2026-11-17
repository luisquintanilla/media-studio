using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;
using HfTokenizer = Tokenizers.HuggingFace.Tokenizer.Tokenizer;

namespace VideoProduction.Decisions.Experimental.Julia;

/// <summary>
/// Exposes Julia's pinned HuggingFace tokenizer through the Microsoft tokenizer abstraction.
/// HF supplies IDs, tokens, offsets and decoding; scalar offsets are converted to UTF-16.
/// Full configured encoding is lossless, without automatic BOS/EOS insertion.
/// </summary>
public sealed class JuliaTokenizer : Tokenizer, IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HfTokenizer _inner;
    private readonly object _gate = new();
    private bool _disposed;
    internal string[] ReservedTokens { get; }
    internal long[] ReservedIds { get; }

    public JuliaTokenizer(string tokenizerJsonPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(tokenizerJsonPath));
        var root = document.RootElement;
        var model = root.GetProperty("model");
        var pre = root.GetProperty("pre_tokenizer");
        var normalizer = root.GetProperty("normalizer");
        if (root.GetProperty("truncation").ValueKind != JsonValueKind.Null ||
            root.GetProperty("padding").ValueKind != JsonValueKind.Null ||
            model.GetProperty("type").GetString() != "BPE" || !model.GetProperty("byte_fallback").GetBoolean() ||
            !model.GetProperty("fuse_unk").GetBoolean() || model.GetProperty("ignore_merges").GetBoolean() ||
            model.GetProperty("unk_token").GetString() != "<unk>" ||
            model.GetProperty("dropout").ValueKind != JsonValueKind.Null ||
            model.GetProperty("continuing_subword_prefix").ValueKind != JsonValueKind.Null ||
            model.GetProperty("end_of_word_suffix").ValueKind != JsonValueKind.Null ||
            normalizer.GetProperty("type").GetString() != "Replace" ||
            normalizer.GetProperty("pattern").GetProperty("String").GetString() != " " ||
            normalizer.GetProperty("content").GetString() != "\u2581" ||
            pre.GetProperty("type").GetString() != "Metaspace" ||
            pre.GetProperty("replacement").GetString() != "\u2581" ||
            pre.GetProperty("prepend_scheme").GetString() != "always" || !pre.GetProperty("split").GetBoolean())
            throw new NotSupportedException("Expected Julia's lossless Replace/Metaspace/ByteFallback BPE configuration.");

        var vocabulary = model.GetProperty("vocab").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);
        var added = root.GetProperty("added_tokens").EnumerateArray().ToArray();
        string[] framing = ["<pad>", "<eos>", "<bos>", "<unk>", "<mask>"];
        for (int i = 0; i < framing.Length; i++)
        {
            var token = added.Single(t => t.GetProperty("content").GetString() == framing[i]);
            if (!token.GetProperty("special").GetBoolean() || token.GetProperty("id").GetInt32() != i ||
                vocabulary[framing[i]] != i)
                throw new NotSupportedException("Julia framing token metadata disagrees.");
        }
        var special = added.Where(t => t.GetProperty("special").GetBoolean()).ToArray();
        ReservedTokens = special.Select(t => t.GetProperty("content").GetString()!).ToArray();
        ReservedIds = special.Select(t => t.GetProperty("id").GetInt64()).ToArray();
        _inner = HfTokenizer.FromFile(tokenizerJsonPath);
    }

    protected override EncodeResults<EncodedToken> EncodeToTokens(string? text, ReadOnlySpan<char> textSpan, EncodeSettings settings)
    {
        string input = text ?? textSpan.ToString();
        var encoding = Encode(input, settings);
        int[] positions = Utf16Positions(input);
        var tokens = encoding.Ids.Select((id, i) => new EncodedToken(checked((int)id), encoding.Tokens[i],
            positions[checked((int)encoding.Offsets[i].Start)]..positions[checked((int)encoding.Offsets[i].End)])).ToArray();
        return new() { Tokens = tokens, NormalizedText = null, CharsConsumed = input.Length };
    }

    private Tokenizers.HuggingFace.Tokenizer.Encoding Encode(string input, EncodeSettings settings)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (settings.MaxTokenCount != int.MaxValue ||
                !settings.ConsiderNormalization || !settings.ConsiderPreTokenization)
                throw new NotSupportedException("Julia requires complete encoding with its configured HF normalization and pretokenization.");
            StrictUtf8.GetByteCount(input);
            return _inner.Encode(input, addSpecialTokens: false, includeTokens: true,
                includeOffsets: true, charOffsets: true).Single();
        }
    }

    private static int[] Utf16Positions(string input)
    {
        List<int> positions = [0];
        int position = 0;
        foreach (var rune in input.EnumerateRunes())
        {
            position += rune.Utf16SequenceLength;
            positions.Add(position);
        }
        return positions.ToArray();
    }

    protected override EncodeResults<int> EncodeToIds(string? text, ReadOnlySpan<char> textSpan, EncodeSettings settings)
    {
        string input = text ?? textSpan.ToString();
        return new() {
            Tokens = Encode(input, settings).Ids.Select(id => checked((int)id)).ToArray(),
            NormalizedText = null, CharsConsumed = input.Length
        };
    }

    protected override int CountTokens(string? text, ReadOnlySpan<char> textSpan, EncodeSettings settings) =>
        Encode(text ?? textSpan.ToString(), settings).Ids.Count;

    protected override int GetIndexByTokenCount(string? text, ReadOnlySpan<char> textSpan, EncodeSettings settings,
        bool fromEnd, out string? normalizedText, out int tokenCount) =>
        throw new NotSupportedException("Julia token-index queries are not supported; encode the complete decision input.");

    public override string Decode(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _inner.Decode(ids.Select(id => checked((uint)id)), skipSpecialTokens: false);
        }
    }

    public override OperationStatus Decode(IEnumerable<int> ids, Span<char> destination, out int idsConsumed, out int charsWritten)
    {
        ArgumentNullException.ThrowIfNull(ids);
        int[] tokens = ids.ToArray();
        string decoded = Decode(tokens);
        idsConsumed = charsWritten = 0;
        if (!decoded.AsSpan().TryCopyTo(destination)) return OperationStatus.DestinationTooSmall;
        idsConsumed = tokens.Length;
        charsWritten = decoded.Length;
        return OperationStatus.Done;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _inner.Dispose();
        }
    }
}
