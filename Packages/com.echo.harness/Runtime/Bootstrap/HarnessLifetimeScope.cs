using VContainer;
using VContainer.Unity;

namespace Echo.Harness.Bootstrap
{
    /// <summary>
    /// The app root scope, and the only one this iteration builds. Session and
    /// scene scopes are deferred: the login screen never goes away, so a child
    /// scope today would have a lifetime identical to its parent - ceremony,
    /// not structure. Two events force the decision later, not on a schedule:
    /// the first screen destroyed while the app keeps running forces a UI
    /// scope, and the first flow that must survive a logout without reusing
    /// the same <c>ProtocolSession</c> forces a session scope. See
    /// <c>docs/migration-checklist.md</c> for the fuller reasoning.
    ///
    /// <para><b>No serialized reference to the endpoint asset, deliberately.</b>
    /// <c>HarnessComposition.Configure(builder)</c> resolves the endpoint through
    /// <c>Resources.Load</c> instead. The scene this component lives in is
    /// committed and the asset is not - the ignore rule on
    /// <c>HarnessEndpointSettings.asset</c> is unqualified - so an Inspector
    /// reference would serialize a GUID into the committed scene that resolves to
    /// nothing on every fresh clone. See the type summary on
    /// <c>HarnessEndpointSettings</c>.</para>
    /// </summary>
    public sealed class HarnessLifetimeScope : LifetimeScope
    {
        protected override void Configure(IContainerBuilder builder)
        {
            HarnessComposition.Configure(builder);
            builder.RegisterEntryPoint<HarnessSessionDriver>();

            // Not decoration. See SessionFaultRouterEntryPoint: without this line
            // the router is registered and never constructed, and nothing fails.
            builder.RegisterEntryPoint<SessionFaultRouterEntryPoint>();

            // The same trap, one message along. Without this line MatchFoundWatcher
            // is registered and never constructed, so nothing subscribes to 2004
            // and every match the server sends becomes a NoDestination fault -
            // with the suite green throughout.
            builder.RegisterEntryPoint<MatchFoundWatcherEntryPoint>();

            builder.RegisterComponentInHierarchy<LoginView>();
        }
    }
}
