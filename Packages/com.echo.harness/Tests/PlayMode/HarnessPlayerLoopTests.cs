using System.Collections;
using Cysharp.Threading.Tasks;
using Echo.Harness.Presentation;
using NUnit.Framework;
using R3;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Echo.Harness.Tests.PlayMode
{
    public sealed class HarnessPlayerLoopTests
    {
        [UnityTest]
        public IEnumerator PresentationSeams_SurviveAPlayerLoopAndDispose()
        {
            using var state = new ReactiveProperty<string>("starting");
            var observed = string.Empty;
            using var subscription = state.Subscribe(value => observed = value);
            var viewModel = new HarnessHealthViewModel("ready");
            var root = new VisualElement { dataSource = viewModel };

            state.Value = "ready";
            var advancedThroughPlayerLoop = false;
            yield return UniTask.ToCoroutine(async () =>
            {
                await UniTask.DelayFrame(1);
                advancedThroughPlayerLoop = true;
            });

            Assert.That(advancedThroughPlayerLoop, Is.True);
            Assert.That(observed, Is.EqualTo("ready"));
            Assert.That(root.dataSource, Is.SameAs(viewModel));
            Assert.That(viewModel.Status, Is.EqualTo("ready"));
        }
    }
}
