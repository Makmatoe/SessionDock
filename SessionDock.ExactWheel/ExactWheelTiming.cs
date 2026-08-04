using System.Globalization;
using System.Numerics;

namespace SessionDock.ExactWheel;

public readonly record struct ExactWheelPlaybackRate
{
    private ExactWheelPlaybackRate(ulong numerator, ulong denominator)
    {
        Numerator = numerator;
        Denominator = denominator;
    }

    public ulong Numerator { get; }

    public ulong Denominator { get; }

    public double Multiplier => (double)Numerator / Denominator;

    public static ExactWheelPlaybackRate Recorded { get; } =
        new(1, 1);

    public static ExactWheelPlaybackRate FromRatio(
        ulong numerator,
        ulong denominator)
    {
        if (numerator == 0 || denominator == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numerator),
                "Playback rate components must be positive.");
        }

        if (new BigInteger(numerator) * 10 < denominator ||
            new BigInteger(numerator) > new BigInteger(denominator) * 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numerator),
                "Playback speed must be from 0.1x through 100x.");
        }

        var divisor = GreatestCommonDivisor(numerator, denominator);
        return new ExactWheelPlaybackRate(
            numerator / divisor,
            denominator / divisor);
    }

    public static ExactWheelPlaybackRate Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!decimal.TryParse(
                text,
                NumberStyles.AllowDecimalPoint |
                NumberStyles.AllowLeadingSign |
                NumberStyles.AllowLeadingWhite |
                NumberStyles.AllowTrailingWhite,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed <= 0)
        {
            throw new FormatException(
                "Playback rate must be a positive invariant decimal.");
        }

        var bits = decimal.GetBits(parsed);
        var scale = (bits[3] >> 16) & 0x7F;
        var negative = (bits[3] & unchecked((int)0x80000000)) != 0;
        if (negative)
            throw new FormatException("Playback rate must be positive.");

        var unscaled =
            (new BigInteger((uint)bits[2]) << 64) |
            (new BigInteger((uint)bits[1]) << 32) |
            (uint)bits[0];
        var denominator = BigInteger.Pow(10, scale);
        var divisor = BigInteger.GreatestCommonDivisor(
            unscaled,
            denominator);
        unscaled /= divisor;
        denominator /= divisor;
        if (unscaled > ulong.MaxValue || denominator > ulong.MaxValue)
        {
            throw new FormatException(
                "Playback rate has too much precision.");
        }

        return FromRatio((ulong)unscaled, (ulong)denominator);
    }

    private static ulong GreatestCommonDivisor(ulong left, ulong right)
    {
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }

        return left;
    }
}

public static class ExactWheelTiming
{
    private const ulong MicrosecondsPerSecond = 1_000_000;

    public static ulong ScaleDurationMicroseconds(
        ulong recordedDurationMicroseconds,
        ExactWheelPlaybackRate rate)
    {
        EnsureRate(rate);
        var scaled = DivideRounded(
            new BigInteger(recordedDurationMicroseconds) * rate.Denominator,
            rate.Numerator);
        return ToUInt64(scaled, "Scaled duration is outside the supported range.");
    }

    public static ulong TimestampOffsetMicroseconds(
        long originTicks,
        long sampleTicks,
        long frequency)
    {
        if (originTicks < 0 || sampleTicks < originTicks || frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleTicks));

        var microseconds = DivideRounded(
            new BigInteger(sampleTicks - originTicks) *
                MicrosecondsPerSecond,
            frequency);
        return ToUInt64(
            microseconds,
            "Capture timestamp is outside the supported range.");
    }

    public static long EventDeadlineTicks(
        long originTicks,
        ulong eventOffsetMicroseconds,
        ExactWheelPlaybackRate rate,
        long frequency) =>
        PlaybackDeadlineTicks(
            originTicks,
            loopIndex: 0,
            recordedDurationMicroseconds: 0,
            eventOffsetMicroseconds,
            rate,
            interLoopDelayMicroseconds: 0,
            frequency);

    public static long LoopStartTicks(
        long originTicks,
        ulong loopIndex,
        ulong recordedDurationMicroseconds,
        ExactWheelPlaybackRate rate,
        ulong interLoopDelayMicroseconds,
        long frequency) =>
        PlaybackDeadlineTicks(
            originTicks,
            loopIndex,
            recordedDurationMicroseconds,
            eventOffsetMicroseconds: 0,
            rate,
            interLoopDelayMicroseconds,
            frequency);

    public static long PlaybackDeadlineTicks(
        long originTicks,
        ulong loopIndex,
        ulong recordedDurationMicroseconds,
        ulong eventOffsetMicroseconds,
        ExactWheelPlaybackRate rate,
        ulong interLoopDelayMicroseconds,
        long frequency)
    {
        EnsureRate(rate);
        if (originTicks < 0 || frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(originTicks));

        var rateAdjustedLoop =
            new BigInteger(recordedDurationMicroseconds) *
                rate.Denominator +
            new BigInteger(interLoopDelayMicroseconds) * rate.Numerator;
        var numeratorMicroseconds =
            new BigInteger(loopIndex) * rateAdjustedLoop +
            new BigInteger(eventOffsetMicroseconds) * rate.Denominator;
        var offsetTicks = DivideRounded(
            numeratorMicroseconds * frequency,
            new BigInteger(rate.Numerator) * MicrosecondsPerSecond);
        var deadline = new BigInteger(originTicks) + offsetTicks;
        if (deadline < 0 || deadline > long.MaxValue)
        {
            throw new OverflowException(
                "Playback deadline is outside the Stopwatch range.");
        }

        return (long)deadline;
    }

    internal static long TicksToMicroseconds(long ticks, long frequency)
    {
        if (ticks <= 0)
            return 0;
        if (frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequency));

        var converted = DivideRounded(
            new BigInteger(ticks) * MicrosecondsPerSecond,
            frequency);
        return converted > long.MaxValue
            ? long.MaxValue
            : (long)converted;
    }

    private static BigInteger DivideRounded(
        BigInteger numerator,
        BigInteger denominator)
    {
        if (numerator < 0 || denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(numerator));

        return (numerator + denominator / 2) / denominator;
    }

    private static ulong ToUInt64(BigInteger value, string message)
    {
        if (value < 0 || value > ulong.MaxValue)
            throw new OverflowException(message);
        return (ulong)value;
    }

    private static void EnsureRate(ExactWheelPlaybackRate rate)
    {
        if (rate.Numerator == 0 || rate.Denominator == 0)
        {
            throw new ArgumentException(
                "Playback rate is not initialized.",
                nameof(rate));
        }
    }
}
