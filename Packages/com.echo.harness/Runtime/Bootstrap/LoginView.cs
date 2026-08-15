using Cysharp.Threading.Tasks;
using Echo.Harness.Presentation;
using UnityEngine;
using UnityEngine.UIElements;
using VContainer;

namespace Echo.Harness.Bootstrap
{
    /// <summary>
    /// Binds <see cref="LoginViewModel"/> to a <see cref="UIDocument"/> and forwards
    /// the button. It holds no logic, because everything it could hold would then
    /// be unreachable from EditMode.
    ///
    /// <para>It lives in Bootstrap rather than beside its view-model because
    /// <c>Echo.Harness.Presentation</c> may not reference VContainer, so an
    /// <c>[Inject]</c> attribute cannot appear there. That split is a real cost,
    /// accepted so the container stays unreachable from every future
    /// view-model.</para>
    /// </summary>
    public sealed class LoginView : MonoBehaviour
    {
        [SerializeField] private UIDocument document;

        private LoginViewModel viewModel;

        [Inject]
        public void Construct(LoginViewModel viewModel) => this.viewModel = viewModel;

        /// <summary>
        /// Start, not OnEnable, and the reason is ordering rather than taste.
        /// VContainer's hierarchy injection completes inside
        /// <c>LifetimeScope.Awake</c>, which is guaranteed to precede Start. Nothing
        /// guarantees it precedes OnEnable, and <c>rootVisualElement</c> is null
        /// until UIDocument's own OnEnable has run - so the OnEnable version fails
        /// in one of two different ways depending on script execution order.
        /// </summary>
        private void Start()
        {
            if (document == null)
            {
                Debug.LogError(
                    "LoginView.document is unset - assign a UIDocument in the Inspector.", this);
                return;
            }

            var root = document.rootVisualElement;
            root.dataSource = viewModel;

            var submit = root.Q<Button>("submit");
            if (submit == null)
            {
                Debug.LogError(
                    "LoginView found no Button named 'submit' under the document's root - " +
                    "check that Login.uxml still names its button 'submit'.", this);
                return;
            }

            submit.clicked += OnSubmit;
        }

        private void Update()
        {
            if (viewModel == null)
            {
                Debug.LogError(
                    "LoginView.viewModel is null - VContainer injection never ran. Check that " +
                    "LoginView is still registered via RegisterComponentInHierarchy.", this);
                enabled = false;
                return;
            }

            viewModel.Refresh();
        }

        private void OnSubmit() => viewModel.SubmitAsync().Forget();
    }
}
