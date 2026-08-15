using Echo.Harness.Application;
using VContainer.Unity;

namespace Echo.Harness.Bootstrap
{
    /// <summary>
    /// Exists to force <see cref="SessionFaultRouter"/> to be constructed, and to
    /// do nothing else.
    ///
    /// <para>The router subscribes to the session in its constructor, and every
    /// VContainer registration is lazy - the trap
    /// <c>HarnessComposition.Configure</c>'s own summary names: "Registering is not
    /// resolving." Without something asking for the router, it is never built, no
    /// fault is ever logged, and every test that constructs one directly still
    /// passes.</para>
    ///
    /// <para>The router cannot solve this itself: <c>IStartable</c> is a VContainer
    /// type and <c>Echo.Harness.Application</c> may not reference VContainer.</para>
    ///
    /// <para>Two alternatives were rejected for the same reason, and one of them
    /// has since happened anyway - for a different reason than the one being
    /// rejected here. <c>LoginViewModel</c> does now take a
    /// <see cref="SessionFaultRouter"/> constructor parameter, but it is not the
    /// "unused parameter" trick this paragraph originally warned against:
    /// <c>LoginViewModel</c> genuinely subscribes to it, to observe connection
    /// faults. That happens to force the router's construction too, but only
    /// for as long as <c>LoginViewModel</c> keeps that subscription - if it were
    /// ever refactored away, the forced-construction guarantee this entry point
    /// exists to provide would silently go with it. Hanging it off
    /// <c>HarnessSessionDriver</c> instead was, and still is, rejected for the
    /// reason originally stated: that class has no use for a router and would
    /// carry one only to force construction, inviting exactly the "looks
    /// unused" cleanup. An empty class named for its only job cannot be tidied
    /// away by accident.</para>
    ///
    /// <para>The parameter is deliberately unused. Taking it IS the work.</para>
    /// </summary>
    public sealed class SessionFaultRouterEntryPoint : IStartable
    {
        public SessionFaultRouterEntryPoint(SessionFaultRouter router)
        {
        }

        public void Start()
        {
        }
    }
}
