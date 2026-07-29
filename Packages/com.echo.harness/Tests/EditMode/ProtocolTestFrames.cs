using System.Text;
using Echo.Harness.Application;
using Echo.Harness.Contracts;

namespace Echo.Harness.Tests.EditMode
{
    internal static class ProtocolTestFrames
    {
        /// <summary>Builds an inbound transport message from a JSON body.</summary>
        public static TransportMessage Frame(MessageId id, string json) =>
            new TransportMessage(id, Encoding.UTF8.GetBytes(json));

        /// <summary>Builds an inbound transport message with no body at all.</summary>
        public static TransportMessage Bodyless(MessageId id) =>
            new TransportMessage(id, System.Array.Empty<byte>());
    }
}
