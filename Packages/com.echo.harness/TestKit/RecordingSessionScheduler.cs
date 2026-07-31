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

        /// <summary>Makes the next switch fail. One-shot.</summary>
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
