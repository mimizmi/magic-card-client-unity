using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Bootstrap;
using Echo.Harness.Infrastructure;
using Echo.Harness.TestKit;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Echo.Harness.Tests.PlayMode
{
    public sealed class HarnessSessionDriverTests
    {
        // The quiet path. An ordinary shutdown must produce no fault at all -
        // otherwise a log full of shutdown faults makes a real one invisible.
        [UnityTest]
        public IEnumerator TheOrdinaryShutdownPathPublishesNoFault()
        {
            var transport = new FakeTransport();
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var scheduler = new RecordingSessionScheduler();
            using var session = new ProtocolSession(transport, time, time, scheduler);

            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);

            var driver = new HarnessSessionDriver(
                session, EndpointResolution.From("example.invalid", 43966, "test"));

            yield return driver.StartAsync(CancellationToken.None).ToCoroutine();
            Assert.That(session.State, Is.EqualTo(SessionState.Connected));

            yield return driver.ShutdownAsync(CancellationToken.None).ToCoroutine();

            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
            Assert.That(faults, Is.Empty);
        }

        // An unconfigured start is the ordinary state of a machine that has not
        // opted in, and must not be a failure.
        [UnityTest]
        public IEnumerator AnUnconfiguredEndpointDoesNotStartAndDoesNotThrow()
        {
            var transport = new FakeTransport();
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var scheduler = new RecordingSessionScheduler();
            using var session = new ProtocolSession(transport, time, time, scheduler);

            var driver = new HarnessSessionDriver(
                session, EndpointResolution.NotConfigured("test"));

            yield return driver.StartAsync(CancellationToken.None).ToCoroutine();

            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
            Assert.That(transport.State, Is.EqualTo(TransportState.Disconnected));
            Assert.That(
                driver.ShutdownSignalsInstalled,
                Is.False,
                "a driver that never started has nothing to tear down, so it must " +
                "not leave a handler on a process-wide event either");
        }

        // The wiring itself, and the reason the seam it reads exists.
        //
        // Measured before this test was written: deleting the InstallHooks() call
        // from StartAsync - so the driver subscribes to no shutdown signal at all
        // and a real quit stops nothing - left every other test in this file green.
        // That is the defect this whole task exists to prevent, and nothing
        // automated could see it.
        //
        // The removal half is not symmetry for its own sake. The finding document's
        // "Subscription lifetime" section measures a runtime subscription outliving
        // play mode and going on to deliver into the next session, so a driver that
        // installs and never removes leaves a handler pointing at a dead session.
        [UnityTest]
        public IEnumerator AConfiguredStartInstallsTheShutdownSignalsAndShutdownRemovesThem()
        {
            var transport = new FakeTransport();
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var scheduler = new RecordingSessionScheduler();
            using var session = new ProtocolSession(transport, time, time, scheduler);

            var driver = new HarnessSessionDriver(
                session, EndpointResolution.From("example.invalid", 43966, "test"));

            Assert.That(
                driver.ShutdownSignalsInstalled,
                Is.False,
                "nothing is subscribed before StartAsync");

            yield return driver.StartAsync(CancellationToken.None).ToCoroutine();

            Assert.That(
                driver.ShutdownSignalsInstalled,
                Is.True,
                "a started session must be reachable from the shutdown signals, or " +
                "an ordinary quit tears nothing down");

            yield return driver.ShutdownAsync(CancellationToken.None).ToCoroutine();

            Assert.That(
                driver.ShutdownSignalsInstalled,
                Is.False,
                "a runtime subscription outlives play mode, so a driver that has " +
                "shut down must not leave one pointing at a dead session");
        }

        // The backstop. With the loop gone the hop cancels, and shutdown must still
        // finish in bounded time rather than parking forever.
        //
        // What this actually pins is narrower than the sentence above, and the
        // difference was measured rather than assumed. ProtocolSession.StopAsync
        // takes NO scheduler hop - its only await is transport.DisconnectAsync - so
        // a latched scheduler never reaches it, and the OperationCanceledException
        // arm of HarnessSessionDriver.ShutdownAsync is NOT exercised here. The test
        // is kept because "shutdown must not park on a dead loop" is the property
        // that matters and must keep holding however StopAsync is later written; the
        // note is here so nobody reads a pass as evidence that the cancelled arm
        // works.
        [UnityTest]
        public IEnumerator ShutdownFinishesEvenWhenTheSchedulerIsAlreadyLatched()
        {
            var transport = new FakeTransport();
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var scheduler = new MainThreadSessionScheduler();
            using var session = new ProtocolSession(transport, time, time, scheduler);

            var driver = new HarnessSessionDriver(
                session, EndpointResolution.From("example.invalid", 43966, "test"));

            yield return driver.StartAsync(CancellationToken.None).ToCoroutine();

            scheduler.LatchForShutdown();

            var finished = false;
            ShutdownAsync().Forget();

            async UniTaskVoid ShutdownAsync()
            {
                await driver.ShutdownAsync(CancellationToken.None);
                finished = true;
            }

            var deadline = Time.realtimeSinceStartup + 5f;
            while (!finished && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(finished, Is.True, "shutdown must not park on a dead loop");
            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
        }

        // The one that makes the quit hook honest, and it is not in the plan.
        //
        // UnityEngine.Application.quitting is measured to fire in the SAME frame as
        // wantsToQuit with no frame boundary after it
        // (docs/findings/2026-08-02-unity-shutdown-callback-order.md, path A), so a
        // handler there gets no further loop tick in which a hopped continuation
        // could run. UniTask's awaiter cannot wait for one either: GetResult() on an
        // incomplete UniTask does not block - it throws
        // InvalidOperationException("Not yet completed, UniTask only allow to use
        // await.") and returns the promise to the pool
        // (UniTaskCompletionSource.cs:227-232 through StateMachineRunner.cs:214-226).
        //
        // So the whole of what makes an ordinary quit tear the session down is that
        // ShutdownAsync is ALREADY COMPLETE when it returns. This asserts exactly
        // that, against an UNLATCHED MainThreadSessionScheduler - the production
        // scheduler in the state it is in one frame before the latch closes - so an
        // await added to that path which really yields turns this red instead of
        // turning a shipped quit into a silent no-op.
        [UnityTest]
        public IEnumerator ShutdownIsAlreadyCompleteWhenItReturnsSoAQuitHookNeedsNoFrame()
        {
            var transport = new FakeTransport();
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var scheduler = new MainThreadSessionScheduler();
            using var session = new ProtocolSession(transport, time, time, scheduler);

            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);

            var driver = new HarnessSessionDriver(
                session, EndpointResolution.From("example.invalid", 43966, "test"));

            yield return driver.StartAsync(CancellationToken.None).ToCoroutine();
            Assert.That(session.State, Is.EqualTo(SessionState.Connected));

            var teardown = driver.ShutdownAsync(CancellationToken.None);

            Assert.That(
                teardown.Status.IsCompleted(),
                Is.True,
                "ShutdownAsync must be complete the moment it returns. " +
                "Application.quitting has no frame after it and UniTask cannot " +
                "block for one, so anything still pending here is a teardown that " +
                "never happens on a real quit.");

            yield return teardown.ToCoroutine();

            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
            Assert.That(faults, Is.Empty);
        }

        // The other half of the same property, and the test that tells this
        // driver's shutdown apart from the one the plan specified.
        //
        // The plan's hook was an unconditional
        // ShutdownAsync(CancellationToken.None).GetAwaiter().GetResult(). Against a
        // transport whose close has genuinely not finished, that call throws
        // InvalidOperationException("Not yet completed, UniTask only allow to use
        // await.") out of the quit hook - and every transport in this repository is
        // synchronous today, so nothing already here can catch that. Hence the local
        // stub below.
        //
        // What must happen instead is a warning that names the situation, so a
        // future asynchronous close is reported rather than silently turning an
        // ordinary quit into a session that is never stopped. Dispose is the public
        // door onto the same private path the two shutdown signals use.
        [UnityTest]
        public IEnumerator AShutdownThatCannotFinishSynchronouslyWarnsRatherThanThrowing()
        {
            var transport = new StallingCloseTransport();
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var scheduler = new RecordingSessionScheduler();
            using var session = new ProtocolSession(transport, time, time, scheduler);

            var driver = new HarnessSessionDriver(
                session, EndpointResolution.From("example.invalid", 43966, "test"));

            yield return driver.StartAsync(CancellationToken.None).ToCoroutine();
            Assert.That(session.State, Is.EqualTo(SessionState.Connected));

            LogAssert.Expect(LogType.Warning, new Regex("did not complete synchronously"));

            // Must not throw. Under the plan's code this raises out of a Unity event
            // dispatch; the driver's catch turns any such throw into a LogError,
            // which the test framework fails on by itself.
            driver.Dispose();

            Assert.That(
                session.State,
                Is.EqualTo(SessionState.Connected),
                "the close has not finished, so StopAsync's finally has not run yet");

            transport.ReleaseCloses();
            yield return null;

            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
        }

        /// <summary>
        /// A transport whose close is genuinely still running when DisconnectAsync
        /// returns. FakeTransport cannot stand in for this: its DisconnectAsync
        /// returns UniTask.CompletedTask unconditionally, which is exactly the
        /// property the driver relies on and therefore exactly the property a test
        /// of the other branch may not borrow.
        /// </summary>
        private sealed class StallingCloseTransport : ITransport
        {
            private readonly List<UniTaskCompletionSource> closes =
                new List<UniTaskCompletionSource>();

            public TransportState State { get; private set; } = TransportState.Disconnected;

            public UniTask ConnectAsync(CancellationToken cancellationToken)
            {
                State = TransportState.Connected;
                return UniTask.CompletedTask;
            }

            public UniTask SendAsync(
                TransportMessage message,
                CancellationToken cancellationToken) => UniTask.CompletedTask;

            // Parks, and honours the token, so CancelPump can still unblock the
            // receive pump. A fake less cancellable than the real transport would
            // hang this test rather than fail it.
            public UniTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken)
            {
                var waiter = new UniTaskCompletionSource<TransportMessage>();
                cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
                return waiter.Task;
            }

            public UniTask DisconnectAsync(CancellationToken cancellationToken)
            {
                State = TransportState.Disconnected;
                var close = new UniTaskCompletionSource();
                closes.Add(close);
                return close.Task;
            }

            public void ReleaseCloses()
            {
                var pending = closes.ToArray();
                closes.Clear();
                foreach (var close in pending)
                {
                    close.TrySetResult();
                }
            }
        }
    }
}
