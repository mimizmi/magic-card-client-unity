using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Contracts;

namespace Echo.Harness.Application
{
    /// <summary>
    /// The one production subscriber to <c>IProtocolSession.SubscribeToFaults</c>.
    /// Before this existed, <c>ProtocolSession.PublishFault</c> iterated an empty
    /// list and all seven fault kinds were produced and never read.
    ///
    /// <para><b>The two halves have different threading on purpose, and that is
    /// the design rather than an inconsistency.</b> Logging is synchronous, on
    /// whichever thread published the fault. That is safe only because
    /// <see cref="IFaultLog"/>'s implementation is relied upon, not documented,
    /// to serialise its calls to <c>Debug</c> internally - Unity documents
    /// nothing about which threads may call it. Fault logs matter most on the
    /// shutdown path - the one <c>HarnessSessionDriver</c> documents as having
    /// no further player-loop tick, where anything that hopped first would
    /// never be emitted at all. UI delivery does hop, through
    /// <see cref="ISessionScheduler"/>, because a handler that touches UI on a
    /// pool thread is a crash; that is the review finding this class closes.</para>
    ///
    /// <para><b>Nothing here may rely on an exception escaping.</b>
    /// <c>ProtocolSession.PublishFault</c> catches everything a handler throws and
    /// says so: "there is nowhere left to report it". So the delivery half carries
    /// its own catch and writes what happened to the log, which is the only
    /// surface left.</para>
    /// </summary>
    public sealed class SessionFaultRouter : IDisposable
    {
        private readonly ISessionScheduler scheduler;
        private readonly IFaultLog log;
        private readonly IDisposable subscription;
        private readonly HashSet<MessageId> reportedNoDestination = new HashSet<MessageId>();
        private readonly List<Action<SessionFault>> connectionObservers =
            new List<Action<SessionFault>>();
        private bool disposed;

        public SessionFaultRouter(
            IProtocolSession session,
            ISessionScheduler scheduler,
            IFaultLog log)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            this.log = log ?? throw new ArgumentNullException(nameof(log));

            // Subscribing in the constructor is why this type must be RESOLVED and
            // not merely registered. See SessionFaultRouterEntryPoint.
            subscription = session.SubscribeToFaults(OnFault);
        }

        /// <summary>
        /// Faults a user could act on: the link is gone or a message could not be
        /// dispatched. Delivered on the session context, so a handler may touch UI.
        /// The other five kinds are logged and stop there - there is no interface
        /// element that could express them while a login screen is the only screen.
        /// </summary>
        public IDisposable ObserveConnectionFaults(Action<SessionFault> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            lock (connectionObservers)
            {
                connectionObservers.Add(handler);
            }

            return new Unsubscribe(() =>
            {
                lock (connectionObservers)
                {
                    connectionObservers.Remove(handler);
                }
            });
        }

        private void OnFault(SessionFault fault)
        {
            // De-duplication sits ahead of the log rather than beside it, so the
            // count the log shows is the count a reader is meant to act on.
            if (fault.Kind == SessionFaultKind.NoDestination
                && !IsFirstUnroutedMessageOfItsId(fault.MessageId))
            {
                return;
            }

            log.Write(SeverityOf(fault.Kind), fault);

            if (IsConnectionFault(fault.Kind))
            {
                DeliverToObserversAsync(fault).Forget();
            }
        }

        /// <summary>
        /// The set is locked because this runs on whichever thread published the
        /// fault, and the session publishes from its pump, from a timer, and from a
        /// caller's own frame. This is a real race, not a defensive habit.
        /// </summary>
        private bool IsFirstUnroutedMessageOfItsId(MessageId messageId)
        {
            lock (reportedNoDestination)
            {
                return reportedNoDestination.Add(messageId);
            }
        }

        private static bool IsConnectionFault(SessionFaultKind kind) =>
            kind == SessionFaultKind.TransportFailure
            || kind == SessionFaultKind.DispatchFailure;

        private static FaultSeverity SeverityOf(SessionFaultKind kind)
        {
            switch (kind)
            {
                case SessionFaultKind.NoDestination:
                    // Ordinary during a slice that subscribes to almost nothing.
                    return FaultSeverity.Info;
                case SessionFaultKind.TransportFailure:
                case SessionFaultKind.DispatchFailure:
                    return FaultSeverity.Error;
                default:
                    return FaultSeverity.Warning;
            }
        }

        private async UniTaskVoid DeliverToObserversAsync(SessionFault fault)
        {
            try
            {
                await scheduler.SwitchToSessionContextAsync(CancellationToken.None);

                Action<SessionFault>[] observers;
                lock (connectionObservers)
                {
                    observers = connectionObservers.ToArray();
                }

                foreach (var observer in observers)
                {
                    try
                    {
                        observer(fault);
                    }
                    catch
                    {
                        // One broken observer must not deny the others the fault.
                    }
                }
            }
            catch (Exception failure)
            {
                // SubscriberFailure rather than DispatchFailure: this is the
                // delivery failing, not the session's dispatch. Written straight
                // to the log rather than back through OnFault - not because that
                // would recurse (SubscriberFailure is never a connection fault,
                // so OnFault would log it and stop there), but because routing
                // it back through the same dispatch would only add indirection
                // for no functional difference.
                log.Write(FaultSeverity.Warning, new SessionFault(
                    SessionFaultKind.SubscriberFailure,
                    fault.MessageId,
                    "A connection fault never reached the UI: " +
                    $"{failure.GetType().Name}: {failure.Message}"));
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            subscription.Dispose();
        }

        private sealed class Unsubscribe : IDisposable
        {
            private readonly Action action;

            public Unsubscribe(Action action) => this.action = action;

            public void Dispose() => action();
        }
    }
}
