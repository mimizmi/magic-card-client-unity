using System.Collections.Generic;
using System.Threading;
using Echo.Harness.Application;

namespace Echo.Harness.TestKit
{
    public readonly struct FaultLogEntry
    {
        public FaultLogEntry(FaultSeverity severity, SessionFault fault, int threadId)
        {
            Severity = severity;
            Fault = fault;
            ThreadId = threadId;
        }

        public FaultSeverity Severity { get; }

        public SessionFault Fault { get; }

        /// <summary>
        /// The thread the write happened on. This is the evidence for the router's
        /// central claim - that logging does not hop - so it is recorded rather
        /// than assumed.
        /// </summary>
        public int ThreadId { get; }
    }

    public sealed class RecordingFaultLog : IFaultLog
    {
        private readonly List<FaultLogEntry> entries = new List<FaultLogEntry>();

        /// <summary>A snapshot taken under the same lock the write takes.</summary>
        public IReadOnlyList<FaultLogEntry> Entries
        {
            get
            {
                lock (entries)
                {
                    return new List<FaultLogEntry>(entries);
                }
            }
        }

        public void Write(FaultSeverity severity, SessionFault fault)
        {
            lock (entries)
            {
                entries.Add(new FaultLogEntry(
                    severity, fault, Thread.CurrentThread.ManagedThreadId));
            }
        }
    }
}
