using System;
using Echo.Harness.Bootstrap;
using Echo.Harness.Infrastructure;
using NUnit.Framework;
using UnityEngine;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class EndpointResolutionTests
    {
        private string savedHost;
        private string savedPort;

        [SetUp]
        public void SaveEnvironment()
        {
            savedHost = Environment.GetEnvironmentVariable(ServerEndpoint.HostVariable);
            savedPort = Environment.GetEnvironmentVariable(ServerEndpoint.PortVariable);
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, null);
            Environment.SetEnvironmentVariable(ServerEndpoint.PortVariable, null);
        }

        [TearDown]
        public void RestoreEnvironment()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, savedHost);
            Environment.SetEnvironmentVariable(ServerEndpoint.PortVariable, savedPort);
        }

        [Test]
        public void Resolve_ReportsNotConfiguredWhenNeitherSourceHasAHost()
        {
            var resolution = HarnessEndpointSettings.Resolve(null);

            Assert.That(resolution.IsConfigured, Is.False);
        }

        // A missing asset is the ordinary state of a fresh clone, not an error.
        [Test]
        public void Resolve_FallsBackToTheEnvironmentWhenTheAssetIsAbsent()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, "from-env.invalid");

            var resolution = HarnessEndpointSettings.Resolve(null);

            Assert.That(resolution.IsConfigured, Is.True);
            Assert.That(resolution.Host, Is.EqualTo("from-env.invalid"));
            Assert.That(resolution.Source, Does.Contain(ServerEndpoint.HostVariable));
        }

        // A blank host in a present asset means "I have not filled this in", which
        // must fall through rather than resolve to an empty host.
        [Test]
        public void Resolve_FallsThroughAnAssetWithABlankHost()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, "from-env.invalid");
            var asset = ScriptableObject.CreateInstance<HarnessEndpointSettings>();
            try
            {
                var resolution = HarnessEndpointSettings.Resolve(asset);

                Assert.That(resolution.IsConfigured, Is.True);
                Assert.That(resolution.Host, Is.EqualTo("from-env.invalid"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        // The asymmetry Task 6 shipped and this task closes. ECHO_SERVER_PORT has
        // rejected these three since Task 6 - ServerEndpointTests carries the same
        // set as strings - while the asset's port was a serialized int that reached
        // the transport unexamined. 0 is why it matters rather than being tidy: it
        // is default(int), so it is what a freshly created asset or a reset
        // Inspector field holds, and Socket reads it as "let the OS choose" instead
        // of refusing it, so the failure surfaces as an unreachable server.
        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(70000)]
        public void Resolve_RejectsAnAssetPortOutsideTheUsableRange(int port)
        {
            var asset = ScriptableObject.CreateInstance<HarnessEndpointSettings>();
            try
            {
                asset.SetForTests("from-asset.invalid", port);

                Assert.Throws<ArgumentException>(() => HarnessEndpointSettings.Resolve(asset));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void Resolve_PrefersTheAssetOverTheEnvironment()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, "from-env.invalid");
            var asset = ScriptableObject.CreateInstance<HarnessEndpointSettings>();
            try
            {
                asset.SetForTests("from-asset.invalid", 1234);

                var resolution = HarnessEndpointSettings.Resolve(asset);

                Assert.That(resolution.Host, Is.EqualTo("from-asset.invalid"));
                Assert.That(resolution.Port, Is.EqualTo(1234));
                Assert.That(resolution.Source, Does.Contain("asset"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }
    }
}
