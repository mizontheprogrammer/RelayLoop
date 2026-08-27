using System.Diagnostics;

namespace RelayLoop.App.Services;

public interface IMonotonicClock
{
    long Frequency { get; }

    long GetTimestamp();
}

public sealed class StopwatchMonotonicClock : IMonotonicClock
{
    public long Frequency => Stopwatch.Frequency;

    public long GetTimestamp() => Stopwatch.GetTimestamp();
}

internal static class MonotonicTime
{
    public static long GetElapsedMicroseconds(long start, long end, long frequency)
    {
        if (end <= start)
        {
            return 0;
        }

        var ticks = end - start;
        var wholeSeconds = ticks / frequency;
        var remainder = ticks % frequency;
        return checked((wholeSeconds * 1_000_000L) + ((remainder * 1_000_000L) / frequency));
    }
}
