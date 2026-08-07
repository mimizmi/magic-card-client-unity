using Echo.Harness.Application;
using Echo.Harness.Bootstrap;
using Echo.Harness.Infrastructure;
using NUnit.Framework;
using VContainer;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class CompositionSmokeTests
    {
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
        [Test]
        public void HarnessComposition_RegistersTheSessionAsASingleton()
        {
            var builder = new ContainerBuilder();
            HarnessComposition.Configure(
                builder, EndpointResolution.From("example.invalid", 43966, "test"));
            using var container = builder.Build();

            Assert.That(
                container.Resolve<IProtocolSession>(),
                Is.SameAs(container.Resolve<IProtocolSession>()));
            Assert.That(
                container.Resolve<ITransport>(),
                Is.SameAs(container.Resolve<ITransport>()));
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
    }
}
