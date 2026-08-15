using System.Collections;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Presentation;
using Echo.Harness.TestKit;
using NUnit.Framework;
using Unity.Properties;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Echo.Harness.Tests.PlayMode
{
    public sealed class LoginViewTests
    {
        // The binding is what carries every value to the screen, and a view-model
        // that updates a field nobody is bound to is the failure this catches.
        [UnityTest]
        public IEnumerator TheBindingSurvivesAPlayerLoopFrame()
        {
            var status = new FakeSessionStatus { State = SessionState.Connected };
            var session = new FakeProtocolSession();
            using var router = new SessionFaultRouter(
                session, new RecordingSessionScheduler(), new RecordingFaultLog());
            using var viewModel = new LoginViewModel(status, new FakeLoginUseCase(), router);

            var root = new VisualElement { dataSource = viewModel };
            var label = new Label();
            label.SetBinding(nameof(Label.text), new DataBinding
            {
                dataSourcePath = new PropertyPath(nameof(LoginViewModel.ConnectionStatus)),
            });
            root.Add(label);

            var advanced = false;
            yield return UniTask.ToCoroutine(async () =>
            {
                await UniTask.DelayFrame(2);
                advanced = true;
            });

            Assert.That(advanced, Is.True);
            Assert.That(root.dataSource, Is.SameAs(viewModel));
            Assert.That(viewModel.ConnectionStatus, Is.EqualTo("Connected."));
        }
    }
}
