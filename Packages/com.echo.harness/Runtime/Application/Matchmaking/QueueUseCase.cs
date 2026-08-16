using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Contracts;

namespace Echo.Harness.Application
{
    /// <summary>
    /// Sends JoinQueueRequest and turns the answer into a
    /// <see cref="QueueOutcome"/>, and sends LeaveQueueRequest, which has no
    /// answer to turn into anything.
    ///
    /// <para><b>The exception policy is <see cref="LoginUseCase"/>'s, unchanged and
    /// for the same reason:</b> outcomes of trying to queue are converted, the
    /// system being broken is not. A timeout and a duplicate request are outcomes.
    /// A cancellation is not, and escapes. Anything else escapes too, because a
    /// broken transport dressed up as a clean refusal sends whoever debugs it to
    /// the wrong layer. The cost is the same catch-all in the calling
    /// view-model.</para>
    /// </summary>
    public sealed class QueueUseCase : IQueueUseCase
    {
        /// <summary>
        /// How long the server gets to answer a join. Matches
        /// <see cref="LoginUseCase.Deadline"/> and
        /// <c>ProtocolSession.RoundTripProbeDeadline</c>: all three time a single
        /// request waiting on one specific reply from this server.
        ///
        /// <para>It bounds the <i>acknowledgement</i>, never the wait for an
        /// opponent. Queueing has no deadline at all - the server holds a player in
        /// its FIFO until a second one arrives or the connection drops - so a
        /// timeout here means the acknowledgement was lost, not that no match was
        /// found.</para>
        /// </summary>
        public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        private readonly IProtocolSession session;

        public QueueUseCase(IProtocolSession session) =>
            this.session = session ?? throw new ArgumentNullException(nameof(session));

        /// <summary>
        /// Joins the matchmaking queue.
        ///
        /// <para><b><paramref name="playerId"/> does not identify the player to the
        /// server, and must not be mistaken for the mechanism that does.</b> The
        /// server's handler never decodes the body at all: it looks the player up
        /// by the TCP session that carried the frame
        /// (<c>playerMgr.GetBySession(s.ID)</c> in matchmaking.go handleJoinQueue,
        /// whose <c>data</c> parameter is unused). The field is populated anyway
        /// because <c>JoinQueueReq</c> declares it, and shipping a blank string
        /// when the client knows the value would be a lie on the wire that only
        /// survives while the handler keeps ignoring it. A blank one is accepted
        /// here rather than refused, precisely because the server does not care -
        /// refusing would invent a client-side requirement the protocol does not
        /// have.</para>
        /// </summary>
        public async UniTask<QueueOutcome> JoinAsync(
            string playerId,
            CancellationToken cancellationToken)
        {
            JoinQueueResponseDto response;
            try
            {
                response = await session.RequestAsync<JoinQueueResponseDto>(
                    MessageId.JoinQueueRequest,
                    new JoinQueueRequestDto { PlayerId = playerId ?? string.Empty },
                    Deadline,
                    cancellationToken);
            }
            catch (TimeoutException)
            {
                return QueueOutcome.NoReply(
                    $"The server did not answer within {Deadline}.");
            }
            catch (RequestAlreadyInFlightException)
            {
                return QueueOutcome.NoReply("A queue request is already in flight.");
            }

            return response.Success
                ? QueueOutcome.Joined()
                : QueueOutcome.Refusal(
                    string.IsNullOrWhiteSpace(response.Error)
                        ? "The server refused the queue request without saying why."
                        : response.Error);
        }

        /// <summary>
        /// Asks to leave the queue, and returns once the request has been written.
        ///
        /// <para><b>Two things a caller must not conclude from this completing.</b>
        /// First, that the server acted: 2003 carries no reply, so there is nothing
        /// to wait for and nothing to check. Second, that no match will arrive: the
        /// server pairs players under the queue's own mutex the moment a second one
        /// enqueues (matchmaking.go tryMatch), so a MatchFoundEvent can already be
        /// on the wire when this request is sent, and it will still be delivered.
        /// A UI that treats leaving as final and ignores a later match will strand
        /// the player in a game the server believes they are in. <b>The match wins;
        /// the cancellation is the thing that lost the race.</b></para>
        ///
        /// <para>Nothing is converted here. There is no reply, so there is no
        /// timeout to turn into an outcome and no refusal to report - only the send
        /// itself can fail, and a failing send is the system being broken rather
        /// than an outcome of leaving. It escapes, per this class's policy.</para>
        ///
        /// <para>The payload is <c>null</c> because 2003 is payload-shape "none":
        /// it has no Go struct, it is absent from
        /// <c>ProtocolMessageMap.PayloadTypes</c>, and <c>ProtocolSession.SendAsync</c>
        /// requires null for exactly that set. Passing a DTO would throw
        /// <c>ArgumentException</c> before a byte left.</para>
        /// </summary>
        public UniTask LeaveAsync(CancellationToken cancellationToken) =>
            session.SendAsync(MessageId.LeaveQueueRequest, null, cancellationToken);
    }
}
