using System;
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

        // The conversion is written out rather than delegated to
        // Stopwatch.GetElapsedTime, which is .NET 7+ and not available here. This
        // pins the arithmetic: a frequency division dropped or inverted makes the
        // reported interval wrong by orders of magnitude while both tests above
        // still pass.
        [Test]
        public void StopwatchElapsedTime_ConvertsFrequencyToWallDuration()
        {
            var time = new StopwatchElapsedTime();

            var start = time.GetTimestamp();
            Thread.Sleep(50);
            var elapsed = time.GetElapsedTime(start);

            Assert.That(elapsed, Is.GreaterThan(TimeSpan.FromMilliseconds(20)));
            Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
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
