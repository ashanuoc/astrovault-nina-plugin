using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Astrovault;
using Astrovault.Core;
using Astrovault.Interfaces;
using Astrovault.Models;
using CloudUploadPlugin.Tests.Helpers;
using Moq;
using NINA.Profile;
using NINA.Profile.Interfaces;

namespace Astrovault.Tests.Core
{
    /// <summary>
    /// Plugin-level tests for storage-quota plan §4 Step 3 (block visibility: banners, the block-
    /// transition toast, IsUploading reset) and Step 6 (the quota usage line and its three refresh
    /// triggers). Mirrors <see cref="UploadPipelineTriggerTests"/>'s arrangement: <see
    /// cref="AstrovaultPlugin"/>'s MEF <c>[ImportingConstructor]</c> eagerly runs full NINA-host
    /// service initialization, so it is not unit-constructable -- the SUT is allocated via
    /// <see cref="RuntimeHelpers.GetUninitializedObject"/> and only the private fields the methods
    /// under test actually read are injected via reflection.
    /// <para>
    /// The Application.Current?.Dispatcher wrap around <c>OnUploadBlocked</c>/<c>OnUploadCompleted</c>
    /// is a no-op in the NUnit host (no live WPF Application), so these tests call the internal
    /// UI-thread bodies (<c>HandleUploadBlockedOnUiThread</c>/<c>HandleUploadCompletedOnUiThread</c>)
    /// directly -- the same pattern already used for <c>EnsureUploadPipelineAsync</c>/<c>ResumeUploadsAsync</c>.
    /// </para>
    /// </summary>
    [TestFixture]
    [Category("StorageQuota")]
    public class BlockVisibilityTests
    {
        private static AstrovaultPlugin CreatePlugin(
            out Mock<IAuthManager> authManager,
            out Mock<IUploadManager> uploadManager,
            out Mock<ICloudApiClient> cloudApiClient,
            NotificationVerbosity notificationVerbosity = NotificationVerbosity.ErrorsOnly)
        {
            var plugin = (AstrovaultPlugin)RuntimeHelpers.GetUninitializedObject(typeof(AstrovaultPlugin));

            var pluginGuid = Guid.NewGuid();
            var pluginSettingsStore = new PluginSettings();
            var mockProfileService = new Mock<IProfileService>();
            mockProfileService.SetupGet(ps => ps.ActiveProfile.PluginSettings).Returns(pluginSettingsStore);
            IPluginOptionsAccessor accessor = new PluginOptionsAccessor(mockProfileService.Object, pluginGuid);
            accessor.SetValueString(nameof(AstrovaultPlugin.NotificationVerbosity), notificationVerbosity.ToString());

            authManager = new Mock<IAuthManager>();
            uploadManager = new Mock<IUploadManager>();
            cloudApiClient = new Mock<ICloudApiClient>();

            SetPrivateField(plugin, "pluginSettings", accessor);
            SetPrivateField(plugin, "authManager", authManager.Object);
            SetPrivateField(plugin, "uploadManager", uploadManager.Object);
            SetPrivateField(plugin, "cloudApiClient", cloudApiClient.Object);
            // Mirrors UploadPipelineTriggerTests: GetUninitializedObject skips field initializers, so the
            // coalescing lock object (used by EnsureUploadPipelineAsync, reached via TestConnectionAsync)
            // must be seeded or the first lock(ensurePipelineGate) throws on a null reference.
            SetPrivateField(plugin, "ensurePipelineGate", new object());
            // Storage-quota plan directive B: EnsureUploadPipelineCoreAsync now also acquires the shared
            // pipelineLock (the same gate ReinitializeHttpServicesAsync holds) -- likewise seeded here.
            SetPrivateField(plugin, "pipelineLock", new SemaphoreSlim(1, 1));

            return plugin;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Field '{fieldName}' not found on {target.GetType().Name}");
            field.SetValue(target, value);
        }

        private static List<string> TrackPropertyChanges(AstrovaultPlugin plugin)
        {
            var names = new List<string>();
            plugin.PropertyChanged += (s, e) => names.Add(e.PropertyName);
            return names;
        }

        // ====================================================================
        // Step 3: banner-visibility properties
        // ====================================================================

        [Test]
        public void IsQuotaBlocked_True_WhenManagerBlockKindQuotaExceeded()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out _);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.QuotaExceeded);

            Assert.That(plugin.IsQuotaBlocked, Is.True);
            Assert.That(plugin.IsAuthBlocked, Is.False);
        }

        [Test]
        public void IsAuthBlocked_True_WhenManagerBlockKindAuthInvalid()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out _);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.AuthInvalid);

            Assert.That(plugin.IsAuthBlocked, Is.True);
            Assert.That(plugin.IsQuotaBlocked, Is.False);
        }

        [Test]
        public void IsQuotaBlocked_AndIsAuthBlocked_False_WhenNone()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out _);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.None);

            Assert.That(plugin.IsQuotaBlocked, Is.False);
            Assert.That(plugin.IsAuthBlocked, Is.False);
        }

        [Test]
        public void QuotaBlockedBannerText_WithValidatedSnapshot_UsesGiBLabeledGBNumbers()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out _);
            uploadManager.SetupGet(m => m.BlockSnapshot).Returns(new UploadBlockSnapshot
            {
                UsedBytes = 16L * 1024 * 1024 * 1024,
                LimitBytes = 10L * 1024 * 1024 * 1024
            });

            var text = plugin.QuotaBlockedBannerText;

            Assert.That(text, Does.Contain("16.0 GB"));
            Assert.That(text, Does.Contain("10.0 GB"));
            Assert.That(text, Does.Contain("storage full"));
            Assert.That(text, Does.Contain("Free up space or upgrade your plan, then resume"));
        }

        [Test]
        public void QuotaBlockedBannerText_SnapshotNull_FallsBackToGenericWording()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out _);
            uploadManager.SetupGet(m => m.BlockSnapshot).Returns((UploadBlockSnapshot)null);

            var text = plugin.QuotaBlockedBannerText;

            Assert.That(text, Does.Contain("storage full"));
            Assert.That(text, Does.Not.Contain("GB"), "No numbers were available -- must not fabricate any");
        }

        [Test]
        public void QuotaBlockedBannerText_SnapshotPartiallyUnavailable_FallsBackToGenericWording()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out _);
            // UsedBytes present, LimitBytes unavailable (e.g. an absurd/negative value on the wire) --
            // must fall back to generic wording rather than showing half a number pair.
            uploadManager.SetupGet(m => m.BlockSnapshot).Returns(new UploadBlockSnapshot
            {
                UsedBytes = 16L * 1024 * 1024 * 1024,
                LimitBytes = null
            });

            var text = plugin.QuotaBlockedBannerText;

            Assert.That(text, Does.Not.Contain("GB"));
        }

        // ====================================================================
        // Contract-delta round: PlanRequired flavor -- distinct wording, no byte numbers ever.
        // ====================================================================

        [Test]
        public void QuotaBlockedBannerText_PlanRequiredFlavor_UsesDistinctWording_NoNumbers()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out _);
            // Even though a snapshot with numbers exists, PlanRequired must never show them --
            // limitBytes is 0 for a no-plan account, so "X GB of 0.0 GB used" would be nonsense.
            uploadManager.SetupGet(m => m.BlockSnapshot).Returns(new UploadBlockSnapshot
            {
                UsedBytes = 5,
                LimitBytes = 0,
                Flavor = Astrovault.Models.QuotaBlockFlavor.PlanRequired
            });

            var text = plugin.QuotaBlockedBannerText;

            Assert.That(text, Does.Contain("no storage plan"));
            Assert.That(text, Does.Contain("Choose a plan on the AstroVault website"));
            Assert.That(text, Does.Contain("Frames are saved locally"));
            Assert.That(text, Does.Not.Contain("GB"), "PlanRequired must never show byte numbers -- limitBytes is 0 for this flavor");
            Assert.That(text, Does.Not.Contain("Free up space"), "Free up space is a dead end for a no-plan account");
        }

        [Test]
        public void QuotaBlockedBannerText_StorageLimitExceededFlavor_Explicit_UnchangedWording()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out _);
            uploadManager.SetupGet(m => m.BlockSnapshot).Returns(new UploadBlockSnapshot
            {
                UsedBytes = 16L * 1024 * 1024 * 1024,
                LimitBytes = 10L * 1024 * 1024 * 1024,
                Flavor = Astrovault.Models.QuotaBlockFlavor.StorageLimitExceeded
            });

            var text = plugin.QuotaBlockedBannerText;

            Assert.That(text, Does.Contain("16.0 GB"));
            Assert.That(text, Does.Contain("10.0 GB"));
            Assert.That(text, Does.Contain("Free up space or upgrade your plan, then resume"));
            Assert.That(text, Does.Not.Contain("no storage plan"));
        }

        [Test]
        public void QuotaBlockedBannerText_StorageLimitExceededFlavor_ZeroLimitBytes_FallsBackToGenericWording_NoNumbers()
        {
            // Pass H P2 regression: LimitBytes must also be POSITIVE for the numbers branch, not merely
            // present. A no-plan account whose 507 arrived without a recognizable flavor code still
            // persists a StorageLimitExceeded-flavored snapshot with UsedBytes=0/LimitBytes=0 -- showing
            // "0.0 GB of 0.0 GB used" would be forbidden nonsense output, so this must fall to the same
            // generic wording as an unavailable snapshot.
            var plugin = CreatePlugin(out _, out var uploadManager, out _);
            uploadManager.SetupGet(m => m.BlockSnapshot).Returns(new UploadBlockSnapshot
            {
                UsedBytes = 0,
                LimitBytes = 0,
                Flavor = Astrovault.Models.QuotaBlockFlavor.StorageLimitExceeded
            });

            var text = plugin.QuotaBlockedBannerText;

            Assert.That(text, Does.Contain("storage full"));
            Assert.That(text, Does.Contain("Free up space or upgrade your plan, then resume"));
            Assert.That(text, Does.Not.Contain("GB"), "LimitBytes==0 must never produce \"0.0 GB of 0.0 GB\" -- fall back to generic wording with no numbers");
        }

        [Test]
        public void ShowBlockTransitionToast_QuotaExceeded_PlanRequiredFlavor_UsesDistinctWording()
        {
            var plugin = CreatePlugin(out _, out _, out _);
            var calls = new List<string>();
            SetPrivateField(plugin, "showErrorToast", (Action<string>)(text => calls.Add(text)));

            plugin.ShowBlockTransitionToast(UploadBlockKind.QuotaExceeded, Astrovault.Models.QuotaBlockFlavor.PlanRequired);

            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0], Does.Contain("no storage plan"));
            Assert.That(calls[0], Does.Not.Contain("storage full"));
        }

        [Test]
        public void ShowBlockTransitionToast_QuotaExceeded_DefaultFlavorParam_UnchangedStorageFullWording()
        {
            // No flavor argument supplied -- existing call sites/tests must be completely unaffected.
            var plugin = CreatePlugin(out _, out _, out _);
            var calls = new List<string>();
            SetPrivateField(plugin, "showErrorToast", (Action<string>)(text => calls.Add(text)));

            plugin.ShowBlockTransitionToast(UploadBlockKind.QuotaExceeded);

            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0], Does.Contain("storage full"));
        }

        [Test]
        public void HandleUploadBlockedOnUiThread_PlanRequiredFlavor_ToastUsesDistinctWording()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out var cloudApiClient);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.QuotaExceeded);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync((QuotaUsage)null);
            var calls = new List<string>();
            SetPrivateField(plugin, "showErrorToast", (Action<string>)(text => calls.Add(text)));

            plugin.HandleUploadBlockedOnUiThread(new UploadBlockedEventArgs
            {
                Kind = UploadBlockKind.QuotaExceeded,
                Snapshot = new UploadBlockSnapshot { Flavor = Astrovault.Models.QuotaBlockFlavor.PlanRequired }
            });

            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0], Does.Contain("no storage plan"),
                "The Snapshot's Flavor from the live transition event must reach the toast");
        }

        [Test]
        public void HandleUploadBlockedOnUiThread_NullSnapshot_DefaultsToStorageLimitExceededWording()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out var cloudApiClient);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.QuotaExceeded);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync((QuotaUsage)null);
            var calls = new List<string>();
            SetPrivateField(plugin, "showErrorToast", (Action<string>)(text => calls.Add(text)));

            plugin.HandleUploadBlockedOnUiThread(new UploadBlockedEventArgs { Kind = UploadBlockKind.QuotaExceeded, Snapshot = null });

            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0], Does.Contain("storage full"));
        }

        // ====================================================================
        // Contract-delta round: usage line hidden when limitBytes == 0 (no-plan account on the
        // always-200 quota endpoint).
        // ====================================================================

        [Test]
        public async Task RefreshQuotaUsageAsync_LimitBytesZero_HasQuotaUsageFalse_TextEmpty()
        {
            var plugin = CreatePlugin(out _, out _, out var cloudApiClient);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new QuotaUsage
            {
                AccountId = "acct-1",
                PlanName = null,
                UsedBytes = 0,
                LimitBytes = 0
            });

            await plugin.RefreshQuotaUsageAsync();

            Assert.That(plugin.HasQuotaUsage, Is.False,
                "limitBytes==0 must hide the usage line -- 'X GB of 0.0 GB used' is a meaningless fraction");
            Assert.That(plugin.QuotaUsageText, Is.Empty);
        }

        [Test]
        public async Task RefreshQuotaUsageAsync_LimitBytesNonZero_HasQuotaUsageTrue()
        {
            var plugin = CreatePlugin(out _, out _, out var cloudApiClient);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new QuotaUsage
            {
                AccountId = "acct-1",
                UsedBytes = 1,
                LimitBytes = 2
            });

            await plugin.RefreshQuotaUsageAsync();

            Assert.That(plugin.HasQuotaUsage, Is.True, "Regression guard: a normal nonzero limit must still show the line");
        }

        [Test]
        public void AuthBlockedBannerText_IsFixedWording()
        {
            var plugin = CreatePlugin(out _, out _, out _);

            Assert.That(plugin.AuthBlockedBannerText, Does.Contain("API key rejected"));
            Assert.That(plugin.AuthBlockedBannerText, Does.Contain("Astrovault options"));
        }

        [Test]
        public void HasBlockPersistFailureWarning_DelegatesToManager()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out _);
            uploadManager.SetupGet(m => m.HasBlockPersistFailureWarning).Returns(true);

            Assert.That(plugin.HasBlockPersistFailureWarning, Is.True);
        }

        [Test]
        public void ShowCircuitOpenBanner_True_WhenCircuitOpenAndNotBlocked()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out _);
            uploadManager.SetupGet(m => m.IsCircuitOpen).Returns(true);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.None);

            Assert.That(plugin.ShowCircuitOpenBanner, Is.True);
        }

        [Test]
        public void ShowCircuitOpenBanner_False_WhenQuotaBlocked_EvenIfCircuitOpen()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out _);
            uploadManager.SetupGet(m => m.IsCircuitOpen).Returns(true);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.QuotaExceeded);

            Assert.That(plugin.ShowCircuitOpenBanner, Is.False,
                "Block banners take visual precedence -- the circuit banner must not show simultaneously");
        }

        [Test]
        public void ShowCircuitOpenBanner_False_WhenAuthBlocked_EvenIfCircuitOpen()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out _);
            uploadManager.SetupGet(m => m.IsCircuitOpen).Returns(true);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.AuthInvalid);

            Assert.That(plugin.ShowCircuitOpenBanner, Is.False);
        }

        // ====================================================================
        // Step 3: ShowBlockTransitionToast -- the seam, unconditional of verbosity/circuit state.
        // ====================================================================

        [Test]
        public void ShowBlockTransitionToast_QuotaExceeded_InvokesSeamOnce_WithFixedLocalText()
        {
            var plugin = CreatePlugin(out _, out _, out _);
            var calls = new List<string>();
            SetPrivateField(plugin, "showErrorToast", (Action<string>)(text => calls.Add(text)));

            plugin.ShowBlockTransitionToast(UploadBlockKind.QuotaExceeded);

            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0], Does.Contain("storage full"));
        }

        [Test]
        public void ShowBlockTransitionToast_AuthInvalid_InvokesSeamOnce_WithFixedLocalText()
        {
            var plugin = CreatePlugin(out _, out _, out _);
            var calls = new List<string>();
            SetPrivateField(plugin, "showErrorToast", (Action<string>)(text => calls.Add(text)));

            plugin.ShowBlockTransitionToast(UploadBlockKind.AuthInvalid);

            Assert.That(calls, Has.Count.EqualTo(1));
            Assert.That(calls[0], Does.Contain("API key rejected"));
        }

        [Test]
        public void ShowBlockTransitionToast_None_NeverInvokesSeam()
        {
            var plugin = CreatePlugin(out _, out _, out _);
            var calls = new List<string>();
            SetPrivateField(plugin, "showErrorToast", (Action<string>)(text => calls.Add(text)));

            plugin.ShowBlockTransitionToast(UploadBlockKind.None);

            Assert.That(calls, Is.Empty);
        }

        [TestCase(NotificationVerbosity.Off)]
        [TestCase(NotificationVerbosity.ErrorsOnly)]
        [TestCase(NotificationVerbosity.AllUploads)]
        public void ShowBlockTransitionToast_FiresRegardlessOfNotificationVerbosity(NotificationVerbosity verbosity)
        {
            // The block-transition toast is unconditional -- it bypasses the verbosity gating that the
            // per-upload SendNotification path applies (including Off, the most restrictive setting: a
            // stopped upload pipeline is categorically more important than routine per-file noise).
            var plugin = CreatePlugin(out _, out _, out _, notificationVerbosity: verbosity);
            var calls = new List<string>();
            SetPrivateField(plugin, "showErrorToast", (Action<string>)(text => calls.Add(text)));

            plugin.ShowBlockTransitionToast(UploadBlockKind.QuotaExceeded);

            Assert.That(calls, Has.Count.EqualTo(1),
                $"The block toast must fire even at NotificationVerbosity.{verbosity}");
        }

        [Test]
        public void ShowBlockTransitionToast_SeamThrows_DoesNotPropagate()
        {
            var plugin = CreatePlugin(out _, out _, out _);
            SetPrivateField(plugin, "showErrorToast", (Action<string>)(_ => throw new InvalidOperationException("boom")));

            Assert.DoesNotThrow(() => plugin.ShowBlockTransitionToast(UploadBlockKind.QuotaExceeded));
        }

        // ====================================================================
        // Step 3: HandleUploadBlockedOnUiThread -- IsUploading reset, toast-once, property refresh.
        // ====================================================================

        [Test]
        public void HandleUploadBlockedOnUiThread_EndsIsUploading()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out var cloudApiClient);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.QuotaExceeded);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync((QuotaUsage)null);
            SetPrivateField(plugin, "showErrorToast", (Action<string>)(_ => { }));
            // Simulate an in-flight upload that got interrupted by the block.
            SetPrivateField(plugin, "isUploading", true);

            plugin.HandleUploadBlockedOnUiThread(new UploadBlockedEventArgs { Kind = UploadBlockKind.QuotaExceeded });

            Assert.That(plugin.IsUploading, Is.False,
                "A blocked job never reaches OnUploadCompleted -- IsUploading must be explicitly ended here");
        }

        [Test]
        public void HandleUploadBlockedOnUiThread_FiresToastExactlyOnce()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out var cloudApiClient);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.AuthInvalid);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync((QuotaUsage)null);
            var calls = new List<string>();
            SetPrivateField(plugin, "showErrorToast", (Action<string>)(text => calls.Add(text)));

            plugin.HandleUploadBlockedOnUiThread(new UploadBlockedEventArgs { Kind = UploadBlockKind.AuthInvalid });

            Assert.That(calls, Has.Count.EqualTo(1), "Exactly one toast per live transition");
        }

        [Test]
        public void HandleUploadBlockedOnUiThread_RaisesBlockVisibilityPropertyChanges()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out var cloudApiClient);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.QuotaExceeded);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync((QuotaUsage)null);
            SetPrivateField(plugin, "showErrorToast", (Action<string>)(_ => { }));
            var raised = TrackPropertyChanges(plugin);

            plugin.HandleUploadBlockedOnUiThread(new UploadBlockedEventArgs { Kind = UploadBlockKind.QuotaExceeded });

            Assert.That(raised, Does.Contain(nameof(AstrovaultPlugin.IsQuotaBlocked)));
            Assert.That(raised, Does.Contain(nameof(AstrovaultPlugin.QuotaBlockedBannerText)));
            Assert.That(raised, Does.Contain(nameof(AstrovaultPlugin.IsUploading)));
        }

        [Test]
        public async Task HandleUploadBlockedOnUiThread_TriggersQuotaUsageRefresh()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out var cloudApiClient);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.QuotaExceeded);
            SetPrivateField(plugin, "showErrorToast", (Action<string>)(_ => { }));
            var refreshCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    refreshCalled.TrySetResult(true);
                    return (QuotaUsage)null;
                });

            plugin.HandleUploadBlockedOnUiThread(new UploadBlockedEventArgs { Kind = UploadBlockKind.QuotaExceeded });

            await refreshCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cloudApiClient.Verify(c => c.GetQuotaAsync(It.IsAny<CancellationToken>()), Times.Once,
                "A block transition is one of the three allowed quota-usage refresh triggers");
        }

        // ====================================================================
        // Storage-quota plan directive I (B#9): wiring-level regression guard.
        // ====================================================================

        /// <summary>
        /// Every OTHER test in this fixture allocates the plugin via GetUninitializedObject and calls
        /// <c>HandleUploadBlockedOnUiThread</c> DIRECTLY, bypassing the event subscription entirely. A
        /// test suite built only that way would still pass if production's
        /// <c>uploadManager.UploadBlocked += OnUploadBlocked;</c> subscription (in
        /// <c>InitializeServicesCore</c>) were ever deleted, renamed, or given an incompatible handler
        /// signature -- the review finding this closes. This test instead subscribes the plugin's REAL
        /// private <c>OnUploadBlocked</c> method to a REAL <see cref="UploadManager"/>'s REAL event
        /// (mirroring the production subscription exactly, via reflection since the target is private)
        /// and raises a genuine block transition, proving the delegate binds and the raise-to-handler
        /// path completes without an unhandled exception.
        /// <para>
        /// HONEST LIMIT: <c>Application.Current</c> is null in the NUnit host, so
        /// <c>Application.Current?.Dispatcher?.BeginInvoke(...)</c> inside <c>OnUploadBlocked</c> is a
        /// no-op here -- this test cannot observe <c>HandleUploadBlockedOnUiThread</c>'s UI-thread
        /// effects (IsUploading, the toast, RefreshQueueUI) through this real event path; those are
        /// covered directly by the other tests in this fixture. This test's job is narrower and
        /// different: prove the WIRING itself -- the subscription target still exists, still matches the
        /// event's delegate signature, and the manager-to-plugin raise completes cleanly -- survives a
        /// refactor. The UI-thread marshaling itself remains uncoverable without a live WPF Application
        /// (matches this project's existing, already-documented testing limit for every Dispatcher-wrapped
        /// handler in this class).
        /// </para>
        /// </summary>
        [Test]
        public void UploadBlocked_WiringLevel_RealEventSubscription_RaiseDoesNotThrow()
        {
            var plugin = CreatePlugin(out _, out _, out _);

            var queueRepo = new Mock<IUploadQueueRepository>();
            queueRepo.Setup(r => r.SetBlockAsync(
                    It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(), It.IsAny<UploadBlockIdentityInput>()))
                .ReturnsAsync(true);
            queueRepo.Setup(r => r.GetJobCountAsync(It.IsAny<UploadStatus>())).ReturnsAsync(0);
            var authManager = new Mock<IAuthManager>();
            authManager.Setup(a => a.GetApiKey()).Returns("test-api-key");
            var apiClient = new Mock<ICloudApiClient>();
            apiClient.Setup(c => c.BaseUrl).Returns("https://api.astrovault.io");
            var realManager = new UploadManager(queueRepo.Object, apiClient.Object, authManager.Object);

            var onUploadBlockedMethod = typeof(AstrovaultPlugin).GetMethod("OnUploadBlocked", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(onUploadBlockedMethod, Is.Not.Null, "The production wiring target must still exist under this exact name");
            var handler = (EventHandler<UploadBlockedEventArgs>)Delegate.CreateDelegate(
                typeof(EventHandler<UploadBlockedEventArgs>), plugin, onUploadBlockedMethod!);

            // Mirrors InitializeServicesCore's real subscription exactly.
            realManager.UploadBlocked += handler;

            var job = TestDataFactory.CreateUploadJob();
            Assert.DoesNotThrowAsync(async () => await realManager.HandleUploadFailure(job,
                    UploadResult.Fail("Insufficient storage", 507, FailureKind.PermanentQuota, new QuotaInfo()),
                    CancellationToken.None),
                "Raising a real block transition through the real event subscription must not throw end-to-end");
        }

        // ====================================================================
        // Storage-quota plan directive I (C review B#9 residual): source-scan pin for the PRODUCTION
        // subscription itself -- distinct from the wiring-level test above, which creates ITS OWN
        // subscription and would still pass even if InitializeServicesCore's real
        // `uploadManager.UploadBlocked += OnUploadBlocked;` line were deleted, renamed, or pointed at a
        // different handler. This test instead reads AstrovaultPlugin.cs as TEXT and asserts the real
        // production wiring line is present, anchored to the exact member names (resilient to
        // whitespace), in the style of the existing ApiClientRedactionTests/SecurityScanTests source
        // scanners. Not beautiful, but it pins the contract the runtime tests above can't reach without
        // a live WPF Application.
        // ====================================================================

        [Test]
        public void ProductionSource_SubscribesUploadBlockedToOnUploadBlocked()
        {
            var root = LocateRepoRoot();
            var pluginSource = File.ReadAllText(Path.Combine(root, "CloudUploadPlugin", "AstrovaultPlugin.cs"));

            var wired = Regex.IsMatch(pluginSource,
                @"uploadManager\s*\.\s*UploadBlocked\s*\+=\s*OnUploadBlocked\s*;");

            Assert.That(wired, Is.True,
                "AstrovaultPlugin.InitializeServicesCore must subscribe uploadManager.UploadBlocked to "
                + "OnUploadBlocked -- deleting/renaming this production wiring line would otherwise pass "
                + "every other test in this fixture (they all invoke the handler directly or create their "
                + "own subscription).");
        }

        private static string LocateRepoRoot()
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir != null)
            {
                // .git is a directory in a normal clone but a file in a git worktree
                if (File.Exists(Path.Combine(dir.FullName, "CloudUploadPlugin.sln"))
                    || Directory.Exists(Path.Combine(dir.FullName, ".git"))
                    || File.Exists(Path.Combine(dir.FullName, ".git")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            throw new InvalidOperationException(
                "Could not locate repo root (no CloudUploadPlugin.sln or .git found above the test dir).");
        }

        // ====================================================================
        // Step 6: HandleUploadCompletedOnUiThread -- the "queue transitioning to idle" trigger.
        // ====================================================================

        [Test]
        public async Task HandleUploadCompletedOnUiThread_QueueIdleAfterCompletion_TriggersQuotaRefresh()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out var cloudApiClient);
            uploadManager.SetupGet(m => m.PendingCount).Returns(0);
            uploadManager.SetupGet(m => m.UploadingCount).Returns(0);
            var refreshCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>()))
                .Returns(async () => { refreshCalled.TrySetResult(true); return (QuotaUsage)null; });

            plugin.HandleUploadCompletedOnUiThread(new UploadCompletedEventArgs { Success = true, FileName = "a.fits" });

            await refreshCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cloudApiClient.Verify(c => c.GetQuotaAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void HandleUploadCompletedOnUiThread_QueueStillActive_DoesNotTriggerQuotaRefresh()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out var cloudApiClient);
            uploadManager.SetupGet(m => m.PendingCount).Returns(3); // still work left
            uploadManager.SetupGet(m => m.UploadingCount).Returns(0);

            plugin.HandleUploadCompletedOnUiThread(new UploadCompletedEventArgs { Success = true, FileName = "a.fits" });

            cloudApiClient.Verify(c => c.GetQuotaAsync(It.IsAny<CancellationToken>()), Times.Never,
                "The queue has not gone idle -- this completion must not trigger a quota refresh");
        }

        [Test]
        public void HandleUploadCompletedOnUiThread_EndsIsUploading()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out var cloudApiClient);
            uploadManager.SetupGet(m => m.PendingCount).Returns(1);
            uploadManager.SetupGet(m => m.UploadingCount).Returns(0);
            SetPrivateField(plugin, "isUploading", true);

            plugin.HandleUploadCompletedOnUiThread(new UploadCompletedEventArgs { Success = true, FileName = "a.fits" });

            Assert.That(plugin.IsUploading, Is.False);
        }

        // ====================================================================
        // Step 6: RefreshQuotaUsageAsync / usage-line properties / accountId threading.
        // ====================================================================

        [Test]
        public async Task RefreshQuotaUsageAsync_Success_UpdatesUsageLineProperties()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out var cloudApiClient);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new QuotaUsage
            {
                AccountId = "acct-1",
                PlanName = "Pro",
                UsedBytes = (long)(12.4 * 1024 * 1024 * 1024),
                LimitBytes = 10L * 1024 * 1024 * 1024
            });

            await plugin.RefreshQuotaUsageAsync();

            Assert.That(plugin.HasQuotaUsage, Is.True);
            Assert.That(plugin.QuotaUsageText, Does.Contain("12.40 GB"));
            Assert.That(plugin.QuotaUsageText, Does.Contain("10.00 GB"));
            Assert.That(plugin.QuotaUsageText, Does.Contain("(Pro)"));
        }

        [Test]
        public async Task RefreshQuotaUsageAsync_NearLimit_TwoDecimals_AndFreeSuffix()
        {
            // Owner request (dev-testing round): one decimal was blind near the limit -- 9.96/10.00
            // rendered as "10.0 of 10.0" while ~40 MB decided whether the next frame fits. Two
            // decimals plus an explicit free-space suffix below 512 MiB.
            var plugin = CreatePlugin(out _, out _, out var cloudApiClient);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new QuotaUsage
            {
                AccountId = "acct-1",
                PlanName = "FREE",
                UsedBytes = 10691051040,          // 9.96 GiB
                LimitBytes = 10737418240,         // 10.00 GiB
                RemainingBytes = 46367200         // ~44 MB
            });

            await plugin.RefreshQuotaUsageAsync();

            Assert.That(plugin.QuotaUsageText, Does.Contain("9.96 GB of 10.00 GB used"));
            Assert.That(plugin.QuotaUsageText, Does.Contain("44 MB free"));
        }

        [Test]
        public async Task RefreshQuotaUsageAsync_PlentyFree_NoFreeSuffix()
        {
            var plugin = CreatePlugin(out _, out _, out var cloudApiClient);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new QuotaUsage
            {
                AccountId = "acct-1",
                PlanName = "FREE",
                UsedBytes = 5L * 1024 * 1024 * 1024,
                LimitBytes = 10L * 1024 * 1024 * 1024
            });

            await plugin.RefreshQuotaUsageAsync();

            Assert.That(plugin.QuotaUsageText, Does.Not.Contain("free"),
                "The free-space suffix appears only when remaining drops under 512 MiB");
        }

        [Test]
        public async Task RefreshQuotaUsageAsync_NoPlanName_NoParenSuffix()
        {
            var plugin = CreatePlugin(out _, out _, out var cloudApiClient);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new QuotaUsage
            {
                AccountId = "acct-1",
                PlanName = null,
                UsedBytes = 5L * 1024 * 1024 * 1024,
                LimitBytes = 10L * 1024 * 1024 * 1024
            });

            await plugin.RefreshQuotaUsageAsync();

            Assert.That(plugin.QuotaUsageText, Does.Not.Contain("("));
        }

        [Test]
        public async Task RefreshQuotaUsageAsync_NullUsage_HasQuotaUsageFalse_TextEmpty()
        {
            var plugin = CreatePlugin(out _, out _, out var cloudApiClient);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync((QuotaUsage)null);

            await plugin.RefreshQuotaUsageAsync();

            Assert.That(plugin.HasQuotaUsage, Is.False);
            Assert.That(plugin.QuotaUsageText, Is.Empty);
        }

        [Test]
        public void HasQuotaUsage_NeverFetched_False()
        {
            var plugin = CreatePlugin(out _, out _, out _);

            Assert.That(plugin.HasQuotaUsage, Is.False);
            Assert.That(plugin.QuotaUsageText, Is.Empty);
        }

        [Test]
        public async Task RefreshQuotaUsageAsync_Success_ThreadsAccountIdIntoManager()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out var cloudApiClient);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new QuotaUsage
            {
                AccountId = "acct-42",
                UsedBytes = 1,
                LimitBytes = 2
            });

            await plugin.RefreshQuotaUsageAsync();

            // Storage-quota plan directive E: the generation-guarded overload is used for this real
            // async fetch path so a context change mid-flight can be recognized and dropped -- the
            // caller doesn't know/care about the exact generation value here.
            uploadManager.Verify(m => m.SetAccountIdAsync("acct-42", It.IsAny<int>()), Times.Once,
                "A successful fetch must thread the account id into the manager's block identity scoping");
        }

        [Test]
        public async Task RefreshQuotaUsageAsync_NullAccountId_DoesNotCallSetAccountId()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out var cloudApiClient);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new QuotaUsage
            {
                AccountId = null,
                UsedBytes = 1,
                LimitBytes = 2
            });

            await plugin.RefreshQuotaUsageAsync();

            uploadManager.Verify(m => m.SetAccountIdAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        }

        [Test]
        public async Task RefreshQuotaUsageAsync_NullUsage_DoesNotCallSetAccountId()
        {
            var plugin = CreatePlugin(out _, out var uploadManager, out var cloudApiClient);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync((QuotaUsage)null);

            await plugin.RefreshQuotaUsageAsync();

            uploadManager.Verify(m => m.SetAccountIdAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        }

        // ====================================================================
        // Step 6: TestConnectionAsync success is an allowed refresh trigger.
        // ====================================================================

        [Test]
        public async Task TestConnectionAsync_Success_TriggersQuotaUsageRefresh()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, out var cloudApiClient);
            authManager.Setup(a => a.ValidateApiKeyAsync(It.IsAny<string>()))
                .ReturnsAsync(new ApiKeyValidationResult { Valid = true, AccountName = "acct" });
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.None);
            uploadManager.SetupGet(m => m.IsRunning).Returns(true);
            cloudApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync((QuotaUsage)null);

            plugin.ApiKeyInput = "some-key";
            await plugin.TestConnectionAsync();

            cloudApiClient.Verify(c => c.GetQuotaAsync(It.IsAny<CancellationToken>()), Times.Once,
                "A successful Test-Connection is one of the three allowed quota-usage refresh triggers");
        }

        [Test]
        public async Task TestConnectionAsync_Failure_DoesNotTriggerQuotaUsageRefresh()
        {
            var plugin = CreatePlugin(out var authManager, out _, out var cloudApiClient);
            authManager.Setup(a => a.ValidateApiKeyAsync(It.IsAny<string>()))
                .ReturnsAsync(new ApiKeyValidationResult { Valid = false, ErrorMessage = "Invalid API key" });

            plugin.ApiKeyInput = "bad-key";
            await plugin.TestConnectionAsync();

            cloudApiClient.Verify(c => c.GetQuotaAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        // ====================================================================
        // No polling: nothing in this class schedules a timer that calls GetQuotaAsync on its own.
        // ====================================================================

        [Test]
        public async Task NoTriggerFired_GetQuotaAsyncNeverCalled_EvenAfterAShortWait()
        {
            // Proxy for "no polling/timers": construct the plugin and simply wait -- if any hidden
            // timer/background loop called GetQuotaAsync on its own, this would observe it.
            var plugin = CreatePlugin(out _, out _, out var cloudApiClient);

            await Task.Delay(150);

            cloudApiClient.Verify(c => c.GetQuotaAsync(It.IsAny<CancellationToken>()), Times.Never,
                "The usage line must only refresh on an explicit trigger, never a background timer");
        }
    }
}
