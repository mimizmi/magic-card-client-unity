using System;
using Echo.Harness.Infrastructure;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class SendBudgetTests
    {
        [Test]
        public void ABudgetOfThirtyAllowsThirtyThenRefuses()
        {
            var budget = new SendBudget(30, new ManualTime(DateTimeOffset.UnixEpoch));

            for (var i = 0; i < 30; i++)
            {
                Assert.That(budget.TryConsume(), Is.True, $"Send {i + 1} of 30.");
            }

            Assert.That(budget.TryConsume(), Is.False,
                "The server's limit is 30 per second and it disconnects silently.");
        }

        [Test]
        public void TokensRefillOverTime()
        {
            var clock = new ManualTime(DateTimeOffset.UnixEpoch);
            var budget = new SendBudget(30, clock);
            for (var i = 0; i < 30; i++)
            {
                budget.TryConsume();
            }

            // One interval is a thirtieth of a second, matching the server's own
            // rateLimitRefillInterval.
            clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30));

            Assert.That(budget.TryConsume(), Is.True);
            Assert.That(budget.TryConsume(), Is.False, "Exactly one token refilled.");
        }

        [Test]
        public void RefillNeverExceedsTheMaximum()
        {
            var clock = new ManualTime(DateTimeOffset.UnixEpoch);
            var budget = new SendBudget(30, clock);
            clock.Advance(TimeSpan.FromMinutes(5));

            for (var i = 0; i < 30; i++)
            {
                Assert.That(budget.TryConsume(), Is.True);
            }

            Assert.That(budget.TryConsume(), Is.False,
                "A long idle period must not build a burst the server will reject.");
        }

        // The remainder invariant, pinned. With a 30/s budget the refill interval
        // is 1/30 s. Advancing by 1.5 intervals nine times must yield 13 whole
        // intervals of refill (13.5 truncated), not 9 - which is what a
        // mark-set-to-now implementation gives, because it discards the
        // half-interval remainder on every call.
        [Test]
        public void TryConsume_CarriesTheFractionalRemainderForward()
        {
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var budget = new SendBudget(30, time);

            for (var i = 0; i < 30; i++)
            {
                Assert.That(budget.TryConsume(), Is.True, $"token {i} should have been available");
            }

            Assert.That(budget.TryConsume(), Is.False, "the bucket should be empty");

            var oneAndAHalfIntervals = TimeSpan.FromTicks(
                (TimeSpan.TicksPerSecond / 30) * 3 / 2);

            var granted = 0;
            for (var i = 0; i < 9; i++)
            {
                time.Advance(oneAndAHalfIntervals);
                while (budget.TryConsume())
                {
                    granted++;
                }
            }

            Assert.That(granted, Is.EqualTo(13));
        }

        [Test]
        public void ANonPositiveBudgetIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SendBudget(0, new ManualTime(DateTimeOffset.UnixEpoch)));
        }

        [Test]
        public void ARateFinerThanOneTickPerMessageIsRejected()
        {
            // One tick per message is the finest interval that can be measured, so
            // it is allowed and the next value up is not. Rejected in the
            // constructor rather than left to TryConsume, where the truncated
            // interval would be zero and the refill would divide by it - surfacing
            // as a DivideByZeroException on the first send, naming nothing that
            // led to it.
            Assert.DoesNotThrow(() => new SendBudget(
                (int)TimeSpan.TicksPerSecond, new ManualTime(DateTimeOffset.UnixEpoch)));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SendBudget(
                    (int)TimeSpan.TicksPerSecond + 1,
                    new ManualTime(DateTimeOffset.UnixEpoch)));
        }

        [Test]
        public void ANullClockIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new SendBudget(30, null));
        }
    }
}
