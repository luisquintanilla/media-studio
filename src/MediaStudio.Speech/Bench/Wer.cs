using System.Text;

namespace MediaStudio.Speech.Bench;

public sealed record WerResult(int Substitutions, int Deletions, int Insertions, int ReferenceWords)
{
    public int Errors => Substitutions + Deletions + Insertions;
    /// <summary>Word error rate. With an empty reference it is the number of inserted words (0 when both are empty).</summary>
    public double Rate => ReferenceWords == 0 ? Errors : (double)Errors / ReferenceWords;
}

/// <summary>Word error rate: word-level Levenshtein distance after lower-casing and stripping punctuation.</summary>
public static class Wer
{
    public static WerResult Compute(string reference, string hypothesis) => Compute(Normalize(reference), Normalize(hypothesis));

    public static WerResult Compute(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        // Each cell keeps (cost, substitutions, deletions, insertions); ties prefer substitution, then deletion, then insertion.
        var previous = new (int Cost, int S, int D, int I)[hypothesis.Count + 1];
        var current = new (int Cost, int S, int D, int I)[hypothesis.Count + 1];
        for (var j = 0; j <= hypothesis.Count; j++) previous[j] = (j, 0, 0, j);
        for (var i = 1; i <= reference.Count; i++)
        {
            current[0] = (i, 0, i, 0);
            for (var j = 1; j <= hypothesis.Count; j++)
            {
                var diagonal = previous[j - 1];
                (int Cost, int S, int D, int I) best = reference[i - 1] == hypothesis[j - 1] ? diagonal : (diagonal.Cost + 1, diagonal.S + 1, diagonal.D, diagonal.I);
                var up = previous[j];
                if (up.Cost + 1 < best.Cost) best = (up.Cost + 1, up.S, up.D + 1, up.I);
                var left = current[j - 1];
                if (left.Cost + 1 < best.Cost) best = (left.Cost + 1, left.S, left.D, left.I + 1);
                current[j] = best;
            }
            (previous, current) = (current, previous);
        }
        var result = previous[hypothesis.Count];
        return new(result.S, result.D, result.I, reference.Count);
    }

    /// <summary>Lower-cases, removes apostrophes (so "don't" = "dont"), turns other punctuation into spaces and splits.</summary>
    public static string[] Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            else if (c is not ('\'' or '\u2019')) builder.Append(' ');
        }
        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
