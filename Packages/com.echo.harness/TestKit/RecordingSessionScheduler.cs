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

        public IReadOnlyList<int> ObservedThreadIds => observedThreadIds;

        public int SwitchCount => observedThreadIds.Count;

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

            observedThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
            return UniTask.CompletedTask;
        }
    }
}
