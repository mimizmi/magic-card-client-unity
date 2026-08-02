using System;
using Echo.Harness.Application;
using Echo.Harness.Contracts;

namespace Echo.Harness.Infrastructure
{
    /// <summary>
    /// A token bucket shaped like the server's own: a maximum, a refill interval of
    /// one second divided by that maximum, and a cap so an idle period cannot build
    /// a burst. Driven by IElapsedTime, so a test can exhaust and refill it without
    /// sleeping, and so a wall-clock step cannot wedge it at zero.
    ///
    /// <para><b>Not thread-safe.</b> TryConsume is a read-modify-write over
    /// <c>tokens</c>, <c>lastFillTimestamp</c> and <c>carryTicks</c> with no
    /// synchronization of its own. Its
    /// only caller holds TcpTransport's send gate across the call, which is what
    /// makes it safe there and is also what keeps tokens in the same order as the
    /// bytes they account for. A second caller must supply its own exclusion.</para>
    /// </summary>
    public sealed class SendBudget
    {
        private readonly int max;
        private readonly long refillIntervalTicks;
        private readonly IElapsedTime time;
        private int tokens;
        private long lastFillTimestamp;

        // The remainder is carried here rather than by advancing the timestamp,
        // because a timestamp's unit is opaque: IElapsedTime exposes no frequency,
        // so there is no way to add "n intervals" to one. This holds what a refill
        // did not consume, preserving the property the DateTimeOffset version got
        // from lastFill.AddTicks.
        private long carryTicks;

        public SendBudget(int perSecond, IElapsedTime time)
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

            this.time = time ?? throw new ArgumentNullException(nameof(time));
            max = perSecond;
            tokens = perSecond;
            refillIntervalTicks = TimeSpan.TicksPerSecond / perSecond;
            lastFillTimestamp = time.GetTimestamp();
        }

        public bool TryConsume()
        {
            var now = time.GetTimestamp();
            var elapsedTicks = time.GetElapsedTime(lastFillTimestamp).Ticks + carryTicks;
            if (elapsedTicks >= refillIntervalTicks)
            {
                var refill = elapsedTicks / refillIntervalTicks;
                tokens = (int)Math.Min(max, tokens + refill);
                carryTicks = elapsedTicks - (refill * refillIntervalTicks);
            }
            else
            {
                carryTicks = elapsedTicks;
            }

            lastFillTimestamp = now;

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
