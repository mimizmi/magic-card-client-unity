using System;
using System.Diagnostics;
using Echo.Harness.Application;

namespace Echo.Harness.Infrastructure
{
    /// <summary>
    /// Wall-clock time from the operating system. Moved here from TestKit, which
    /// carries defineConstraints ["UNITY_INCLUDE_TESTS"] and therefore could not
    /// ship in a player build - the whole reason none of this stack was
    /// constructible.
    /// </summary>
    public sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Monotonic elapsed time from <see cref="Stopwatch"/>, which is backed by the
    /// platform's high-resolution performance counter and is unaffected by clock
    /// synchronisation.
    ///
    /// <para>The conversion is written out because the static
    /// <c>Stopwatch.GetElapsedTime</c> is .NET 7+ and this project targets
    /// .NET Standard 2.1. The multiplication happens before the division and in
    /// <see cref="double"/>, because <c>Stopwatch.Frequency</c> is 10,000,000 on
    /// Windows and differs elsewhere: dividing first in integer arithmetic would
    /// truncate every interval shorter than one whole unit to zero.</para>
    /// </summary>
    public sealed class StopwatchElapsedTime : IElapsedTime
    {
        private static readonly double TicksPerCounterUnit =
            (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

        public long GetTimestamp() => Stopwatch.GetTimestamp();

        public TimeSpan GetElapsedTime(long startingTimestamp)
        {
            var counterUnits = Stopwatch.GetTimestamp() - startingTimestamp;
            return new TimeSpan((long)(counterUnits * TicksPerCounterUnit));
        }
    }
}
