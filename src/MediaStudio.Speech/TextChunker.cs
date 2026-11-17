using System.Text;
using System.Text.RegularExpressions;

namespace MediaStudio.Speech;

/// <summary>Sentence splitting and length-bounded packing shared by the TTS clients.</summary>
public static partial class TextChunker
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "e.g", "i.e", "fig", "no", "approx", "inc", "ltd", "co"
    };

    /// <summary>Splits text into sentences at <c>. ! ? …</c> followed by whitespace, and at blank lines. Common abbreviations do not end a sentence.</summary>
    public static IReadOnlyList<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return sentences;
        foreach (var paragraph in ParagraphBreak().Split(text.Replace("\r\n", "\n")))
        {
            var current = new StringBuilder();
            var words = Whitespace().Split(paragraph.Trim());
            foreach (var word in words)
            {
                if (word.Length == 0) continue;
                if (current.Length > 0) current.Append(' ');
                current.Append(word);
                if (EndsSentence(word))
                {
                    sentences.Add(current.ToString());
                    current.Clear();
                }
            }
            if (current.Length > 0) sentences.Add(current.ToString());
        }
        return sentences;
    }

    /// <summary>
    /// Greedily packs sentences into chunks whose <paramref name="measure"/> is at most <paramref name="max"/>.
    /// A single sentence over the limit is split at clause punctuation, then at spaces, then hard.
    /// </summary>
    public static IReadOnlyList<string> Pack(IEnumerable<string> sentences, int max, Func<string, int>? measure = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        measure ??= s => s.Length;
        var chunks = new List<string>();
        var current = "";
        foreach (var sentence in sentences.SelectMany(s => measure(s) <= max ? [s] : SplitLong(s, max, measure)))
        {
            var candidate = current.Length == 0 ? sentence : current + " " + sentence;
            if (measure(candidate) <= max) { current = candidate; continue; }
            if (current.Length > 0) chunks.Add(current);
            current = sentence;
        }
        if (current.Length > 0) chunks.Add(current);
        return chunks;
    }

    private static IEnumerable<string> SplitLong(string sentence, int max, Func<string, int> measure)
    {
        foreach (var separators in new[] { new[] { ',', ';', ':' }, new[] { ' ' } })
        {
            var pieces = SplitKeeping(sentence, separators);
            if (pieces.Count > 1 && pieces.All(p => measure(p) <= max)) return Pack(pieces, max, measure);
            if (pieces.Count > 1) return pieces.SelectMany(p => measure(p) <= max ? [p] : SplitLong(p, max, measure));
        }
        var hard = new List<string>();
        var remaining = sentence;
        while (remaining.Length > 0)
        {
            var take = Math.Min(remaining.Length, Math.Max(1, remaining.Length * max / Math.Max(1, measure(remaining))));
            while (take > 1 && measure(remaining[..take]) > max) take--;
            hard.Add(remaining[..take]);
            remaining = remaining[take..];
        }
        return hard;
    }

    private static List<string> SplitKeeping(string text, char[] separators)
    {
        var pieces = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(separators, text[i]) < 0) continue;
            var piece = text[start..(i + 1)].Trim();
            if (piece.Length > 0) pieces.Add(piece);
            start = i + 1;
        }
        var tail = text[start..].Trim();
        if (tail.Length > 0) pieces.Add(tail);
        return pieces;
    }

    private static bool EndsSentence(string word)
    {
        var trimmed = word.TrimEnd('"', '\'', ')', ']', '”', '’');
        if (trimmed.Length == 0) return false;
        var last = trimmed[^1];
        if (last is '!' or '?' or '…') return true;
        if (last != '.') return false;
        var stem = trimmed.TrimEnd('.').TrimStart('(', '"', '\'', '“', '‘');
        return !Abbreviations.Contains(stem) && !(stem.Length == 1 && char.IsLetter(stem[0]));
    }

    [GeneratedRegex(@"\n\s*\n")] private static partial Regex ParagraphBreak();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
