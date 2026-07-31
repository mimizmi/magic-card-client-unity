using System;
using Echo.Harness.Application;
using Echo.Harness.Contracts;

namespace Echo.Harness.Infrastructure
{
    /// <summary>
    /// A token bucket shaped like the server's own: a maximum, a refill interval of
    /// one second divided by that maximum, and a cap so an idle period cannot build
    /// a burst. Driven by IClock rather than the wall clock so a test can exhaust
    /// and refill it without sleeping.
    ///
    /// <para><b>Not thread-safe.</b> TryConsume is a read-modify-write over
    /// <c>tokens</c> and <c>lastFill</c> with no synchronization of its own. Its
    /// only caller holds TcpTransport's send gate across the call, which is what
    /// makes it safe there and is also what keeps tokens in the same order as the
    /// bytes they account for. A second caller must supply its own exclusion.</para>
    /// </summary>
    public sealed class SendBudget
    {
        private readonly int max;
        private readonly long refillIntervalTicks;
        private readonly IClock clock;
        private int tokens;
        private DateTimeOffset lastFill;

        public SendBudget(int perSecond, IClock clock)
        {
            if (perSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(perSecond), perSecond, "A send budget must be positive.");
            }

            // Above one tick per message the refill interval truncates to zero and
            // TryConsume divides by it. Rejected here rather than left to fail at
            // the first send, where the DivideByZeroException would surface as a
            // transport fault naming nothing that led to it.
            if (perSecond > TimeSpan.TicksPerSecond)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(perSecond),
                    perSecond,
                    $"A send budget above {TimeSpan.TicksPerSecond} per second has " +
                    "a refill interval of less than one tick and cannot be measured.");
            }

            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            max = perSecond;
            tokens = perSecond;
            refillIntervalTicks = TimeSpan.TicksPerSecond / perSecond;
            lastFill = clock.UtcNow;
        }

        public bool TryConsume()
        {
            var elapsedTicks = (clock.UtcNow - lastFill).Ticks;
            if (elapsedTicks >= refillIntervalTicks)
            {
                var refill = elapsedTicks / refillIntervalTicks;
                tokens = (int)Math.Min(max, tokens + refill);

                // Advanced by whole intervals only, so a fractional remainder
                // carries forward instead of being discarded. Setting lastFill to
                // now would lose it and make the effective rate lower than asked.
                lastFill = lastFill.AddTicks(refill * refillIntervalTicks);
            }

            if (tokens <= 0)
            {
                return false;
            }

            tokens--;
            return true;
        }
    }

    /// <summary>
    /// The caller sent faster than the server tolerates. Its own type, and not a
    /// session fault, because this is the one failure that is the caller's defect
    /// rather than the link's: the connection is still fine.
    /// </summary>
    public sealed class SendBudgetExceededException : InvalidOperationException
    {
        public SendBudgetExceededException(MessageId messageId, string message)
            : base(message)
        {
            MessageId = messageId;
        }

        public MessageId MessageId { get; }
    }
}
