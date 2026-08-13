using System;
using System.Diagnostics;
using System.Threading;
using Echo.Harness.Infrastructure;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class SystemTimeTests
    {
        [Test]
        public void StopwatchElapsedTime_NeverReportsANegativeInterval()
        {
            var time = new StopwatchElapsedTime();

            var start = time.GetTimestamp();
            Thread.Sleep(5);
            var elapsed = time.GetElapsedTime(start);

            Assert.That(elapsed, Is.GreaterThan(TimeSpan.Zero));
        }

        [Test]
        public void StopwatchElapsedTime_TimestampsAreNonDecreasing()
        {
            var time = new StopwatchElapsedTime();

            var first = time.GetTimestamp();
            Thread.Sleep(5);
            var second = time.GetTimestamp();

            Assert.That(second, Is.GreaterThanOrEqualTo(first));
        }

        // Synthesizes a start timestamp exactly Stopwatch.Frequency counter units
        // in the past, which is one second by the definition of that constant, and
        // asserts the conversion turns it back into one second. Deterministic, and
        // it needs no sleep - the previous version of this test slept 50 ms and
        // asserted only >20 ms and <5 s, bounds loose enough to tolerate a 2.5x
        // undercount or a 100x overcount.
        //
        // What it can and cannot catch, stated rather than assumed. Where
        // Stopwatch.Frequency differs from TimeSpan.TicksPerSecond, dropping the
        // conversion or inverting it moves the result by that whole ratio and this
        // assertion fails. Where the two are EQUAL - both are 10,000,000 on this
        // machine - TicksPerCounterUnit is exactly 1.0, so multiplying by it,
        // dividing by it and omitting it are the same computation, and no test of
        // this implementation can tell them apart. That is a property of the
        // platform, not a gap this test can close; see the mutation probe recorded
        // in the task report.
        [Test]
        public void StopwatchElapsedTime_ConvertsFrequencyToWallDuration()
        {
            var time = new StopwatchElapsedTime();

            var start = time.GetTimestamp() - Stopwatch.Frequency;
            var elapsed = time.GetElapsedTime(start);

            Assert.That(
                elapsed,
                Is.EqualTo(TimeSpan.FromSeconds(1)).Within(TimeSpan.FromMilliseconds(50)));
        }

        [Test]
        public void SystemClock_ReportsAPlausibleWallTime()
        {
            var clock = new SystemClock();

            Assert.That(
                clock.UtcNow,
                Is.GreaterThan(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        }
    }
}
