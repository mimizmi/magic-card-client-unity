using Echo.Harness.Contracts;

namespace Echo.Harness.Application
{
    public enum SessionState
    {
        Disconnected,
        Connecting,
        Connected,
        Faulted
    }

    public enum SessionFaultKind
    {
        UnknownMessageId,
        MalformedPayload,
        CorrelationMismatch,
        SubscriberFailure,
        TransportFailure,
        DispatchFailure
    }

    /// <summary>
    /// A recoverable problem the session decided not to raise as an exception,
    /// because the caller that would have caught it is not on the stack.
    /// </summary>
    public readonly struct SessionFault
    {
        public SessionFault(SessionFaultKind kind, MessageId messageId, string diagnostic)
        {
            Kind = kind;
            MessageId = messageId;
            Diagnostic = diagnostic;
        }

        public SessionFaultKind Kind { get; }

        /// <summary>Carries no meaning when <see cref="Kind"/> is TransportFailure.</summary>
        public MessageId MessageId { get; }

        public string Diagnostic { get; }
    }
}
