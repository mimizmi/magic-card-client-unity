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
    /// <para>Two alternatives were rejected for the same reason. Hanging the router
    /// off <c>LoginViewModel</c>'s constructor works today and breaks silently the
    /// first time someone removes what looks like an unused parameter; hanging it
    /// off <c>HarnessSessionDriver</c> gives that class an argument it never uses
    /// and invites the same cleanup. An empty class named for its only job cannot
    /// be tidied away by accident.</para>
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
