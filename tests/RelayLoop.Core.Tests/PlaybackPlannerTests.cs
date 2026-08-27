using RelayLoop.Core;

namespace RelayLoop.Core.Tests;

public sealed class PlaybackPlannerTests
{
    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(37.5)]
    [InlineData(100)]
    public void PlaybackOptions_AcceptsPresetAndCustomSpeeds(double speed)
    {
        var options = new PlaybackOptions(speed, repeatCount: 9_999);

        options.Validate();
        Assert.Equal(speed, options.Speed);
    }

    [Theory]
    [InlineData(0.249)]
    [InlineData(100.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void PlaybackOptions_RejectsInvalidSpeed(double speed)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackOptions(speed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_000)]
    public void PlaybackOptions_RejectsInvalidRepeatCount(int repeatCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackOptions(1, repeatCount));
    }

    [Theory]
    [InlineData(1_000_000, 0.25, 4_000_000)]
    [InlineData(1_000_000, 2, 500_000)]
    [InlineData(3, 2, 2)]
    public void ScaleDelayMicroseconds_UsesPlaybackSpeed(
        long original,
        double speed,
        long expected)
    {
        Assert.Equal(expected, PlaybackPlanner.ScaleDelayMicroseconds(original, speed));
    }

    [Theory]
    [InlineData(1_000_000, 10_000_000, 10_000_000)]
    [InlineData(500_000, 10_000_000, 5_000_000)]
    [InlineData(1, 1_000_000, 1)]
    public void MicrosecondsToStopwatchTicks_UsesSuppliedMonotonicFrequency(
        long microseconds,
        long frequency,
        long expected)
    {
        Assert.Equal(expected, PlaybackPlanner.MicrosecondsToStopwatchTicks(microseconds, frequency));
    }

    [Fact]
    public void EnumerateLoop_UsesCumulativeRoundingWithoutDrift()
    {
        var document = TestMacros.WithDelays(1, 1, 1, 1);

        var plan = PlaybackPlanner.EnumerateLoop(document, speed: 4).ToArray();

        Assert.Equal([0L, 1L, 0L, 0L], plan.Select(item => item.ScaledDelayMicroseconds));
        Assert.Equal(1, plan[^1].ScheduledAtMicroseconds);
    }

    [Fact]
    public void DisabledEvents_PreserveTimelineDelays()
    {
        var document = TestMacros.WithDelays(100_000, 200_000);
        document.Events[0].Enabled = false;

        var plan = PlaybackPlanner.EnumerateLoop(document, speed: 1).ToArray();

        Assert.False(plan[0].Event.Enabled);
        Assert.Equal(TimeSpan.FromMilliseconds(300), PlaybackPlanner.GetSingleLoopDuration(document));
        Assert.Equal(300_000, plan[1].ScheduledAtMicroseconds);
    }

    [Fact]
    public void Enumerate_ImplementsFiniteRepeatCount()
    {
        var document = TestMacros.WithDelays(1, 2);
        var options = new PlaybackOptions(1, repeatCount: 3);

        var plan = PlaybackPlanner.Enumerate(document, options).ToArray();

        Assert.Equal(6, plan.Length);
        Assert.Equal([1, 1, 2, 2, 3, 3], plan.Select(item => item.LoopNumber));
    }

    [Fact]
    public void ContinuousPlayback_HasNoFiniteLoopCountOrEstimate()
    {
        var document = TestMacros.WithDelays(1_000_000);
        var options = new PlaybackOptions(1, continuous: true);

        Assert.Null(PlaybackPlanner.GetTotalLoopCount(options));
        Assert.Null(PlaybackPlanner.GetTotalDuration(document, options));
        Assert.Null(PlaybackPlanner.EstimateRemaining(document, options, 12, TimeSpan.Zero));
    }

    [Fact]
    public void TotalDurationAndRemainingEstimate_AccountForSpeedAndLoops()
    {
        var document = TestMacros.WithDelays(1_000_000, 500_000);
        var options = new PlaybackOptions(speed: 2, repeatCount: 3);

        Assert.Equal(TimeSpan.FromMilliseconds(750), PlaybackPlanner.GetSingleLoopDuration(document, 2));
        Assert.Equal(TimeSpan.FromMilliseconds(2_250), PlaybackPlanner.GetTotalDuration(document, options));
        Assert.Equal(
            TimeSpan.FromMilliseconds(1_300),
            PlaybackPlanner.EstimateRemaining(document, options, completedLoops: 1, elapsedInCurrentLoop: TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void Enumeration_ObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => PlaybackPlanner.Enumerate(TestMacros.WithDelays(1), new PlaybackOptions(), cancellation.Token).ToArray());
    }

    [Fact]
    public void Enumeration_ObservesPreCancellationBeforeValidatingTheDocument()
    {
        var invalid = TestMacros.WithDelays(-1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => PlaybackPlanner.Enumerate(invalid, new PlaybackOptions(), cancellation.Token));
    }

    [Fact]
    public void Enumeration_SnapshotsMutableDocumentAndOptionsOnce()
    {
        var document = TestMacros.WithDelays(100);
        var options = new PlaybackOptions(speed: 1, repeatCount: 2);

        var planned = PlaybackPlanner.Enumerate(document, options);
        document.Events[0].DelayMicroseconds = 900;
        options.Speed = 2;
        options.RepeatCount = 3;
        var result = planned.ToArray();

        Assert.Equal(2, result.Length);
        Assert.Equal([1, 2], result.Select(item => item.LoopNumber));
        Assert.All(result, item => Assert.Equal(100, item.ScaledDelayMicroseconds));
        Assert.NotSame(document.Events[0], result[0].Event);
    }

    [Fact]
    public void EnumerateLoop_SnapshotsTheCallerOwnedDocument()
    {
        var document = TestMacros.WithDelays(250);

        var planned = PlaybackPlanner.EnumerateLoop(document, speed: 1);
        document.Events[0].DelayMicroseconds = 500;
        var result = Assert.Single(planned);

        Assert.Equal(250, result.ScaledDelayMicroseconds);
        Assert.NotSame(document.Events[0], result.Event);
    }

    [Fact]
    public void ZeroDelayContinuousEnumeration_YieldsToCancellationWithoutFlooding()
    {
        var document = TestMacros.WithDelays(0);
        var options = new PlaybackOptions(speed: 1, continuous: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var count = 0;

        Assert.Throws<OperationCanceledException>(() =>
        {
            foreach (var _ in PlaybackPlanner.Enumerate(document, options, cancellation.Token))
            {
                count++;
            }
        });

        Assert.InRange(count, 1, 5_000);
    }
}
