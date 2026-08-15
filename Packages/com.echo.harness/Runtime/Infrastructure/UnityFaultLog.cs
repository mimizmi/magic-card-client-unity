using Echo.Harness.Application;
using UnityEngine;

namespace Echo.Harness.Infrastructure
{
    /// <summary>
    /// Writes routed faults to the Unity console. It lives here rather than in
    /// Application for one mechanical reason: the architecture gate's
    /// Application source-text assertion in
    /// <c>Tools/ci/verify-architecture.ps1</c> forbids naming a
    /// <c>UnityEngine</c> type there.
    ///
    /// <para>Unity documents nothing about which threads may call <c>Debug</c>'s
    /// three logging methods. The design relies on the log handler serialising
    /// internally rather than on any documented guarantee - that reliance is
    /// what lets <see cref="SessionFaultRouter"/> log without hopping first.</para>
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
