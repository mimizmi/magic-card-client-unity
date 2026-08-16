using System;
using System.Collections.Generic;
using Echo.Harness.Contracts;

namespace Echo.Harness.Application
{
    /// <summary>
    /// The one production subscriber to 2004 MatchFoundEvent. It converts the wire
    /// DTO into a <see cref="MatchFound"/> that Presentation is allowed to name,
    /// and hands it to whoever is watching.
    ///
    /// <para><b>It subscribes in its constructor, and that timing is the whole
    /// point.</b> <see cref="IProtocolSession.Subscribe{TPayload}"/> says it in the
    /// general case - "subscribe before the request that provokes the message" -
    /// but 2004 is the case that makes it concrete, and worse than the general
    /// warning suggests. The provoking request is not JoinQueueRequest. On the
    /// server's reconnect path a successful LoginResponse is followed immediately
    /// by a MatchFoundEvent for the game already in progress (matchmaking.go
    /// handleLogin, path A), both written to the same connection back to back. So
    /// a subscription registered after login returns can miss the event that says
    /// the player is already in a game - which is the one case where missing it
    /// strands them.</para>
    ///
    /// <para>Like <see cref="SessionFaultRouter"/>, this type must therefore be
    /// RESOLVED and not merely registered; see <c>MatchFoundWatcherEntryPoint</c>
    /// for what forces that and why the watcher cannot force it itself.</para>
    ///
    /// <para><b><see cref="Latest"/> exists so that being early is enough.</b> A
    /// view-model built after the event arrives - which the reconnect path makes
    /// ordinary rather than exotic - would otherwise watch a match that has already
    /// happened and see nothing. Every new observer is handed the latest match
    /// immediately if there is one, so a watcher constructed before login covers
    /// every consumer constructed after it. It is a replay of one and not a log:
    /// a second match supersedes the first, because a player is in at most one
    /// game and the older pairing is not something anyone should still act on.</para>
    ///
    /// <para><b>No scheduler, and that is a difference from
    /// <see cref="SessionFaultRouter"/> rather than an omission.</b> The router
    /// hops because faults are published from the pump, from a timer, and from a
    /// caller's own frame. Subscriber dispatch has no such spread:
    /// <c>ProtocolSession</c>'s pump takes the hop to the session context once,
    /// before <c>Dispatch</c>, so every handler here already runs on the context a
    /// UI needs. Adding a hop would cost a frame and buy nothing.</para>
    /// </summary>
    public sealed class MatchFoundWatcher : IDisposable
    {
        private readonly IDisposable subscription;
        private readonly List<Action<MatchFound>> observers = new List<Action<MatchFound>>();
        private bool disposed;

        public MatchFoundWatcher(IProtocolSession session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            subscription = session.Subscribe<MatchFoundEventDto>(
                MessageId.MatchFoundEvent, OnMatchFound);
        }

        /// <summary>
        /// The most recent match, or null if none has arrived. Null rather than a
        /// default <see cref="MatchFound"/> because seat 0 is a real seat, so a
        /// zeroed struct is indistinguishable from a genuine "you are seat 0"
        /// pairing.
        /// </summary>
        public MatchFound? Latest { get; private set; }

        /// <summary>
        /// Watches for a match. If one has already been found, the handler is
        /// called with it before this method returns - see the type summary for
        /// why that replay is the reason this class holds state at all.
        /// </summary>
        public IDisposable Observe(Action<MatchFound> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            lock (observers)
            {
                observers.Add(handler);
            }

            // Outside the lock. A handler is caller code that may do anything,
            // including subscribing again or disposing this watcher, and running
            // it while holding the lock that its own re-entry would take is the
            // ordinary way to deadlock.
            var replay = Latest;
            if (replay.HasValue)
            {
                handler(replay.Value);
            }

            return new Unsubscribe(() =>
            {
                lock (observers)
                {
                    observers.Remove(handler);
                }
            });
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            subscription.Dispose();

            lock (observers)
            {
                observers.Clear();
            }
        }

        private void OnMatchFound(MatchFoundEventDto dto)
        {
            // Delivered on the session context by ProtocolSession's pump, which
            // hops once before Dispatch. Touching state here needs no lock against
            // the pump; the list is locked only against Observe and Dispose, which
            // a view-model may call from its own frame.
            var match = new MatchFound(dto.GameId, dto.YourSeat, dto.OpponentName);
            Latest = match;

            Action<MatchFound>[] snapshot;
            lock (observers)
            {
                snapshot = observers.ToArray();
            }

            foreach (var observer in snapshot)
            {
                try
                {
                    observer(match);
                }
                catch
                {
                    // One broken observer must not deny the others the match.
                    // Nothing is published in its place: this runs inside
                    // ProtocolSession's Dispatch, which already converts an
                    // escaping handler exception into a DispatchFailure fault, so
                    // swallowing here trades that one report for the other
                    // observers still being told. The trade is deliberate - a match
                    // reaching three screens out of four beats it reaching none.
                }
            }
        }

        private sealed class Unsubscribe : IDisposable
        {
            private readonly Action action;

            public Unsubscribe(Action action) => this.action = action;

            public void Dispose() => action();
        }
    }
}
