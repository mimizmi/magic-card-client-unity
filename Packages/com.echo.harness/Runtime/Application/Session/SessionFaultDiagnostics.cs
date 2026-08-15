namespace Echo.Harness.Application
{
    public enum FaultSeverity
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Where a routed fault is written. A port rather than a direct call because
    /// <c>Echo.Harness.Application</c> may not name a Unity type - the
    /// architecture gate's Application source-text assertion in
    /// <c>Tools/ci/verify-architecture.ps1</c> forbids it - and because a test
    /// needs to read what was written and on which thread.
    ///
    /// <para>Implementations must be safe to call from any thread. The router
    /// writes without hopping first, deliberately; see
    /// <see cref="SessionFaultRouter"/>.</para>
    /// </summary>
    public interface IFaultLog
    {
        void Write(FaultSeverity severity, SessionFault fault);
    }
}
