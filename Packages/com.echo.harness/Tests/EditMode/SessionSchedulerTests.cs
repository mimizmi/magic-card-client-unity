using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class SessionSchedulerTests
    {
        [Test]
        public void RecordingSchedulerCompletesSynchronouslyAndRecordsTheThread()
        {
            var scheduler = new RecordingSessionScheduler();

            var task = scheduler.SwitchToSessionContextAsync(CancellationToken.None);

            Assert.That(task.Status.IsCompletedSuccessfully(), Is.True,
                "A synchronous completion is what keeps the existing suite's timing unchanged.");
            Assert.That(scheduler.SwitchCount, Is.EqualTo(1));
            Assert.That(
                scheduler.ObservedThreadIds[0],
                Is.EqualTo(Thread.CurrentThread.ManagedThreadId));
        }

        [Test]
        public void RecordingSchedulerHonoursCancellation()
        {
            var scheduler = new RecordingSessionScheduler();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => scheduler.SwitchToSessionContextAsync(cancellation.Token));
            Assert.That(scheduler.SwitchCount, Is.EqualTo(0));
        }

        [Test]
        public void RecordingSchedulerCanBeMadeToFail()
        {
            var scheduler = new RecordingSessionScheduler
            {
                NextFailure = new InvalidOperationException("no context")
            };

            var failure = Assert.Throws<InvalidOperationException>(
                () => scheduler.SwitchToSessionContextAsync(CancellationToken.None)
                    .GetAwaiter().GetResult());
            Assert.That(failure.Message, Is.EqualTo("no context"));
            Assert.That(scheduler.NextFailure, Is.Null, "The failure is one-shot.");
        }
    }
}
