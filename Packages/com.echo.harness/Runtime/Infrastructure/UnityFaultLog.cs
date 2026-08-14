using Echo.Harness.Application;
using UnityEngine;

namespace Echo.Harness.Infrastructure
{
    /// <summary>
    /// Writes routed faults to the Unity console. It lives here rather than in
    /// Application for one mechanical reason: the architecture gate asserts by
    /// source text that Application names no <c>UnityEngine</c> type
    /// (<c>Tools/ci/verify-architecture.ps1:345</c>).
    ///
    /// <para><c>Debug</c>'s three methods are safe to call from any thread, which
    /// is what lets <see cref="SessionFaultRouter"/> log without hopping first.</para>
    /// </summary>
    public sealed class UnityFaultLog : IFaultLog
    {
        public void Write(FaultSeverity severity, SessionFault fault)
        {
            var line = $"[Harness] {fault.Kind} on {fault.MessageId}: {fault.Diagnostic}";

            switch (severity)
            {
                case FaultSeverity.Error:
                    Debug.LogError(line);
                    break;
                case FaultSeverity.Warning:
                    Debug.LogWarning(line);
                    break;
                default:
                    Debug.Log(line);
                    break;
            }
        }
    }
}
