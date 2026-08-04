using System.Numerics;
using SessionDock.ExactWheel;

namespace SessionDock.Tests;

public sealed class ExactWheelTimingTests
{
    [Fact]
    public void PlaybackRate_ReducesExactRational()
    {
        var rate = ExactWheelPlaybackRate.FromRatio(150, 100);

        Assert.Equal(3UL, rate.Numerator);
        Assert.Equal(2UL, rate.Denominator);
        Assert.Equal(1.5, rate.Multiplier);
    }

    [Theory]
    [InlineData("0.1", 1UL, 10UL)]
    [InlineData("1.0", 1UL, 1UL)]
    [InlineData("1.25", 5UL, 4UL)]
    [InlineData("100", 100UL, 1UL)]
    public void PlaybackRate_ParseInvariantDecimal_IsExact(
        string value,
        ulong numerator,
        ulong denominator)
    {
        var rate = ExactWheelPlaybackRate.Parse(value);

        Assert.Equal(numerator, rate.Numerator);
        Assert.Equal(denominator, rate.Denominator);
    }

    [Theory]
    [InlineData("0.09")]
    [InlineData("100.01")]
    [InlineData("1,5")]
    [InlineData("-1")]
    [InlineData("NaN")]
    public void PlaybackRate_InvalidOrOutOfRangeText_IsRejected(string value)
    {
        Assert.ThrowsAny<Exception>(() => ExactWheelPlaybackRate.Parse(value));
    }

    [Fact]
    public void PlaybackRate_ExactBoundaryChecks_DoNotUseFloatingPoint()
    {
        Assert.Equal(
            ExactWheelPlaybackRate.FromRatio(1, 10),
            ExactWheelPlaybackRate.Parse("0.1000000000000000000000000000"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExactWheelPlaybackRate.FromRatio(9, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExactWheelPlaybackRate.FromRatio(101, 1));
    }

    [Fact]
    public void PlaybackDeadlineTicks_UsesFixedOriginAcrossLoops()
    {
        const long frequency = 10_000_000;
        const long origin = 100;
        var rate = ExactWheelPlaybackRate.FromRatio(2, 1);

        var deadline = ExactWheelTiming.PlaybackDeadlineTicks(
            origin,
            loopIndex: 3,
            recordedDurationMicroseconds: 1_000_000,
            eventOffsetMicroseconds: 250_000,
            rate,
            interLoopDelayMicroseconds: 100_000,
            frequency);

        Assert.Equal(19_250_100, deadline);
        Assert.Equal(
            18_000_100,
            ExactWheelTiming.LoopStartTicks(
                origin,
                3,
                1_000_000,
                rate,
                100_000,
                frequency));
    }

    [Fact]
    public void PlaybackDeadlineTicks_MillionthLoop_HasNoIncrementalDrift()
    {
        const long frequency = 1_000_003;
        const long origin = 17;
        const ulong loop = 1_000_000;
        const ulong duration = 333_333;
        const ulong delay = 17;
        const ulong eventOffset = 123_456;
        var rate = ExactWheelPlaybackRate.FromRatio(3, 2);
        var numeratorMicroseconds =
            new BigInteger(loop) *
                (new BigInteger(duration) * rate.Denominator +
                 new BigInteger(delay) * rate.Numerator) +
            new BigInteger(eventOffset) * rate.Denominator;
        var denominator = new BigInteger(rate.Numerator) * 1_000_000;
        var expected = origin + (long)(
            (numeratorMicroseconds * frequency + denominator / 2) /
            denominator);

        var actual = ExactWheelTiming.PlaybackDeadlineTicks(
            origin,
            loop,
            duration,
            eventOffset,
            rate,
            delay,
            frequency);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PlaybackDeadlineTicks_CommonTimelineMath_IsAllocationFree()
    {
        var rate = ExactWheelPlaybackRate.FromRatio(3, 2);
        for (ulong loop = 0; loop < 100; loop++)
        {
            _ = ExactWheelTiming.PlaybackDeadlineTicks(
                17,
                loop,
                5_000_000,
                123_456,
                rate,
                10_000,
                10_000_000);
        }
        long aggregate = 0;
        var allocated = AllocationMeasurement.MinimumAllocatedBytes(() =>
        {
            aggregate = 0;
            for (ulong loop = 0; loop < 10_000; loop++)
            {
                aggregate ^= ExactWheelTiming.PlaybackDeadlineTicks(
                    17,
                    loop,
                    5_000_000,
                    123_456,
                    rate,
                    10_000,
                    10_000_000);
            }
        });
        Assert.NotEqual(0, aggregate);
        Assert.InRange(allocated, 0, 256);
    }

    [Theory]
    [InlineData(1_000_000UL, 1UL, 1UL, 1_000_000UL)]
    [InlineData(1_000_000UL, 2UL, 1UL, 500_000UL)]
    [InlineData(1UL, 3UL, 2UL, 1UL)]
    public void ScaleDurationMicroseconds_RoundsToNearest(
        ulong duration,
        ulong numerator,
        ulong denominator,
        ulong expected)
    {
        Assert.Equal(
            expected,
            ExactWheelTiming.ScaleDurationMicroseconds(
                duration,
                ExactWheelPlaybackRate.FromRatio(numerator, denominator)));
    }

    [Fact]
    public void TimestampOffsetMicroseconds_ConvertsStopwatchTicksExactly()
    {
        Assert.Equal(
            250_000UL,
            ExactWheelTiming.TimestampOffsetMicroseconds(
                originTicks: 1_000,
                sampleTicks: 2_501_000,
                frequency: 10_000_000));
    }

    [Fact]
    public void PlaybackDeadlineTicks_OverflowAndDefaultRate_AreRejected()
    {
        Assert.Throws<OverflowException>(() =>
            ExactWheelTiming.EventDeadlineTicks(
                long.MaxValue,
                1,
                ExactWheelPlaybackRate.Recorded,
                1_000_000));
        Assert.Throws<ArgumentException>(() =>
            ExactWheelTiming.EventDeadlineTicks(
                0,
                1,
                default,
                1_000_000));
    }
}
