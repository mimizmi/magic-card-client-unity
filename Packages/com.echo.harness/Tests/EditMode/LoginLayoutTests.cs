using NUnit.Framework;
using Unity.Properties;
using UnityEditor;
using UnityEngine.UIElements;

namespace Echo.Harness.Tests.EditMode
{
    // The PlayMode LoginViewTests binds a Label built by hand in C# and never
    // opens Login.uxml, so it passes identically whether the layout binds
    // anything at all. This test opens the real asset instead, because a
    // <ui:Label data-source-path="..."/> attribute records where to look and
    // never records what to bind - see Login.uxml's <Bindings> children.
    public sealed class LoginLayoutTests
    {
        private const string LayoutPath = "Packages/com.echo.harness/UI/Login.uxml";

        [Test]
        public void TheLayoutAssetExists()
        {
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);

            Assert.That(asset, Is.Not.Null, "Login.uxml did not import as a VisualTreeAsset.");
        }

        [Test]
        public void EveryElementCarriesARealDataBinding()
        {
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            Assert.That(asset, Is.Not.Null);

            var root = asset.Instantiate();

            AssertBound(root, "connection-status", "text");
            AssertBound(root, "player-name", "value");
            AssertBound(root, "submit", "enabledSelf");
            AssertBound(root, "result", "text");
            AssertBound(root, "connection-fault", "text");

            AssertBound(root, "queue-status", "text");
            AssertBound(root, "join-queue", "enabledSelf");
            AssertBound(root, "leave-queue", "enabledSelf");
            AssertBound(root, "match", "text");
        }

        /// <summary>
        /// The queue panel binds against its OWN data source - LoginView assigns a
        /// QueueViewModel to 'queue-root' - so that element has to exist and has to
        /// be the ancestor of the queue bindings. Without the container element the
        /// four bindings above would resolve against the root's LoginViewModel,
        /// find no such properties, and silently render nothing.
        /// </summary>
        [Test]
        public void TheQueuePanelHasItsOwnContainerElement()
        {
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            var root = asset.Instantiate();

            var queueRoot = root.Q("queue-root");
            Assert.That(queueRoot, Is.Not.Null,
                "'queue-root' did not import from Login.uxml; LoginView assigns the " +
                "QueueViewModel to it by name.");

            foreach (var name in new[] { "queue-status", "join-queue", "leave-queue", "match" })
            {
                Assert.That(queueRoot.Q(name), Is.Not.Null,
                    $"'{name}' must sit UNDER 'queue-root', or its binding resolves " +
                    "against the login view-model instead of the queue one.");
            }
        }

        // The same copy-paste hazard the submit button's test covers: some
        // DataBinding being present says nothing about which property it names,
        // and the two queue buttons differ by exactly one path.
        [Test]
        public void TheQueueButtonsBindToTheirOwnGates()
        {
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            var root = asset.Instantiate();

            var join = (DataBinding)AssertBound(root, "join-queue", "enabledSelf");
            var leave = (DataBinding)AssertBound(root, "leave-queue", "enabledSelf");

            Assert.That(join.dataSourcePath, Is.EqualTo(new PropertyPath("CanJoin")));
            Assert.That(leave.dataSourcePath, Is.EqualTo(new PropertyPath("CanLeave")));
        }

        // QueueStatusText and MatchText are separate for a reason the view-model
        // states: one is what this client asked for, the other is what the server
        // pushed, and either binding pointed at the other's property would let a
        // cancellation erase the match that beat it.
        [Test]
        public void TheQueueLabelsBindToTheirOwnText()
        {
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            var root = asset.Instantiate();

            var status = (DataBinding)AssertBound(root, "queue-status", "text");
            var match = (DataBinding)AssertBound(root, "match", "text");

            Assert.That(status.dataSourcePath, Is.EqualTo(new PropertyPath("QueueStatusText")));
            Assert.That(match.dataSourcePath, Is.EqualTo(new PropertyPath("MatchText")));
        }

        // A copy-pasted data-source-path (e.g. the button binding to
        // "ConnectionStatus" instead of "CanSubmit") would still pass
        // EveryElementCarriesARealDataBinding, since that test only checks that
        // *some* DataBinding is present. These pin the actual path per element.
        [Test]
        public void TheSubmitButtonBindsToCanSubmit()
        {
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            var root = asset.Instantiate();

            var binding = (DataBinding)AssertBound(root, "submit", "enabledSelf");

            Assert.That(binding.dataSourcePath, Is.EqualTo(new PropertyPath("CanSubmit")));
        }

        [Test]
        public void TheConnectionStatusLabelBindsToConnectionStatus()
        {
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            var root = asset.Instantiate();

            var binding = (DataBinding)AssertBound(root, "connection-status", "text");

            Assert.That(binding.dataSourcePath, Is.EqualTo(new PropertyPath("ConnectionStatus")));
        }

        // The player-name field is disabled until the session connects - see
        // LoginViewModel.IsConnected - and it must still bind its value two-way
        // for typing to reach the view-model at all. Both bindings live on one
        // element, so a test pinning only one of them would miss a regression
        // that silently dropped the other.
        [Test]
        public void ThePlayerNameFieldCarriesTwoBindings()
        {
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            var root = asset.Instantiate();

            AssertBound(root, "player-name", "value");
            var enabledBinding = (DataBinding)AssertBound(root, "player-name", "enabledSelf");

            Assert.That(enabledBinding.dataSourcePath, Is.EqualTo(new PropertyPath("IsConnected")));
        }

        private static Binding AssertBound(VisualElement root, string elementName, string property)
        {
            var element = root.Q(elementName);
            Assert.That(element, Is.Not.Null, $"'{elementName}' did not import from Login.uxml.");

            var binding = element.GetBinding(property);
            Assert.That(
                binding,
                Is.Not.Null,
                $"'{elementName}' has no DataBinding on '{property}'. A data-source-path " +
                "attribute alone declares no binding - it must be a <Bindings><ui:DataBinding " +
                "..../></Bindings> child element.");

            return binding;
        }
    }
}
