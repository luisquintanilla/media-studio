using System.Numerics.Tensors;
using System.Text;
using Microsoft.ML.Tokenizers;

using VideoProduction.Decisions.Experimental.Generator;

namespace VideoProduction.Decisions.Experimental.Julia;

using DecisionKind = Generator.DecisionKind;
using DecisionQuestion = Generator.DecisionQuestion;
using BinaryDecisionQuestion = Generator.BinaryDecisionQuestion;
using ChoiceDecisionQuestion = Generator.ChoiceDecisionQuestion;
using ScoreDecisionQuestion = Generator.ScoreDecisionQuestion;

/// <summary>Owned buffers, exposed through standard read-only tensor spans.</summary>
public sealed class JuliaInputBatch
{
    public DecisionInput Input { get; }
    public int BatchSize => Input.Questions.Count;
    public int SequenceLength { get; }
    public int MarkerWidth { get; }
    internal long[] Ids { get; }
    internal long[] Attention { get; }
    internal long[] Positions { get; }
    internal bool[] Masks { get; }
    internal long[] Types { get; }
    internal int[] OptionCounts { get; }
    public ReadOnlyTensorSpan<long> InputIds => new(Ids, [BatchSize, SequenceLength]);
    public ReadOnlyTensorSpan<long> AttentionMask => new(Attention, [BatchSize, SequenceLength]);
    public ReadOnlyTensorSpan<long> MarkerPositions => new(Positions, [BatchSize, MarkerWidth]);
    public ReadOnlyTensorSpan<bool> MarkerMask => new(Masks, [BatchSize, MarkerWidth]);
    public ReadOnlyTensorSpan<long> QuestionTypes => new(Types, [BatchSize]);

    internal JuliaInputBatch(DecisionInput input, int length, int width, long[] ids, long[] attention,
        long[] positions, bool[] masks, long[] types, int[] counts)
        => (Input, SequenceLength, MarkerWidth, Ids, Attention, Positions, Masks, Types, OptionCounts) =
            (input, length, width, ids, attention, positions, masks, types, counts);
}

/// <summary>
/// Julia's Python strict sequence/Collator recipe: 1024 context, 256 head, 48 tokens per option.
/// Uses the Microsoft tokenizer abstraction; preparation and owned disposal are serialized.
/// </summary>
public sealed class PrepareDecisionInputs : IDisposable
{
    public const int MaxLength = 1024;
    public const int HeadLength = 256;
    private readonly Tokenizer _tokenizer;
    private readonly bool _ownsTokenizer;
    private readonly string[] _reserved;
    private readonly HashSet<long> _reservedIds;
    private readonly SemaphoreSlim _gate = new(1);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private bool _disposed;

    public PrepareDecisionInputs(string tokenizerJsonPath) : this(new JuliaTokenizer(tokenizerJsonPath)) { }

    private PrepareDecisionInputs(JuliaTokenizer tokenizer)
        : this(tokenizer, tokenizer.ReservedTokens, tokenizer.ReservedIds, ownsTokenizer: true) { }

    public PrepareDecisionInputs(Tokenizer tokenizer, IEnumerable<string> reservedTokens,
        IEnumerable<long> reservedIds, bool ownsTokenizer = false)
    {
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        _reserved = reservedTokens.ToArray();
        _reservedIds = reservedIds.ToHashSet();
        _ownsTokenizer = ownsTokenizer;
    }

    public JuliaInputBatch Prepare(DecisionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        _gate.Wait(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string[][] labels = input.Questions.Select(Labels).ToArray();
            var state = Encode(PythonJson.RenderState(input.State), MaxLength, "state", cancellationToken);
            var rows = new List<(long[] Ids, long[] Positions, long Type)>();
            for (int row = 0; row < input.Questions.Count; row++)
            {
                var q = input.Questions[row];
                var options = labels[row].Select(x => Encode(" " + x, 48, "option", cancellationToken)).ToArray();
                int budget = HeadLength - options.Sum(x => x.Length + 1);
                if (budget < 16) throw new ArgumentException("Julia options exceed the lossless head budget.");
                var head = Encode($"{KindName(q.Kind)} question: {q.Instructions}", budget, "instructions", cancellationToken);
                int room = MaxLength - (head.Length + options.Sum(x => x.Length + 1) + 4);
                if (room < 1 || state.Length > room) throw new ArgumentException("Julia state exceeds the lossless context budget.");
                List<long> ids = [2, .. head, 1];
                List<long> positions = [];
                foreach (var option in options) { positions.Add(ids.Count); ids.Add(4); ids.AddRange(option); }
                ids.Add(1);
                ids.AddRange(state);
                ids.Add(1);
                rows.Add((ids.ToArray(), positions.ToArray(), q.Kind switch { DecisionKind.Choice => 0, DecisionKind.Score => 1, _ => 2 }));
            }
            int length = Math.Min(MaxLength, ((rows.Max(r => r.Ids.Length) + 7) / 8) * 8);
            int width = rows.Max(r => r.Positions.Length);
            var idsBuffer = new long[checked(rows.Count * length)];
            var attention = new long[idsBuffer.Length];
            var positionsBuffer = new long[checked(rows.Count * width)];
            var masks = new bool[positionsBuffer.Length];
            for (int row = 0; row < rows.Count; row++)
            {
                rows[row].Ids.CopyTo(idsBuffer, row * length);
                Array.Fill(attention, 1L, row * length, rows[row].Ids.Length);
                rows[row].Positions.CopyTo(positionsBuffer, row * width);
                Array.Fill(masks, true, row * width, rows[row].Positions.Length);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(input, length, width, idsBuffer, attention, positionsBuffer, masks,
                rows.Select(r => r.Type).ToArray(), labels.Select(x => x.Length).ToArray());
        }
        finally { _gate.Release(); }
    }

    private long[] Encode(string text, int budget, string part, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StrictUtf8.GetByteCount(text);
        if (_reserved.Any(t => text.Contains(t, StringComparison.Ordinal)))
            throw new NotSupportedException($"Reserved Julia token in {part}.");
        var ids = _tokenizer.EncodeToIds(text).Select(id => (long)id).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (ids.Length > budget) throw new ArgumentException($"Julia {part} token budget exceeded; truncation is disabled.");
        if (ids.Any(_reservedIds.Contains)) throw new NotSupportedException("Tokenizer emitted a reserved Julia ID from caller text.");
        return ids;
    }

    private static string[] Labels(DecisionQuestion question)
    {
        string[] labels = question switch {
            ChoiceDecisionQuestion c => c.Candidates.Select(c => c.Description).ToArray(),
            ScoreDecisionQuestion s => s.Rubric.ToArray(),
            BinaryDecisionQuestion b when (b.TrueDescription is null) != (b.FalseDescription is null)
                => throw new NotSupportedException("Julia Binary criteria require both descriptions or neither."),
            BinaryDecisionQuestion b => [b.FalseDescription ?? "false", b.TrueDescription ?? "true"],
            _ => throw new NotSupportedException("Unknown decision kind.")
        };
        if (labels.Length is < 2 or > 20) throw new ArgumentException("Julia requires 2-20 alternatives; routing is not implicit.");
        return labels;
    }

    private static string KindName(DecisionKind kind) => kind switch {
        DecisionKind.Choice => "choice",
        DecisionKind.Score => "score",
        DecisionKind.Binary => "noul",
        _ => throw new NotSupportedException()
    };

    public void Dispose()
    {
        _gate.Wait();
        try { if (!_disposed) { _disposed = true; if (_ownsTokenizer) (_tokenizer as IDisposable)?.Dispose(); } }
        finally { _gate.Release(); }
    }
}
