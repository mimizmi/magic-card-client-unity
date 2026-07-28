using Echo.Harness.Bootstrap;
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
    }
}
