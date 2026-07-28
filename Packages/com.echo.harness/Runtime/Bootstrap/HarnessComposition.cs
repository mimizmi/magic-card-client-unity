using Echo.Harness.Domain;
using VContainer;

namespace Echo.Harness.Bootstrap
{
    public sealed class HarnessRuntimeDescriptor
    {
        public HarnessRuntimeDescriptor(string name, bool containsGameplayImplementation)
        {
            Name = name;
            ContainsGameplayImplementation = containsGameplayImplementation;
        }

        public string Name { get; }

        public bool ContainsGameplayImplementation { get; }
    }

    public static class HarnessComposition
    {
        public static void Configure(IContainerBuilder builder)
        {
            builder.RegisterInstance(new HarnessRuntimeDescriptor(
                "Echo Unity Harness",
                HarnessPolicy.ContainsGameplayImplementation));
        }
    }
}
