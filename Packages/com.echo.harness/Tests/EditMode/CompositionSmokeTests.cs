using System;
using Echo.Harness.Application;
using Echo.Harness.Bootstrap;
using Echo.Harness.Infrastructure;
using NUnit.Framework;
using VContainer;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class CompositionSmokeTests
    {
        private string savedHost;
        private string savedPort;

        // Only one test below sets these, but the save/restore sits on the fixture
        // for the reason ServerEndpointTests puts it there: ECHO_SERVER_HOST is
        // process-wide, and GoServerEndToEndTests reads it to decide whether to
        // skip. A leaked value does not quietly do nothing - it turns the three
        // sanctioned skips into three loud failures against a placeholder host, and
        // the gate then reports skipped=0, which breaks the sanctioned-skip
        // invariant as well. Measured, by deleting the TearDown attribute below:
        // three GoServerEndToEndTests failures, skipped=0. Note that a resolver
        // which answers for unregistered names - many consumer ISPs run one - makes
        // that a socket-level failure rather than a DNS one, so "it will not
        // resolve" is not the safety net; the restore is.
        //
        // NUnit runs TearDown even when a test body throws, which is the case that
        // matters: the test below asserts while the variables are still set.
        //
        // The variables live in the EDITOR PROCESS, which outlives the run and
        // every domain reload in it. A run that leaks one poisons every later run
        // in the same editor, and reverting the source does not undo it, because
        // the next SetUp saves the poisoned value and the TearDown puts it back.
        [SetUp]
        public void SaveEnvironment()
        {
            savedHost = Environment.GetEnvironmentVariable(ServerEndpoint.HostVariable);
            savedPort = Environment.GetEnvironmentVariable(ServerEndpoint.PortVariable);
        }

        [TearDown]
        public void RestoreEnvironment()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, savedHost);
            Environment.SetEnvironmentVariable(ServerEndpoint.PortVariable, savedPort);
        }

        [Test]
        public void HarnessComposition_ResolvesItsHealthDescriptor()
        {
            var builder = new ContainerBuilder();
            HarnessComposition.Configure(builder);
            using var container = builder.Build();

            var descriptor = container.Resolve<HarnessRuntimeDescriptor>();

            Assert.That(descriptor.Name, Is.EqualTo("Echo Unity Harness"));
            Assert.That(descriptor.ContainsGameplayImplementation, Is.False);
        }

        // Registration is identical whether or not an endpoint is configured. If
        // the unconfigured case registered less, this test would cover a shape that
        // never runs in anger.
        [TestCase(true)]
        [TestCase(false)]
        public void HarnessComposition_ResolvesTheWholeSessionStack(bool configured)
        {
            var endpoint = configured
                ? EndpointResolution.From("example.invalid", 43966, "test")
                : EndpointResolution.NotConfigured("test");

            var builder = new ContainerBuilder();
            HarnessComposition.Configure(builder, endpoint);
            using var container = builder.Build();

            Assert.That(container.Resolve<IClock>(), Is.Not.Null);
            Assert.That(container.Resolve<IElapsedTime>(), Is.Not.Null);
            Assert.That(container.Resolve<ISessionScheduler>(), Is.Not.Null);
            Assert.That(container.Resolve<ITransport>(), Is.Not.Null);
            Assert.That(container.Resolve<IProtocolSession>(), Is.Not.Null);
            Assert.That(
                container.Resolve<EndpointResolution>().IsConfigured, Is.EqualTo(configured));
        }

        // The session and the transport must be the same instances everything else
        // sees. Two ProtocolSessions over two sockets is a defect a resolve-once
        // test cannot see.
        //
        // The scheduler and the two time sources are here for a sharper reason than
        // symmetry, and this test used to cover only the first two: flipping all
        // three of those registrations to Transient left the whole suite green.
        // MainThreadSessionScheduler.LatchForShutdown sets a per-instance field, so
        // a lifecycle owner that resolves ISessionScheduler in order to latch it
        // would, under Transient, latch a scheduler freshly built for that resolve
        // and never seen by the session. The explicit half of the shutdown latch
        // becomes a silent no-op while the pump parks on a dying player loop. The
        // static half still covers Application.quitting and ExitingPlayMode, which
        // is exactly why nothing would fail loudly.
        //
        // Two resolves of the same interface is the whole check: VContainer keys a
        // singleton by its Registration, so the instance ProtocolSession is
        // constructed with is the instance a direct resolve returns. Reaching into
        // ProtocolSession's private scheduler field would assert the same fact
        // through reflection and would break on a rename that changes nothing.
        //
        // NUnit 3.5 as shipped by com.unity.ext.nunit has no Assert.Multiple, so
        // each assertion carries the name of what it pins; only the first failure
        // is reported.
        [Test]
        public void HarnessComposition_RegistersTheSessionAsASingleton()
        {
            var builder = new ContainerBuilder();
            HarnessComposition.Configure(
                builder, EndpointResolution.From("example.invalid", 43966, "test"));
            using var container = builder.Build();

            Assert.That(
                container.Resolve<IProtocolSession>(),
                Is.SameAs(container.Resolve<IProtocolSession>()),
                "IProtocolSession must be a singleton.");
            Assert.That(
                container.Resolve<ITransport>(),
                Is.SameAs(container.Resolve<ITransport>()),
                "ITransport must be a singleton.");
            Assert.That(
                container.Resolve<ISessionScheduler>(),
                Is.SameAs(container.Resolve<ISessionScheduler>()),
                "ISessionScheduler must be a singleton, or the shutdown latch is a no-op.");
            Assert.That(
                container.Resolve<IClock>(),
                Is.SameAs(container.Resolve<IClock>()),
                "IClock must be a singleton.");
            Assert.That(
                container.Resolve<IElapsedTime>(),
                Is.SameAs(container.Resolve<IElapsedTime>()),
                "IElapsedTime must be a singleton.");
        }

        // The tests above resolve every port and would keep passing with the
        // endpoint dropped on the floor, because nothing here connects. Deleting
        // both ternaries in Configure leaves them all green. This is what pins that
        // a configured endpoint actually reaches the transport rather than merely
        // being registered beside it.
        [Test]
        public void HarnessComposition_PointsTheTransportAtTheConfiguredEndpoint()
        {
            var builder = new ContainerBuilder();
            HarnessComposition.Configure(
                builder, EndpointResolution.From("example.invalid", 45000, "test"));
            using var container = builder.Build();

            var options = container.Resolve<TcpTransportOptions>();

            Assert.That(options.Host, Is.EqualTo("example.invalid"));
            Assert.That(options.Port, Is.EqualTo(45000));
        }

        // The unconfigured half of the same property, and not symmetry for its own
        // sake: an unconfigured EndpointResolution carries a null Host and a Port of
        // 0, so a straight-through assignment would build a transport aimed at
        // nothing at all rather than at the loopback default.
        [Test]
        public void HarnessComposition_LeavesTheTransportOnItsDefaultsWhenUnconfigured()
        {
            var defaults = new TcpTransportOptions();

            var builder = new ContainerBuilder();
            HarnessComposition.Configure(builder, EndpointResolution.NotConfigured("test"));
            using var container = builder.Build();

            var options = container.Resolve<TcpTransportOptions>();

            Assert.That(options.Host, Is.EqualTo(defaults.Host));
            Assert.That(options.Port, Is.EqualTo(defaults.Port));
        }

        // The one-argument overload is the only line in this repository that reads
        // endpoint configuration in production, and it is what a LifetimeScope will
        // call. Replacing its body with a hard-coded NotConfigured left every test
        // above green, because every one of them either supplies its own endpoint
        // or never asks what was resolved.
        //
        // Pinned as a delegation rather than as a literal value, and that is the
        // whole design. HarnessEndpointSettings.Resolve prefers a settings asset in
        // Resources over ECHO_SERVER_HOST; the asset is gitignored precisely so a
        // developer can have one. Asserting a fixed host would therefore pass on a
        // bare clone and fail on a correctly configured machine, for a reason with
        // nothing to do with this code. Comparing against ResolveFromResources()
        // evaluated here holds on both, because both sides take the same branch.
        //
        // Setting ECHO_SERVER_HOST is what gives the comparison teeth. Whichever
        // branch wins, the resolution is then configured - and a configured
        // resolution is the only kind that can discriminate a substitute, since
        // NotConfigured in and NotConfigured out produce identical transport
        // defaults. The IsConfigured assertion on the expectation is not a guard
        // that lets the test off: with the variable set it cannot be false, so if
        // it ever is, that is a real finding about resolution and not a reason to
        // skip.
        //
        // Residual, stated rather than hidden: a settings asset carrying a port
        // outside 1..65535 makes ResolveFromResources throw, and this test would
        // then fail on that machine. So would Configure(builder), which is the
        // point of the guard; failing loudly there is the intended behaviour.
        [Test]
        public void HarnessComposition_ResolvesTheEndpointFromResourcesWhenGivenNone()
        {
            Environment.SetEnvironmentVariable(
                ServerEndpoint.HostVariable, "example.invalid");
            Environment.SetEnvironmentVariable(ServerEndpoint.PortVariable, "45000");

            var expected = HarnessEndpointSettings.ResolveFromResources();
            Assert.That(
                expected.IsConfigured,
                Is.True,
                "ECHO_SERVER_HOST is set, so resolution must be configured whether " +
                "it comes from the asset or from the environment.");

            var builder = new ContainerBuilder();
            HarnessComposition.Configure(builder);
            using var container = builder.Build();

            var resolved = container.Resolve<EndpointResolution>();

            Assert.That(resolved.IsConfigured, Is.True, "IsConfigured");
            Assert.That(resolved.Host, Is.EqualTo(expected.Host), "Host");
            Assert.That(resolved.Port, Is.EqualTo(expected.Port), "Port");
            Assert.That(resolved.Source, Is.EqualTo(expected.Source), "Source");

            // Registered beside the graph is not the same as reaching it. This is
            // the half that would still be broken if the endpoint were resolved
            // correctly and then dropped.
            var options = container.Resolve<TcpTransportOptions>();

            Assert.That(options.Host, Is.EqualTo(expected.Host));
            Assert.That(options.Port, Is.EqualTo(expected.Port));
        }
    }
}
