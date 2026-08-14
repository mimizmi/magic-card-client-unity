using VContainer;
using VContainer.Unity;

namespace Echo.Harness.Bootstrap
{
    /// <summary>
    /// The app root scope, and the only one this iteration builds. Session and
    /// scene scopes are deferred: with one scene and no login flow, a child scope
    /// with one child and a lifetime identical to its parent is ceremony. See the
    /// design spec for the reasoning.
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
        }
    }
}
