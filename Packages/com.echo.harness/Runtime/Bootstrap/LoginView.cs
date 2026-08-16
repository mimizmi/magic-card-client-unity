using Cysharp.Threading.Tasks;
using Echo.Harness.Presentation;
using UnityEngine;
using UnityEngine.UIElements;
using VContainer;

namespace Echo.Harness.Bootstrap
{
    /// <summary>
    /// Binds <see cref="LoginViewModel"/> and <see cref="QueueViewModel"/> to one
    /// <see cref="UIDocument"/> and forwards the buttons. It holds no logic,
    /// because everything it could hold would then be unreachable from EditMode.
    ///
    /// <para>It lives in Bootstrap rather than beside its view-models because
    /// <c>Echo.Harness.Presentation</c> may not reference VContainer, so an
    /// <c>[Inject]</c> attribute cannot appear there. That split is a real cost,
    /// accepted so the container stays unreachable from every future
    /// view-model.</para>
    ///
    /// <para><b>Two view-models, one component, and the reason is the scene rather
    /// than the design.</b> A separate <c>QueueView</c> would mirror this class
    /// exactly and read better - but it would have to be a second MonoBehaviour in
    /// the committed <c>Assets/Scenes/Bootstrap.unity</c>, reached through
    /// <c>RegisterComponentInHierarchy</c>, and adding one means hand-writing a
    /// MonoBehaviour entry with the right script GUID into a scene file. This class
    /// is already in the scene and VContainer injects by method, so taking a second
    /// view-model here changes no serialized asset at all. <b>The split becomes
    /// worth making the moment the queue panel stops sharing this document</b> -
    /// which is also the moment the deferred UI lifetime scope comes due; see
    /// <c>HarnessLifetimeScope</c>.</para>
    ///
    /// <para>The two view-models are bound at different depths, deliberately. The
    /// login one is the document root's data source; the queue one is
    /// <c>queue-root</c>'s. UI Toolkit resolves a data-source-path against the
    /// nearest ancestor carrying a source, so each panel sees only its own
    /// properties and neither view-model has to grow the other's.</para>
    /// </summary>
    public sealed class LoginView : MonoBehaviour
    {
        [SerializeField] private UIDocument document;

        private LoginViewModel viewModel;
        private QueueViewModel queueViewModel;

        [Inject]
        public void Construct(LoginViewModel viewModel, QueueViewModel queueViewModel)
        {
            this.viewModel = viewModel;
            this.queueViewModel = queueViewModel;
        }

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

            BindQueuePanel(root);
        }

        /// <summary>
        /// Every miss below is reported rather than skipped. A queue panel that
        /// silently fails to bind looks exactly like a server that never sends a
        /// match, and that is the more expensive of the two to debug.
        /// </summary>
        private void BindQueuePanel(VisualElement root)
        {
            var queueRoot = root.Q("queue-root");
            if (queueRoot == null)
            {
                Debug.LogError(
                    "LoginView found no element named 'queue-root' under the document's " +
                    "root - check that Login.uxml still declares the queue panel.", this);
                return;
            }

            // Set on the panel rather than the root, which is what keeps the two
            // view-models from having to know about each other. See the type summary.
            queueRoot.dataSource = queueViewModel;

            var join = queueRoot.Q<Button>("join-queue");
            var leave = queueRoot.Q<Button>("leave-queue");
            if (join == null || leave == null)
            {
                Debug.LogError(
                    "LoginView needs Buttons named 'join-queue' and 'leave-queue' under " +
                    "'queue-root' - check that Login.uxml still names the queue buttons.",
                    this);
                return;
            }

            join.clicked += OnJoinQueue;
            leave.clicked += OnLeaveQueue;
        }

        private void Update()
        {
            if (viewModel == null || queueViewModel == null)
            {
                Debug.LogError(
                    "LoginView's view-models are null - VContainer injection never ran. Check " +
                    "that LoginView is still registered via RegisterComponentInHierarchy.", this);
                enabled = false;
                return;
            }

            viewModel.Refresh();
            queueViewModel.Refresh();
        }

        private void OnSubmit() => viewModel.SubmitAsync().Forget();

        private void OnJoinQueue() => queueViewModel.JoinAsync().Forget();

        private void OnLeaveQueue() => queueViewModel.LeaveAsync().Forget();
    }
}
