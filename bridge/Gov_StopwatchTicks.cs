using System.Diagnostics;

namespace Genesis.Governance;

public static class StopwatchTicks
{
    public static long ToNanoseconds(long ticks)
    {
        if (ticks < 0)
            throw new ArgumentOutOfRangeException(nameof(ticks));

        // Quotient/remainder form avoids overflowing ticks * 1_000_000_000.
        var wholeSeconds = ticks / Stopwatch.Frequency;
        var remainder = ticks % Stopwatch.Frequency;

        return checked(
            wholeSeconds * 1_000_000_000L +
            (long)((decimal)remainder * 1_000_000_000m / Stopwatch.Frequency));
    }
}
