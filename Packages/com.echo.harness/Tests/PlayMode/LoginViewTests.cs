using System.Collections;
using System.Reflection;
using Echo.Harness.Application;
using Echo.Harness.Bootstrap;
using Echo.Harness.Presentation;
using Echo.Harness.TestKit;
using NUnit.Framework;
using Unity.Properties;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Echo.Harness.Tests.PlayMode
{
    public sealed class LoginViewTests
    {
        // A VisualElement that is never attached to a panel never runs the
        // binding system at all - a data-source-path is only ever resolved
        // against a live panel's binding update, which the player loop drives.
        // Building a PanelSettings in memory and attaching a UIDocument to it
        // is what makes this a real panel rather than a detached tree: no
        // AssetDatabase, because this assembly is not editor-only.
        [UnityTest]
        public IEnumerator TheBindingSurvivesAPlayerLoopFrame()
        {
            var status = new FakeSessionStatus { State = SessionState.Connected };
            var session = new FakeProtocolSession();
            using var router = new SessionFaultRouter(
                session, new RecordingSessionScheduler(), new RecordingFaultLog());
            using var viewModel = new LoginViewModel(status, new FakeLoginUseCase(), router);

            var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            var documentGameObject = new GameObject(
                nameof(TheBindingSurvivesAPlayerLoopFrame), typeof(UIDocument));
            try
            {
                var document = documentGameObject.GetComponent<UIDocument>();
                document.panelSettings = panelSettings;

                var root = document.rootVisualElement;
                root.dataSource = viewModel;

                var label = new Label();
                label.SetBinding(nameof(Label.text), new DataBinding
                {
                    dataSourcePath = new PropertyPath(nameof(LoginViewModel.ConnectionStatus)),
                });
                root.Add(label);

                yield return null;
                yield return null;

                Assert.That(
                    label.text,
                    Is.EqualTo("Connected."),
                    "The label's binding never ran, so the view-model's value never " +
                    "reached the element it is supposedly bound to.");
            }
            finally
            {
                Object.Destroy(documentGameObject);
                Object.Destroy(panelSettings);
            }
        }

        // LoginView itself has no coverage anywhere in the repository. This pins
        // its two jobs: Start wires the real document to the view-model, and a
        // click on "submit" reaches SubmitAsync rather than doing nothing.
        [UnityTest]
        public IEnumerator StartBindsTheDocumentAndClickingSubmitReachesTheViewModel()
        {
            var status = new FakeSessionStatus { State = SessionState.Connected };
            var session = new FakeProtocolSession();
            var login = new FakeLoginUseCase();
            using var router = new SessionFaultRouter(
                session, new RecordingSessionScheduler(), new RecordingFaultLog());
            using var viewModel = new LoginViewModel(status, login, router);

            var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            var host = new GameObject(
                nameof(StartBindsTheDocumentAndClickingSubmitReachesTheViewModel));
            try
            {
                // UIDocument builds its root in OnEnable, which only runs while
                // the GameObject is active - so the document is attached first,
                // while active, and LoginView is added only once the button it
                // will query for already exists. Start() is still deferred to
                // the next frame regardless, which is what leaves room to set
                // the private fields below before it runs.
                var document = host.AddComponent<UIDocument>();
                document.panelSettings = panelSettings;

                // The button LoginView.Start() queries for by name, built by
                // hand rather than loaded from Login.uxml - LoginLayoutTests
                // already pins that the real asset carries this element and a
                // real binding on it; this test is only about LoginView's own
                // wiring, not the layout.
                var submit = new Button { name = "submit" };
                document.rootVisualElement.Add(submit);

                var view = host.AddComponent<LoginView>();
                SetPrivateField(view, "document", document);
                SetPrivateField(view, "viewModel", viewModel);

                // LoginView.Start runs before the next Update, so one frame is
                // enough for it to have wired the document.
                yield return null;

                Assert.That(
                    document.rootVisualElement.dataSource,
                    Is.SameAs(viewModel),
                    "LoginView.Start must set the document's root dataSource to the view-model.");

                SimulateClick(submit);

                yield return null;

                Assert.That(
                    login.CallCount,
                    Is.EqualTo(1),
                    "Clicking 'submit' must reach LoginViewModel.SubmitAsync, which calls " +
                    "ILoginUseCase.LoginAsync exactly once.");
            }
            finally
            {
                Object.Destroy(host);
                Object.Destroy(panelSettings);
            }
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"LoginView no longer declares a field named '{fieldName}'.");
            field.SetValue(target, value);
        }

        // Clickable.SimulateSingleClick is the mechanism UI Toolkit's own tests
        // use to drive a Button without staging a real pointer at real screen
        // coordinates against a laid-out panel. It is internal rather than
        // public, hence the reflection; the alternative - sending raw
        // PointerDown/PointerUp events - depends on the button already having a
        // computed, non-zero layout rect, which a panel built in this test has
        // no reliable way to guarantee within a single frame.
        private static void SimulateClick(Button button)
        {
            var clickable = button.clickable;
            Assert.That(clickable, Is.Not.Null, "'submit' has no Clickable manipulator.");

            var simulate = typeof(Clickable).GetMethod(
                "SimulateSingleClick", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(simulate, Is.Not.Null, "Clickable no longer declares SimulateSingleClick.");

            using var clickEvent = ClickEvent.GetPooled();
            simulate.Invoke(clickable, new object[] { clickEvent, 0 });
        }
    }
}
