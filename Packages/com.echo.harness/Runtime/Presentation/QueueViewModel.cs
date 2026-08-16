using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Unity.Properties;
using UnityEngine.UIElements;

namespace Echo.Harness.Presentation
{
    /// <summary>
    /// The queue panel's state. Like <see cref="LoginViewModel"/> it names UI
    /// Toolkit's binding contract and no widget, so the whole of it is testable in
    /// EditMode with no player loop.
    ///
    /// <para><b>Two texts rather than one, for the reason
    /// <see cref="LoginViewModel"/> keeps two.</b> <see cref="QueueStatusText"/> is
    /// what this client asked for and what the server said about it;
    /// <see cref="MatchText"/> is a fact the server pushed, unprompted and possibly
    /// while the player was doing something else. One field would let whichever
    /// landed second erase the other, and the pairing that matters most - "you
    /// cancelled, but a match had already been made" - is exactly the one where
    /// both halves have to stay readable at once.</para>
    /// </summary>
    public sealed class QueueViewModel : INotifyBindablePropertyChanged, IDisposable
    {
        private readonly ISessionStatus status;
        private readonly ICurrentPlayer player;
        private readonly IQueueUseCase queue;
        private readonly IDisposable matchSubscription;

        private string queueStatusText = "Not in queue.";
        private string matchText = string.Empty;
        private bool queued;
        private bool busy;
        private bool matched;
        private SessionState lastSeenState = (SessionState)(-1);

        public QueueViewModel(
            ISessionStatus status,
            ICurrentPlayer player,
            IQueueUseCase queue,
            MatchFoundWatcher matches)
        {
            this.status = status ?? throw new ArgumentNullException(nameof(status));
            this.player = player ?? throw new ArgumentNullException(nameof(player));
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));

            if (matches == null)
            {
                throw new ArgumentNullException(nameof(matches));
            }

            // Subscribing here also collects a match that arrived BEFORE this
            // view-model existed - the watcher replays its latest on subscribe.
            // That is not a nicety: on the server's reconnect path the match lands
            // during login, which is before any screen has been built.
            matchSubscription = matches.Observe(OnMatchFound);

            Refresh();
        }

        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

        /// <summary>
        /// <b>Being logged in is checked here and enforced by the server.</b>
        /// <see cref="ICurrentPlayer.IsLoggedIn"/> is a claim about this process
        /// and can be stale - see that type - so this gate is what a user sees,
        /// and the server's own "not logged in" refusal is what actually holds.
        /// </summary>
        [CreateProperty]
        public bool CanJoin =>
            status.State == SessionState.Connected
            && player.IsLoggedIn
            && !busy
            && !queued
            && !matched;

        /// <summary>
        /// False once matched, because there is nothing left to cancel: the server
        /// has already taken both players out of the queue and made a room, and
        /// LeaveQueueRequest against a player who is no longer queued is a no-op it
        /// will not answer.
        /// </summary>
        [CreateProperty]
        public bool CanLeave =>
            status.State == SessionState.Connected
            && queued
            && !busy
            && !matched;

        [CreateProperty]
        public bool IsQueued => queued;

        [CreateProperty]
        public bool IsBusy => busy;

        [CreateProperty]
        public bool IsMatched => matched;

        [CreateProperty]
        public string QueueStatusText => queueStatusText;

        [CreateProperty]
        public string MatchText => matchText;

        /// <summary>
        /// Re-reads the session state. Polling for the reason
        /// <see cref="LoginViewModel.Refresh"/> gives: <see cref="ISessionStatus"/>
        /// exposes no change event because <c>IProtocolSession</c> has none.
        /// Called once a frame by the view, and it raises nothing unless something
        /// moved.
        /// </summary>
        public void Refresh()
        {
            var current = status.State;
            if (current == lastSeenState)
            {
                return;
            }

            lastSeenState = current;
            Notify(nameof(CanJoin));
            Notify(nameof(CanLeave));
        }

        /// <summary>
        /// The guard is the same shape as <see cref="LeaveAsync"/>'s, and both are
        /// belt to <see cref="CanJoin"/>'s braces: the bindable property is what
        /// disables the button, this is what holds when the method is reached
        /// anyway - a second click inside one frame, or a caller that is not the
        /// view. Joining twice is not a protocol error (the server's Enqueue
        /// ignores a duplicate) but it does spend a request that the single-flight
        /// gate could then refuse to a caller who needed it.
        /// </summary>
        public async UniTask JoinAsync()
        {
            if (busy || queued || matched)
            {
                return;
            }

            SetBusy(true);
            try
            {
                var outcome = await queue.JoinAsync(player.PlayerId, CancellationToken.None);

                // A match can arrive while the join is in flight - the server pairs
                // under its queue mutex the moment a second player enqueues, and
                // the MatchFoundEvent is written independently of the response. If
                // that happened, saying "searching for an opponent" here would
                // overwrite the truth with a stale intention.
                if (matched)
                {
                    return;
                }

                queued = outcome.Result == QueueResult.Joined;
                queueStatusText = Describe(outcome);
            }
            catch (OperationCanceledException)
            {
                // Unreachable today for the reason LoginViewModel.SubmitAsync
                // states: this passes CancellationToken.None. Kept for the same
                // caller, so that a cancelled queue reads as "the screen is going
                // away" rather than as a refusal the player might act on.
                queued = false;
                queueStatusText = "Not in queue.";
            }
            catch (Exception failure)
            {
                // QueueUseCase lets a genuinely broken transport escape, and this is
                // where it lands. Without this catch it reaches UniTask's
                // unobserved handler and the button appears inert.
                queued = false;
                queueStatusText =
                    $"Could not join the queue: {failure.GetType().Name}: {failure.Message}";
            }
            finally
            {
                SetBusy(false);
                Notify(nameof(QueueStatusText));
                Notify(nameof(IsQueued));
                Notify(nameof(CanJoin));
                Notify(nameof(CanLeave));
            }
        }

        /// <summary>
        /// <b>Optimistic, and it has to be.</b> LeaveQueueRequest carries no reply,
        /// so this clears the queued flag on the strength of the request having
        /// been written - see <c>IQueueUseCase.LeaveAsync</c>. What that buys is
        /// small and the alternative is worse: a button that stays lit waiting for
        /// an acknowledgement that never comes.
        ///
        /// <para>What it does NOT do is close the door on a match. If one was
        /// already in flight when the player cancelled, it still arrives and
        /// <see cref="OnMatchFound"/> still reports it. The player loses that race,
        /// and the screen must say so rather than quietly stay in a lobby the
        /// server has already moved them out of.</para>
        /// </summary>
        public async UniTask LeaveAsync()
        {
            if (busy || !queued)
            {
                return;
            }

            SetBusy(true);
            try
            {
                await queue.LeaveAsync(CancellationToken.None);

                if (!matched)
                {
                    queued = false;
                    queueStatusText = "Not in queue.";
                }
            }
            catch (OperationCanceledException)
            {
                queued = false;
                queueStatusText = "Not in queue.";
            }
            catch (Exception failure)
            {
                // The queued flag is deliberately left alone. The request may or
                // may not have reached the server, so claiming either state would
                // be a guess; saying what happened lets the player try again.
                queueStatusText =
                    $"Could not leave the queue: {failure.GetType().Name}: {failure.Message}";
            }
            finally
            {
                SetBusy(false);
                Notify(nameof(QueueStatusText));
                Notify(nameof(IsQueued));
                Notify(nameof(CanJoin));
                Notify(nameof(CanLeave));
            }
        }

        public void Dispose() => matchSubscription.Dispose();

        /// <summary>
        /// Delivered on the session context by <c>ProtocolSession</c>'s pump, which
        /// hops once before dispatching, so touching bindable state here is safe.
        /// </summary>
        private void OnMatchFound(MatchFound match)
        {
            matched = true;
            queued = false;
            matchText =
                $"Match found against {match.OpponentName}. " +
                $"You are seat {match.Seat} in {match.GameId}.";

            Notify(nameof(MatchText));
            Notify(nameof(IsMatched));
            Notify(nameof(IsQueued));
            Notify(nameof(CanJoin));
            Notify(nameof(CanLeave));
        }

        private void SetBusy(bool value)
        {
            if (busy == value)
            {
                return;
            }

            busy = value;
            Notify(nameof(IsBusy));
            Notify(nameof(CanJoin));
            Notify(nameof(CanLeave));
        }

        private void Notify(string property) =>
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(property));

        private static string Describe(QueueOutcome outcome)
        {
            switch (outcome.Result)
            {
                case QueueResult.Joined:
                    return "Searching for an opponent…";
                case QueueResult.Rejected:
                    return $"The server refused the queue request: {outcome.Message}";
                default:
                    return $"No answer from the server: {outcome.Message}";
            }
        }
    }
}
