using Cysharp.Threading.Tasks;
using Echo.Harness.Infrastructure;
using NUnit.Framework;
using R3;
using UnityEngine.AddressableAssets;
using UnityEngine.UIElements;
using VContainer;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ThirdPartyPackageSmokeTests
    {
        [Test]
        public void SelectedHarnessPackages_AreResolvable()
        {
            Assert.That(typeof(UniTask).FullName, Is.EqualTo("Cysharp.Threading.Tasks.UniTask"));
            Assert.That(typeof(ReactiveProperty<int>).FullName, Does.Contain("ReactiveProperty"));
            Assert.That(typeof(IContainerBuilder).FullName, Does.Contain("IContainerBuilder"));
            Assert.That(typeof(Addressables).FullName, Does.Contain("Addressables"));
            Assert.That(typeof(VisualElement).FullName, Does.Contain("VisualElement"));
            Assert.That(IntegrationCapabilities.AddressablesPackageAvailable, Is.True);
            Assert.DoesNotThrow(() =>
            {
                _ = IntegrationCapabilities.XluaPackageAvailable;
            });
        }
    }
}
