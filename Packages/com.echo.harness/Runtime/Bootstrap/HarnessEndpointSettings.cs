using Echo.Harness.Infrastructure;
using UnityEngine;

namespace Echo.Harness.Bootstrap
{
    /// <summary>
    /// A resolved endpoint, or the fact that there is not one.
    /// <see cref="Source"/> exists so a log line can say where the address came
    /// from: with two sources and a fallthrough, "connecting to X" alone leaves a
    /// developer unable to tell why their asset edit had no effect.
    /// </summary>
    public readonly struct EndpointResolution
    {
        private EndpointResolution(bool isConfigured, string host, int port, string source)
        {
            IsConfigured = isConfigured;
            Host = host;
            Port = port;
            Source = source;
        }

        public bool IsConfigured { get; }

        public string Host { get; }

        public int Port { get; }

        public string Source { get; }

        public static EndpointResolution NotConfigured(string source) =>
            new EndpointResolution(false, null, 0, source);

        public static EndpointResolution From(string host, int port, string source) =>
            new EndpointResolution(true, host, port, source);
    }

    /// <summary>
    /// The endpoint, as an asset a developer can edit in the Inspector without
    /// restarting the editor.
    ///
    /// <para><b>The asset is gitignored and is loaded through Resources.Load
    /// rather than a serialized reference from the scene.</b> That is not a
    /// convenience. The scene is committed and the asset is not, so a serialized
    /// reference would ship a dangling GUID inside a committed scene and break for
    /// every fresh clone. Resources.Load returns null when the asset is absent,
    /// which is exactly the not-configured path, with nothing broken to
    /// explain.</para>
    /// </summary>
    public sealed class HarnessEndpointSettings : ScriptableObject
    {
        public const string ResourcePath = "HarnessEndpointSettings";

        [SerializeField]
        [Tooltip("Blank means fall through to the ECHO_SERVER_HOST environment variable.")]
        private string host = string.Empty;

        [SerializeField]
        private int port = ServerEndpoint.DefaultPort;

        public string Host => host;

        public int Port => port;

        /// <summary>
        /// Test-only seam. The fields are serialized and private so the Inspector
        /// owns them; a test still needs some way to populate an in-memory
        /// instance.
        /// </summary>
        public void SetForTests(string hostValue, int portValue)
        {
            host = hostValue;
            port = portValue;
        }

        public static EndpointResolution ResolveFromResources() =>
            Resolve(Resources.Load<HarnessEndpointSettings>(ResourcePath));

        public static EndpointResolution Resolve(HarnessEndpointSettings asset)
        {
            if (asset != null && !string.IsNullOrWhiteSpace(asset.Host))
            {
                return EndpointResolution.From(
                    asset.Host.Trim(),
                    asset.Port,
                    $"the {ResourcePath} asset");
            }

            if (ServerEndpoint.TryResolveFromEnvironment(out var fromEnvironment))
            {
                return EndpointResolution.From(
                    fromEnvironment.Host,
                    fromEnvironment.Port,
                    $"the {ServerEndpoint.HostVariable} environment variable");
            }

            return EndpointResolution.NotConfigured(
                $"no {ResourcePath} asset with a host, and no {ServerEndpoint.HostVariable}");
        }
    }
}
