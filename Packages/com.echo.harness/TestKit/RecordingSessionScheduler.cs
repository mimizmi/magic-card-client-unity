using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;

namespace Echo.Harness.TestKit
{
    /// <summary>
    /// Completes synchronously and records the thread each switch was requested
    /// from. Synchronous completion is deliberate: it leaves the session's
    /// observable timing identical to the pre-hop behaviour, so a test that fails
    /// after the hop is added is reporting a real change rather than a scheduling
    /// artifact.
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
            cancellationToken.ThrowIfCancellationRequested();

            if (NextFailure != null)
            {
                var failure = NextFailure;
                NextFailure = null;
                throw failure;
            }

            lock (observedThreadIds)
            {
                observedThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
            }

            return UniTask.CompletedTask;
        }
    }
}
