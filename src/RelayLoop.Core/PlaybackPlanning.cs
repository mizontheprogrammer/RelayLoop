using System.Diagnostics;

namespace RelayLoop.Core;

public sealed class PlaybackOptions
{
    public const double MinimumSpeed = 0.25;
    public const double MaximumSpeed = 100.0;
    public const int MinimumRepeatCount = 1;
    public const int MaximumRepeatCount = 9_999;

    public static IReadOnlyList<double> PresetSpeeds { get; } = [0.25, 0.5, 1, 2, 4, 8];

    public PlaybackOptions()
    {
    }

    public PlaybackOptions(double speed, int repeatCount = 1, bool continuous = false)
    {
        Speed = speed;
        RepeatCount = repeatCount;
        Continuous = continuous;
        Validate();
    }

    public double Speed { get; set; } = 1.0;

    public int RepeatCount { get; set; } = 1;

    public bool Continuous { get; set; }

    public double SpeedMultiplier
    {
        get => Speed;
        set => Speed = value;
    }

    public int? TotalLoops => Continuous ? null : RepeatCount;

    public void Validate()
    {
        if (!double.IsFinite(Speed) || Speed is < MinimumSpeed or > MaximumSpeed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Speed),
                Speed,
                $"Playback speed must be between {MinimumSpeed} and {MaximumSpeed}.");
        }

        if (RepeatCount is < MinimumRepeatCount or > MaximumRepeatCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RepeatCount),
                RepeatCount,
                $"Repeat count must be between {MinimumRepeatCount} and {MaximumRepeatCount}.");
        }
    }

    public PlaybackOptions DeepClone() => new(Speed, RepeatCount, Continuous);
}

public sealed record PlannedMacroEvent(
    MacroEvent Event,
    long ScaledDelayMicroseconds,
    long ScheduledAtMicroseconds,
    int LoopNumber);

/// <summary>Timing conversion and finite/infinite loop planning for playback.</summary>
public static class PlaybackPlanner
{
    public static int? GetTotalLoopCount(PlaybackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options.Continuous ? null : options.RepeatCount;
    }

    /// <summary>
    /// Enumerates a loop lazily. Disabled events remain in the plan so their delays preserve the
    /// original timeline; an input sink should wait, then inject only when Event.Enabled is true.
    /// </summary>
    public static IEnumerable<PlannedMacroEvent> EnumerateLoop(
        MacroDocument document,
        double speed,
        int loopNumber = 1)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateSpeed(speed);
        if (loopNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(loopNumber));
        }

        var snapshot = CreateValidatedSnapshot(document, CancellationToken.None);
        return EnumerateLoopSnapshot(snapshot.Events, speed, loopNumber);
    }

    /// <summary>
    /// Enumerates every requested loop lazily. Continuous mode runs until cancellation or until the
    /// consumer stops enumeration.
    /// </summary>
    public static IEnumerable<PlannedMacroEvent> Enumerate(
        MacroDocument document,
        PlaybackOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        // Capture mutable settings and document contents once. Playback must not change when an
        // editor or settings surface mutates the caller-owned objects after enumeration begins.
        var optionsSnapshot = new PlaybackOptions(options.Speed, options.RepeatCount, options.Continuous);
        var documentSnapshot = CreateValidatedSnapshot(document, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return EnumerateSnapshot(documentSnapshot, optionsSnapshot, cancellationToken);
    }

    private static IEnumerable<PlannedMacroEvent> EnumerateSnapshot(
        MacroDocument document,
        PlaybackOptions options,
        CancellationToken cancellationToken)
    {
        var guardZeroDelayContinuousLoop = options.Continuous &&
            document.Events.Count != 0 &&
            document.Events.All(static macroEvent => macroEvent.DelayMicroseconds == 0);

        for (var loop = 1; options.Continuous || loop <= options.RepeatCount; loop++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in EnumerateLoopSnapshot(document.Events, options.Speed, loop))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;

                // A continuous all-zero-delay macro would otherwise monopolize a worker and flood
                // its consumer. Yield between events so an emergency-stop thread can run.
                if (guardZeroDelayContinuousLoop)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Thread.Yield();
                }
            }

            // An empty continuous macro must still be cancellable without overflowing immediately.
            if (options.Continuous && document.Events.Count == 0)
            {
                yield break;
            }

            if (guardZeroDelayContinuousLoop && cancellationToken.WaitHandle.WaitOne(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (loop == int.MaxValue)
            {
                loop = 0;
            }
        }
    }

    private static IEnumerable<PlannedMacroEvent> EnumerateLoopSnapshot(
        IReadOnlyList<MacroEvent> events,
        double speed,
        int loopNumber)
    {
        long previousScheduledAt = 0;
        long unscaledScheduledAt = 0;
        foreach (var macroEvent in events)
        {
            unscaledScheduledAt = checked(unscaledScheduledAt + macroEvent.DelayMicroseconds);
            var scheduledAt = ScaleDelayMicroseconds(unscaledScheduledAt, speed);
            var scaledDelay = scheduledAt - previousScheduledAt;
            previousScheduledAt = scheduledAt;
            yield return new(macroEvent, scaledDelay, scheduledAt, loopNumber);
        }
    }

    private static MacroDocument CreateValidatedSnapshot(
        MacroDocument document,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MacroValidator.Validate(document, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceEvents = document.Events.ToArray();
        var clonedEvents = new List<MacroEvent>(sourceEvents.Length);
        for (var index = 0; index < sourceEvents.Length; index++)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            clonedEvents.Add(sourceEvents[index].DeepClone());
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new MacroDocument
        {
            Format = document.Format,
            Version = document.Version,
            CreatedUtc = document.CreatedUtc,
            DisplayLayout = document.DisplayLayout!.DeepClone(),
            Events = clonedEvents,
        };
    }

    public static long ScaleDelayMicroseconds(long delayMicroseconds, double speed)
    {
        if (delayMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delayMicroseconds));
        }

        ValidateSpeed(speed);
        var result = decimal.Round((decimal)delayMicroseconds / (decimal)speed, 0, MidpointRounding.AwayFromZero);
        return result > long.MaxValue ? long.MaxValue : decimal.ToInt64(result);
    }

    public static long MicrosecondsToStopwatchTicks(
        long microseconds,
        long stopwatchFrequency = 0)
    {
        if (microseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(microseconds));
        }

        var frequency = stopwatchFrequency == 0 ? Stopwatch.Frequency : stopwatchFrequency;
        if (frequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stopwatchFrequency));
        }

        var result = decimal.Round(
            (decimal)microseconds * frequency / 1_000_000m,
            0,
            MidpointRounding.AwayFromZero);
        return result > long.MaxValue ? long.MaxValue : decimal.ToInt64(result);
    }

    public static TimeSpan GetSingleLoopDuration(MacroDocument document, double speed = 1.0)
    {
        ArgumentNullException.ThrowIfNull(document);
        MacroValidator.Validate(document);
        ValidateSpeed(speed);

        decimal totalMicroseconds = 0;
        foreach (var macroEvent in document.Events)
        {
            totalMicroseconds += macroEvent.DelayMicroseconds;
        }

        return FromScaledMicroseconds(totalMicroseconds, speed);
    }

    public static TimeSpan? GetTotalDuration(MacroDocument document, PlaybackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (options.Continuous)
        {
            return null;
        }

        var singleLoop = GetSingleLoopDuration(document, options.Speed);
        return MultiplySaturating(singleLoop, options.RepeatCount);
    }

    /// <summary>Returns null for continuous playback because it has no finite remaining estimate.</summary>
    public static TimeSpan? EstimateRemaining(
        MacroDocument document,
        PlaybackOptions options,
        int completedLoops,
        TimeSpan elapsedInCurrentLoop)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (completedLoops < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedLoops));
        }

        if (elapsedInCurrentLoop < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedInCurrentLoop));
        }

        if (options.Continuous)
        {
            return null;
        }

        if (completedLoops >= options.RepeatCount)
        {
            return TimeSpan.Zero;
        }

        var singleLoop = GetSingleLoopDuration(document, options.Speed);
        var currentLoopRemaining = singleLoop > elapsedInCurrentLoop
            ? singleLoop - elapsedInCurrentLoop
            : TimeSpan.Zero;
        var laterLoopCount = options.RepeatCount - completedLoops - 1;
        return AddSaturating(currentLoopRemaining, MultiplySaturating(singleLoop, laterLoopCount));
    }

    private static TimeSpan FromScaledMicroseconds(decimal microseconds, double speed)
    {
        var ticks = decimal.Round(microseconds * TimeSpan.TicksPerMicrosecond / (decimal)speed, 0, MidpointRounding.AwayFromZero);
        return ticks >= TimeSpan.MaxValue.Ticks
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(decimal.ToInt64(ticks));
    }

    private static TimeSpan MultiplySaturating(TimeSpan value, int multiplier)
    {
        var ticks = (decimal)value.Ticks * multiplier;
        return ticks >= TimeSpan.MaxValue.Ticks
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(decimal.ToInt64(ticks));
    }

    private static TimeSpan AddSaturating(TimeSpan left, TimeSpan right)
    {
        if (TimeSpan.MaxValue - left < right)
        {
            return TimeSpan.MaxValue;
        }

        return left + right;
    }

    private static void ValidateSpeed(double speed)
    {
        if (!double.IsFinite(speed) || speed is < PlaybackOptions.MinimumSpeed or > PlaybackOptions.MaximumSpeed)
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }
    }
}
