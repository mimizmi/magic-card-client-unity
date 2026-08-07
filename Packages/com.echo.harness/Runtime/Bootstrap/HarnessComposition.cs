using Echo.Harness.Application;
using Echo.Harness.Domain;
using Echo.Harness.Infrastructure;
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
        public static void Configure(IContainerBuilder builder) =>
            Configure(builder, HarnessEndpointSettings.ResolveFromResources());

        /// <summary>
        /// The single registration point. The endpoint is a parameter rather than
        /// something this method reads, so the whole graph can be built in EditMode
        /// from a bare ContainerBuilder with no Resources folder and no scene.
        ///
        /// <para>The graph has the same shape whether or not an endpoint is
        /// configured. Deciding whether to connect belongs to whatever drives the
        /// session lifecycle, not here; registering less in the unconfigured case
        /// would mean the EditMode resolution test covers a shape that never runs.
        /// (The plan's text named a <c>HarnessSessionDriver</c> here. That type does
        /// not exist yet - Task 9 creates it - and a cref to it would not compile
        /// clean, so the role is named instead of the type.)</para>
        ///
        /// <para><b>Registering is not resolving.</b> Every Register call below is
        /// lazy, so nothing is constructed until something asks. Nothing in this
        /// repository asks yet: this method has no caller outside the EditMode smoke
        /// test until Task 9 lands a LifetimeScope. That is what still bounds the
        /// shutdown exposure described on
        /// <c>ProtocolSession.SwitchToSessionContextForTeardownAsync</c>.</para>
        /// </summary>
        public static void Configure(IContainerBuilder builder, EndpointResolution endpoint)
        {
            builder.RegisterInstance(new HarnessRuntimeDescriptor(
                "Echo Unity Harness",
                HarnessPolicy.ContainsGameplayImplementation));

            builder.RegisterInstance(endpoint);

            builder.Register<SystemClock>(Lifetime.Singleton).As<IClock>();
            builder.Register<StopwatchElapsedTime>(Lifetime.Singleton).As<IElapsedTime>();
            builder.Register<MainThreadSessionScheduler>(Lifetime.Singleton)
                .As<ISessionScheduler>();

            // Host and port are the only options configuration supplies. The rest
            // are derived from the authoritative Go server and are not negotiable,
            // so they keep their defaults. An unconfigured endpoint leaves the
            // loopback default in place, which nothing connects to because nothing
            // starts the session.
            var defaults = new TcpTransportOptions();
            builder.RegisterInstance(new TcpTransportOptions
            {
                Host = endpoint.IsConfigured ? endpoint.Host : defaults.Host,
                Port = endpoint.IsConfigured ? endpoint.Port : defaults.Port,
            });

            builder.Register<TcpTransport>(Lifetime.Singleton).As<ITransport>();
            builder.Register<ProtocolSession>(Lifetime.Singleton).As<IProtocolSession>();
        }
    }
}
