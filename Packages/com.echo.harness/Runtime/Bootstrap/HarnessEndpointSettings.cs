using System;
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

        /// <summary>
        /// The one door every configured endpoint comes through, and therefore where
        /// the port range is enforced.
        ///
        /// <para>Until now only the environment door checked it.
        /// <see cref="ServerEndpoint.TryResolveFromEnvironment"/> rejects anything
        /// outside <see cref="ServerEndpoint.MinPort"/>..<see cref="ServerEndpoint.MaxPort"/>,
        /// while the asset's <c>port</c> is a plain serialized <c>int</c> that
        /// reached here unexamined. <c>0</c> is the value that makes the asymmetry
        /// matter: it is <c>default(int)</c>, so a freshly created asset or a reset
        /// Inspector field lands on it, and it clears every .NET argument check on
        /// the way to a connect that fails at the socket layer - an unreachable
        /// server to read, a typo in fact.</para>
        ///
        /// <para>Here rather than in <see cref="HarnessEndpointSettings.Resolve"/>
        /// for two reasons. This is the only site that knows
        /// <paramref name="source"/>, so the message can name the asset or the
        /// variable the bad value came from rather than reporting a bare integer.
        /// And it is the only site every configured endpoint must pass - including
        /// one handed straight to <c>HarnessComposition.Configure(builder,
        /// endpoint)</c>, which <c>Resolve</c> never sees.</para>
        ///
        /// <para>The range itself is <see cref="ServerEndpoint"/>'s rather than a
        /// second copy; that type's summary is explicit that two copies of the port
        /// guard would drift. <see cref="NotConfigured"/> is exempt and stays so: it
        /// carries port <c>0</c> precisely because it carries no endpoint.</para>
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="port"/> is not a usable port.
        /// </exception>
        public static EndpointResolution From(string host, int port, string source)
        {
            if (!ServerEndpoint.IsUsablePort(port))
            {
                throw new ArgumentException(
                    $"{source} supplies port {port}, which is not between " +
                    $"{ServerEndpoint.MinPort} and {ServerEndpoint.MaxPort}.",
                    nameof(port));
            }

            return new EndpointResolution(true, host, port, source);
        }
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
