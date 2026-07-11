using System.Diagnostics;
namespace Genesis.Governance;
public static class StopwatchTicks
{
    public static long ToNanoseconds(long ticks)
    {
        if (ticks < 0) throw new ArgumentOutOfRangeException(nameof(ticks));
        var seconds = ticks / Stopwatch.Frequency;
        var remainder = ticks % Stopwatch.Frequency;
        return checked(seconds * 1_000_000_000L +
            (long)((decimal)remainder * 1_000_000_000m / Stopwatch.Frequency));
    }
}
