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

            var binding = (DataBinding)root.Q<Button>("submit").GetBinding("enabledSelf");

            Assert.That(binding.dataSourcePath, Is.EqualTo(new PropertyPath("CanSubmit")));
        }

        [Test]
        public void TheConnectionStatusLabelBindsToConnectionStatus()
        {
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            var root = asset.Instantiate();

            var binding = (DataBinding)root.Q<Label>("connection-status").GetBinding("text");

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

            var field = root.Q<TextField>("player-name");
            Assert.That(field, Is.Not.Null);

            var valueBinding = field.GetBinding("value");
            Assert.That(valueBinding, Is.Not.Null, "'player-name' has no DataBinding on 'value'.");

            var enabledBinding = field.GetBinding("enabledSelf");
            Assert.That(
                enabledBinding,
                Is.Not.Null,
                "'player-name' has no DataBinding on 'enabledSelf'.");
            Assert.That(
                ((DataBinding)enabledBinding).dataSourcePath,
                Is.EqualTo(new PropertyPath("IsConnected")));
        }

        private static void AssertBound(VisualElement root, string elementName, string property)
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
        }
    }
}
