using Echo.Harness.Application;
using VContainer.Unity;

namespace Echo.Harness.Bootstrap
{
    /// <summary>
    /// Exists to force <see cref="MatchFoundWatcher"/> to be constructed, and to do
    /// nothing else. The sibling of <see cref="SessionFaultRouterEntryPoint"/>, for
    /// the same reason and with the same shape.
    ///
    /// <para>The watcher subscribes to 2004 MatchFoundEvent in its constructor and
    /// every VContainer registration is lazy, so without something asking for it
    /// the watcher is never built, nothing is ever subscribed to that message, and
    /// every match the server sends becomes a <c>NoDestination</c> fault. The whole
    /// suite stays green while doing it: every test that needs a watcher constructs
    /// one directly.</para>
    ///
    /// <para><b><c>QueueViewModel</c> taking a watcher is not a substitute, and the
    /// distinction is the one SessionFaultRouterEntryPoint had to learn.</b> It
    /// does genuinely consume the watcher, and that happens to force construction -
    /// but only for as long as it keeps doing so, and only once something resolves
    /// the view-model. Both are properties of a different class that could change
    /// for unrelated reasons. An empty class named for its only job cannot be
    /// tidied away by accident.</para>
    ///
    /// <para>The watcher cannot solve this itself: <c>IStartable</c> is a VContainer
    /// type and <c>Echo.Harness.Application</c> may not reference VContainer.</para>
    ///
    /// <para>The parameter is deliberately unused. Taking it IS the work.</para>
    /// </summary>
    public sealed class MatchFoundWatcherEntryPoint : IStartable
    {
        public MatchFoundWatcherEntryPoint(MatchFoundWatcher watcher)
        {
        }

        public void Start()
        {
        }
    }
}
