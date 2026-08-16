using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;

namespace Echo.Harness.TestKit
{
    /// <summary>
    /// Completes synchronously, so a view-model test may use the
    /// <c>[Test]</c> + <c>GetAwaiter().GetResult()</c> pattern the rest of the
    /// suite uses. Shaped after <see cref="FakeLoginUseCase"/>.
    ///
    /// <para>There is deliberately no fake <see cref="ICurrentPlayer"/>. The real
    /// <see cref="CurrentPlayer"/> holds one string and has no collaborators, so a
    /// double would only restate it - and a test that set a fake's
    /// <c>IsLoggedIn</c> directly could describe a state the real type cannot
    /// reach, since its own <c>IsLoggedIn</c> is derived from the id rather than
    /// stored beside it.</para>
    /// </summary>
    public sealed class FakeQueueUseCase : IQueueUseCase
    {
        public QueueOutcome NextOutcome { get; set; } = QueueOutcome.Joined();

        /// <summary>One-shot, and it wins over <see cref="NextOutcome"/>.</summary>
        public Exception NextJoinFailure { get; set; }

        /// <summary>One-shot. Leaving has no outcome, so a throw is all it can do.</summary>
        public Exception NextLeaveFailure { get; set; }

        public int JoinCount { get; private set; }

        public int LeaveCount { get; private set; }

        public string LastPlayerId { get; private set; }

        /// <summary>
        /// Makes the next join park instead of completing, so a test can make
        /// something else happen <i>while the request is in flight</i>. Without it
        /// every await here resumes before the next statement runs, and the whole
        /// class of "the server pushed an event before it answered" is
        /// unreachable - which is exactly the race the queue flow is shaped
        /// around. Modelled on the parking transport double the transport
        /// iteration needed for the same reason.
        /// </summary>
        public void ParkNextJoin() => parkedJoin = new UniTaskCompletionSource<QueueOutcome>();

        /// <summary>Releases a parked join with <see cref="NextOutcome"/>.</summary>
        public void CompleteParkedJoin()
        {
            var parked = parkedJoin
                ?? throw new InvalidOperationException("No join is parked.");
            parkedJoin = null;
            parked.TrySetResult(NextOutcome);
        }

        private UniTaskCompletionSource<QueueOutcome> parkedJoin;

        public UniTask<QueueOutcome> JoinAsync(string playerId, CancellationToken cancellationToken)
        {
            JoinCount++;
            LastPlayerId = playerId;

            if (NextJoinFailure != null)
            {
                var failure = NextJoinFailure;
                NextJoinFailure = null;
                return UniTask.FromException<QueueOutcome>(failure);
            }

            if (parkedJoin != null)
            {
                return parkedJoin.Task;
            }

            return UniTask.FromResult(NextOutcome);
        }

        public UniTask LeaveAsync(CancellationToken cancellationToken)
        {
            LeaveCount++;

            if (NextLeaveFailure != null)
            {
                var failure = NextLeaveFailure;
                NextLeaveFailure = null;
                return UniTask.FromException(failure);
            }

            return UniTask.CompletedTask;
        }
    }
}
