using System.Threading;
using Cysharp.Threading.Tasks;

namespace Echo.Harness.Application
{
    /// <summary>
    /// Joining and leaving the matchmaking queue. The port exists for the reason
    /// <see cref="ILoginUseCase"/> gives: <c>Echo.Harness.Presentation</c> depends
    /// on an interface rather than on the concrete use case, so a view-model test
    /// needs no session at all.
    ///
    /// <para><b>The two halves are deliberately asymmetric, because the protocol
    /// is.</b> 2001 JoinQueueRequest is answered by 2002 JoinQueueResponse, so
    /// joining has an outcome. 2003 LeaveQueueRequest is answered by nothing at
    /// all - the server's handler dequeues and returns without writing
    /// (matchmaking.go handleLeaveQueue) - so leaving cannot have one. Inventing a
    /// <c>bool</c> or an outcome type for <see cref="LeaveAsync"/> would report
    /// "the bytes were handed to a socket" as "the player is out of the queue",
    /// and those are not the same claim.</para>
    /// </summary>
    public interface IQueueUseCase
    {
        UniTask<QueueOutcome> JoinAsync(string playerId, CancellationToken cancellationToken);

        /// <summary>
        /// Asks to leave the queue. <b>Completing proves only that the request was
        /// written.</b> See the type summary for why no acknowledgement exists, and
        /// <see cref="QueueUseCase.LeaveAsync"/> for the race a caller inherits.
        /// </summary>
        UniTask LeaveAsync(CancellationToken cancellationToken);
    }
}
