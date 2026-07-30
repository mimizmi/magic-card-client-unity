using System;
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
        DispatchFailure,
        NoDestination
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

        /// <summary>
        /// The message this fault concerns, or <c>default</c> when no single
        /// message does. A stream fault passes <c>default</c>; a heartbeat write
        /// failure passes <see cref="Contracts.MessageId.Pong"/>. Kind is identical
        /// for both, so this field is the only thing separating "the heartbeat
        /// write failed and the connection is probably still usable" from "the
        /// stream desynchronized". Do not treat it as meaningless.
        /// </summary>
        public MessageId MessageId { get; }

        public string Diagnostic { get; }
    }

    /// <summary>
    /// A second request for a response id that already has one in flight. Distinct
    /// from <see cref="CorrelationMismatchException"/> because a probe loop must be
    /// able to tell "mine is still running" from "the server answered wrongly"
    /// without matching on message text.
    /// </summary>
    public sealed class RequestAlreadyInFlightException : InvalidOperationException
    {
        public RequestAlreadyInFlightException(MessageId responseId, string message)
            : base(message)
        {
            ResponseId = responseId;
        }

        public MessageId ResponseId { get; }
    }

    /// <summary>
    /// A reply whose correlatable field does not match what was sent. The protocol
    /// carries no correlation identifier, so this is only detectable where a
    /// payload echoes something back - today, ClientPingResponse.ts.
    /// </summary>
    public sealed class CorrelationMismatchException : InvalidOperationException
    {
        public CorrelationMismatchException(MessageId messageId, string message)
            : base(message)
        {
            MessageId = messageId;
        }

        public MessageId MessageId { get; }
    }
}
