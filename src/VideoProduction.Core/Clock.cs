using System.Numerics;

namespace VideoProduction;

public enum Rounding { Floor, Ceiling, Nearest }

public readonly record struct Rate(int Numerator, int Denominator)
{
    public void Validate()
    {
        Guard.Require(Numerator > 0 && Denominator > 0, "clock.rate", "Frame rate terms must be positive.");
    }

    public long Samples(long frame, int sampleRate, Rounding rounding)
    {
        Validate();
        Guard.Require(frame >= 0 && sampleRate > 0, "clock.range", "Negative frame or invalid sample rate.");
        return Divide((BigInteger)frame * Denominator * sampleRate, Numerator, rounding);
    }

    public long Frames(long samples, int sampleRate, Rounding rounding)
    {
        Validate();
        Guard.Require(samples >= 0 && sampleRate > 0, "clock.range", "Negative sample or invalid sample rate.");
        return Divide((BigInteger)samples * Numerator, (BigInteger)sampleRate * Denominator, rounding);
    }

    public static long Divide(BigInteger value, BigInteger divisor, Rounding rounding)
    {
        Guard.Require(value >= 0 && divisor > 0, "clock.range", "Clock division requires nonnegative values and positive divisor.");
        var result = BigInteger.DivRem(value, divisor, out var remainder);
        return checked((long)(result + (rounding switch {
            Rounding.Floor => 0,
            Rounding.Ceiling => remainder > 0 ? 1 : 0,
            Rounding.Nearest => remainder * 2 >= divisor ? 1 : 0,
            _ => throw new ArgumentOutOfRangeException(nameof(rounding))
        })));
    }
}

public readonly record struct Span64(long Start, long End)
{
    public long Length => checked(End - Start);
    public void Validate() => Guard.Require(Start >= 0 && End > Start, "clock.span", "Ranges are nonempty, nonnegative and half-open.");
}
