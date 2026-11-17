using System.Numerics.Tensors;

namespace MediaStudio.Speech.Audio;

/// <summary>Vectorized sample math (TensorPrimitives) shared by the local and cloud speech clients.</summary>
public static class AudioMath
{
    /// <summary>Scales <paramref name="samples"/> so the peak magnitude equals <paramref name="target"/>. Silence is left untouched.</summary>
    public static void PeakNormalize(Span<float> samples, float target = 0.95f)
    {
        if (samples.IsEmpty) return;
        var peak = MathF.Abs(TensorPrimitives.MaxMagnitude<float>(samples));
        if (peak <= 1e-6f || !float.IsFinite(peak)) return;
        TensorPrimitives.Multiply(samples, target / peak, samples);
    }

    /// <summary>Attenuates only when the peak exceeds <paramref name="ceiling"/> (never amplifies quiet speech).</summary>
    public static void PeakLimit(Span<float> samples, float ceiling = 0.95f)
    {
        if (samples.IsEmpty) return;
        var peak = MathF.Abs(TensorPrimitives.MaxMagnitude<float>(samples));
        if (peak > ceiling && float.IsFinite(peak)) TensorPrimitives.Multiply(samples, ceiling / peak, samples);
    }

    /// <summary>Linear cross-fade: <c>destination[i] = tail[i]·(1−r) + head[i]·r</c> with r ramping 0→1. All spans share one length.</summary>
    public static void CrossFade(ReadOnlySpan<float> tail, ReadOnlySpan<float> head, Span<float> destination)
    {
        var n = destination.Length;
        if (tail.Length != n || head.Length != n) throw new ArgumentException("Cross-fade spans must have the same length.");
        if (n == 0) return;
        var rampUp = new float[n];
        var rampDown = new float[n];
        for (var i = 0; i < n; i++) rampUp[i] = n == 1 ? 0.5f : (float)i / (n - 1);
        TensorPrimitives.Subtract(1f, rampUp, rampDown);
        var faded = new float[n];
        TensorPrimitives.Multiply(tail, rampDown, faded);
        TensorPrimitives.Multiply(head, rampUp, destination);
        TensorPrimitives.Add(destination, faded, destination);
    }

    /// <summary>Joins segments, overlapping each boundary by <paramref name="fadeSamples"/> with a linear cross-fade.</summary>
    public static float[] Concatenate(IReadOnlyList<float[]> segments, int fadeSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fadeSamples);
        if (segments.Count == 0) return [];
        long total = segments[0].Length;
        for (var i = 1; i < segments.Count; i++)
            total += segments[i].Length - Overlap(segments[i - 1].Length, segments[i].Length, fadeSamples);
        var output = new float[total];
        segments[0].CopyTo(output, 0);
        var cursor = segments[0].Length;
        for (var i = 1; i < segments.Count; i++)
        {
            var next = segments[i];
            var overlap = Overlap(segments[i - 1].Length, next.Length, fadeSamples);
            var start = cursor - overlap;
            if (overlap > 0)
            {
                var region = output.AsSpan(start, overlap);
                CrossFade(region.ToArray(), next.AsSpan(0, overlap), region);
            }
            next.AsSpan(overlap).CopyTo(output.AsSpan(cursor));
            cursor = start + next.Length;
        }
        return output;
    }

    /// <summary>Clamps to [-1, 1], scales by 32767 and truncates to PCM16.</summary>
    public static void ToPcm16(ReadOnlySpan<float> samples, Span<short> destination)
    {
        if (destination.Length < samples.Length) throw new ArgumentException("Destination is shorter than the source.", nameof(destination));
        var scratch = new float[samples.Length];
        TensorPrimitives.Clamp(samples, -1f, 1f, scratch);
        TensorPrimitives.Multiply(scratch, 32767f, scratch);
        TensorPrimitives.ConvertTruncating<float, short>(scratch, destination);
    }

    /// <summary>Converts PCM16 samples to floats in [-1, 1).</summary>
    public static void FromPcm16(ReadOnlySpan<short> samples, Span<float> destination)
    {
        TensorPrimitives.ConvertChecked<short, float>(samples, destination[..samples.Length]);
        TensorPrimitives.Multiply(destination[..samples.Length], 1f / 32768f, destination[..samples.Length]);
    }

    public static double Rms(ReadOnlySpan<float> samples) =>
        samples.IsEmpty ? 0 : Math.Sqrt(TensorPrimitives.SumOfSquares(samples) / samples.Length);

    /// <summary>Linear-interpolation resampler (adequate for speech recognition input).</summary>
    public static float[] Resample(ReadOnlySpan<float> samples, int fromRate, int toRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toRate);
        if (fromRate == toRate || samples.IsEmpty) return samples.ToArray();
        var length = (int)Math.Max(1, (long)samples.Length * toRate / fromRate);
        var output = new float[length];
        var step = (double)fromRate / toRate;
        for (var i = 0; i < length; i++)
        {
            var position = i * step;
            var index = (int)position;
            if (index >= samples.Length - 1) { output[i] = samples[^1]; continue; }
            var fraction = (float)(position - index);
            output[i] = samples[index] + (samples[index + 1] - samples[index]) * fraction;
        }
        return output;
    }

    private static int Overlap(int previous, int next, int fade) => Math.Min(fade, Math.Min(previous, next));
}
