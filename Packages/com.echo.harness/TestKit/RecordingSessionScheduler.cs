using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;

namespace Echo.Harness.TestKit
{
    /// <summary>
    /// Completes synchronously on the success path and records the thread each
    /// switch was requested from. Synchronous completion is deliberate: it leaves
    /// the session's observable timing identical to the pre-hop behaviour, so a
    /// test that fails after the hop is added is reporting a real change rather
    /// than a scheduling artifact.
    ///
    /// <para>The synchrony claim covers the success path only. Cancellation and
    /// the injected failure come back as an already-faulted UniTask rather than
    /// being thrown out of the call, because that is the only shape production
    /// can have: MainThreadSessionScheduler is an <c>async UniTask</c> method,
    /// and such a method structurally cannot throw before it returns. A double
    /// that threw synchronously would let the EditMode suite pin a contract that
    /// never ships, and no test could catch the divergence.</para>
    /// </summary>
    public sealed class RecordingSessionScheduler : ISessionScheduler
    {
        private readonly List<int> observedThreadIds = new List<int>();

        /// <summary>
        /// A snapshot, taken under the same lock the recording takes. The tool
        /// that proves thread confinement is itself called from more than one
        /// thread - a request timeout records from the CancelAfter timer while
        /// the test thread reads - so an unsynchronized List would let the
        /// evidence tear the very race it exists to rule out.
        /// </summary>
        public IReadOnlyList<int> ObservedThreadIds
        {
            get
            {
                lock (observedThreadIds)
                {
                    return new List<int>(observedThreadIds);
                }
            }
        }

        public int SwitchCount
        {
            get
            {
                lock (observedThreadIds)
                {
                    return observedThreadIds.Count;
                }
            }
        }

        /// <summary>
        /// Makes the next switch fail. One-shot - it is cleared by whichever switch
        /// reaches it first, and it now has two possible consumers rather than one.
        /// The receive pump hops once per inbound message, and a failing request
        /// hops again on its way out through
        /// <c>SwitchToSessionContextForTeardownAsync</c>. So a test arming this
        /// expecting the pump to fail can have it eaten by a request teardown that
        /// ran first, leaving the pump to hop successfully and the test to assert
        /// against a session that never faulted. Arm it with only one of the two in
        /// flight.
        /// </summary>
        public Exception NextFailure { get; set; }

        public UniTask SwitchToSessionContextAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return UniTask.FromCanceled(cancellationToken);
            }

            if (NextFailure != null)
            {
                var failure = NextFailure;
                NextFailure = null;
                return UniTask.FromException(failure);
            }

            lock (observedThreadIds)
            {
                observedThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
            }

            return UniTask.CompletedTask;
        }
    }
}
