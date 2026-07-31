using System;
using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Infrastructure;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Echo.Harness.Tests.PlayMode
{
    public sealed class MainThreadSessionSchedulerTests
    {
        [UnityTest]
        public IEnumerator SwitchingFromAThreadPoolThreadReachesTheMainThread() =>
            UniTask.ToCoroutine(async () =>
            {
                var mainThreadId = Thread.CurrentThread.ManagedThreadId;
                var scheduler = new MainThreadSessionScheduler();

                await UniTask.SwitchToThreadPool();
                Assert.That(
                    Thread.CurrentThread.ManagedThreadId,
                    Is.Not.EqualTo(mainThreadId),
                    "The test has to actually be off the main thread to prove anything.");

                await scheduler.SwitchToSessionContextAsync(CancellationToken.None);

                Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(mainThreadId));
            });

        /// <summary>
        /// The scheduler's doc comment claims the already-on-main-thread case costs
        /// nothing. Asserting only "the thread is still the main thread" would hold
        /// whether the switch returned inline, yielded one frame, or yielded a
        /// hundred, so it would measure nothing; the frame count is what actually
        /// measures the claim. The thread id is kept alongside it because that is
        /// what would break if the hop were ever swapped for a thread-pool one.
        /// </summary>
        [UnityTest]
        public IEnumerator SwitchingWhileAlreadyOnTheMainThreadCostsNoFrame() =>
            UniTask.ToCoroutine(async () =>
            {
                var mainThreadId = Thread.CurrentThread.ManagedThreadId;
                var scheduler = new MainThreadSessionScheduler();
                var frameBefore = Time.frameCount;

                await scheduler.SwitchToSessionContextAsync(CancellationToken.None);

                Assert.That(
                    Time.frameCount,
                    Is.EqualTo(frameBefore),
                    "Switching while already on the main thread must complete without " +
                    "yielding, or a session whose transport completed inline pays a " +
                    "frame for a hop it did not need.");
                Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(mainThreadId));
            });

        /// <summary>
        /// RecordingSessionScheduler throws OperationCanceledException on a cancelled
        /// token and every EditMode session test is written against that double. If
        /// production disagreed, the EditMode suite would be pinning a contract that
        /// never ships.
        /// </summary>
        [UnityTest]
        public IEnumerator SwitchingWithACancelledTokenOnTheMainThreadThrows() =>
            UniTask.ToCoroutine(async () =>
            {
                var scheduler = new MainThreadSessionScheduler();
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                var canceled = false;
                try
                {
                    await scheduler.SwitchToSessionContextAsync(cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }

                Assert.That(
                    canceled,
                    Is.True,
                    "A cancelled token has to surface as OperationCanceledException, " +
                    "because that is the contract RecordingSessionScheduler pins for " +
                    "the whole EditMode session suite.");
            });

        /// <summary>
        /// The same contract from the other side. Only the exception type is
        /// asserted, deliberately: off the main thread the awaiter registers its
        /// continuation on the player loop before it ever consults the token, so the
        /// throw arrives a frame later rather than immediately. Asserting promptness
        /// here would assert something that is not true.
        /// </summary>
        [UnityTest]
        public IEnumerator SwitchingWithACancelledTokenOffTheMainThreadAlsoThrows() =>
            UniTask.ToCoroutine(async () =>
            {
                var mainThreadId = Thread.CurrentThread.ManagedThreadId;
                var scheduler = new MainThreadSessionScheduler();
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                await UniTask.SwitchToThreadPool();
                Assert.That(
                    Thread.CurrentThread.ManagedThreadId,
                    Is.Not.EqualTo(mainThreadId),
                    "The test has to actually be off the main thread to prove anything.");

                var canceled = false;
                try
                {
                    await scheduler.SwitchToSessionContextAsync(cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }

                Assert.That(canceled, Is.True);
                Assert.That(
                    Thread.CurrentThread.ManagedThreadId,
                    Is.EqualTo(mainThreadId),
                    "Even the cancelled switch resumes on the main thread, because the " +
                    "player loop is what runs the continuation that throws.");
            });
    }
}
