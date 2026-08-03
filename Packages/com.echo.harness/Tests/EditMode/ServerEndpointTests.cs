using System;
using Echo.Harness.Infrastructure;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ServerEndpointTests
    {
        private string savedHost;
        private string savedPort;

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
        public void TryResolveFromEnvironment_ReportsNotConfiguredWithoutAHost()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, null);

            Assert.That(ServerEndpoint.TryResolveFromEnvironment(out _), Is.False);
        }

        [Test]
        public void TryResolveFromEnvironment_DefaultsThePortWhenOnlyAHostIsSet()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, "example.invalid");
            Environment.SetEnvironmentVariable(ServerEndpoint.PortVariable, null);

            Assert.That(ServerEndpoint.TryResolveFromEnvironment(out var endpoint), Is.True);
            Assert.That(endpoint.Host, Is.EqualTo("example.invalid"));
            Assert.That(endpoint.Port, Is.EqualTo(43966));
        }

        // A signed value is a typo, and falling back to the default would run
        // against a different endpoint than the one asked for while reporting
        // whatever answered as the truth. NumberStyles.None is what surfaces it.
        [TestCase("+43966")]
        [TestCase("-1")]
        [TestCase("not-a-port")]
        [TestCase("70000")]
        [TestCase("0")]
        public void TryResolveFromEnvironment_RejectsAnUnusablePort(string configured)
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, "example.invalid");
            Environment.SetEnvironmentVariable(ServerEndpoint.PortVariable, configured);

            Assert.Throws<ArgumentException>(
                () => ServerEndpoint.TryResolveFromEnvironment(out _));
        }

        [Test]
        public void RemoteServerEndpoint_ResolvesThroughTheSameImplementation()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, "example.invalid");
            Environment.SetEnvironmentVariable(ServerEndpoint.PortVariable, "1234");

            Assert.That(RemoteServerEndpoint.TryResolve(out var endpoint), Is.True);
            Assert.That(endpoint.Host, Is.EqualTo("example.invalid"));
            Assert.That(endpoint.Port, Is.EqualTo(1234));
        }
    }
}
