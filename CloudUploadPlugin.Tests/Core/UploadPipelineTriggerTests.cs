using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Astrovault;
using Astrovault.Interfaces;
using Astrovault.Models;
using Moq;
using NINA.Profile;
using NINA.Profile.Interfaces;

namespace Astrovault.Tests.Core
{
    /// <summary>
    /// Plugin-level tests for storage-quota plan §4 Steps 4/5: the single coalesced
    /// <c>EnsureUploadPipelineAsync</c> choke point (G7 fix), the kind-gated <c>ResumeUploadsAsync</c>
    /// entry point, the <c>UploadEnabled</c> false-&gt;true transition wrapper, and TestConnectionAsync's
    /// success-path AuthInvalid clear. Covers step-7 test table rows 4 (toggle portion), 5 (gate
    /// portion), and 12 (every step-4 trigger restarts a stopped-but-enabled+configured manager).
    /// <para>
    /// Arrangement note (mirrors <see cref="DisconnectConsentTests"/>): <see cref="AstrovaultPlugin"/>
    /// exposes only a MEF <c>[ImportingConstructor]</c> that eagerly runs full service initialization
    /// against NINA host types -- not unit-constructable. The SUT is allocated without a constructor
    /// (<see cref="RuntimeHelpers.GetUninitializedObject"/>) and only the private fields the methods
    /// under test actually read are injected: <c>pluginSettings</c> (a real
    /// <see cref="PluginOptionsAccessor"/> over an in-memory <see cref="PluginSettings"/> so
    /// <c>UploadEnabled</c> round-trips), <c>authManager</c> (Moq), and <c>uploadManager</c> (Moq).
    /// Fire-and-forget assertions (the UploadEnabled setter) use a
    /// <see cref="TaskCompletionSource{TResult}"/> signaled from inside a mock callback and a bounded
    /// <c>WaitAsync</c> -- the same deterministic synchronization pattern already used by
    /// <c>ShutdownLifecycleTests.FlushPendingAsync_AwaitsInFlightEnqueue_SoCaptureReachesDisk</c>.
    /// </para>
    /// </summary>
    [TestFixture]
    [Category("StorageQuota")]
    public class UploadPipelineTriggerTests
    {
        private static AstrovaultPlugin CreatePlugin(
            out Mock<IAuthManager> authManager,
            out Mock<IUploadManager> uploadManager,
            bool initialUploadEnabled = false)
        {
            var plugin = (AstrovaultPlugin)RuntimeHelpers.GetUninitializedObject(typeof(AstrovaultPlugin));

            var pluginGuid = Guid.NewGuid();
            var pluginSettingsStore = new PluginSettings();
            var mockProfileService = new Mock<IProfileService>();
            mockProfileService.SetupGet(ps => ps.ActiveProfile.PluginSettings).Returns(pluginSettingsStore);
            IPluginOptionsAccessor accessor = new PluginOptionsAccessor(mockProfileService.Object, pluginGuid);
            accessor.SetValueBoolean(nameof(AstrovaultPlugin.UploadEnabled), initialUploadEnabled);

            authManager = new Mock<IAuthManager>();
            uploadManager = new Mock<IUploadManager>();

            SetPrivateField(plugin, "pluginSettings", accessor);
            SetPrivateField(plugin, "authManager", authManager.Object);
            SetPrivateField(plugin, "uploadManager", uploadManager.Object);
            // RuntimeHelpers.GetUninitializedObject skips field initializers -- EnsureUploadPipelineAsync's
            // coalescing lock object is normally initialized by the (never-run) constructor, so it must be
            // seeded here or the very first lock(ensurePipelineGate) throws on a null reference.
            SetPrivateField(plugin, "ensurePipelineGate", new object());
            // Storage-quota plan directive B: EnsureUploadPipelineCoreAsync now also acquires the shared
            // pipelineLock (the same gate ReinitializeHttpServicesAsync holds) -- likewise never
            // constructor-initialized here, so it must be seeded too.
            SetPrivateField(plugin, "pipelineLock", new SemaphoreSlim(1, 1));

            return plugin;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null,
                $"Field '{fieldName}' not found on {target.GetType().Name}");
            field.SetValue(target, value);
        }

        // ====================================================================
        // EnsureUploadPipelineAsync -- the single choke point (G7 fix)
        // ====================================================================

        [Test]
        public async Task EnsureUploadPipelineAsync_EnabledConfiguredNotRunning_Starts()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);
            uploadManager.Setup(m => m.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            await plugin.EnsureUploadPipelineAsync();

            uploadManager.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task EnsureUploadPipelineAsync_UploadDisabled_DoesNotStart()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: false);
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);

            await plugin.EnsureUploadPipelineAsync();

            uploadManager.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task EnsureUploadPipelineAsync_NotConfigured_DoesNotStart()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);
            authManager.Setup(a => a.IsConfigured).Returns(false);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);

            await plugin.EnsureUploadPipelineAsync();

            uploadManager.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task EnsureUploadPipelineAsync_AlreadyRunning_DoesNotStartAgain()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.IsRunning).Returns(true);

            await plugin.EnsureUploadPipelineAsync();

            uploadManager.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public void EnsureUploadPipelineAsync_StartAsyncThrows_ExceptionCaught_TaskCompletesSuccessfully()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);
            uploadManager.Setup(m => m.StartAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("boom"));

            Task resultTask = null;
            Assert.That(() => resultTask = plugin.EnsureUploadPipelineAsync(), Throws.Nothing,
                "EnsureUploadPipelineAsync itself must never throw synchronously");
            Assert.That(async () => await resultTask, Throws.Nothing,
                "The exception must be caught internally (logged + surfaced via connectionStatus) and never propagate to the caller");
        }

        [Test]
        public async Task EnsureUploadPipelineAsync_ConcurrentCalls_Coalesce_StartAsyncInvokedExactlyOnce()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);

            var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var startEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var startCallCount = 0;
            uploadManager.Setup(m => m.StartAsync(It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    Interlocked.Increment(ref startCallCount);
                    startEntered.TrySetResult(true);
                    await startGate.Task;
                });

            var task1 = plugin.EnsureUploadPipelineAsync();
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // StartAsync is now in flight, gated open

            var task2 = plugin.EnsureUploadPipelineAsync(); // concurrent call while task1 is still in flight

            Assert.That(task2, Is.SameAs(task1), "A concurrent caller must observe and await the SAME in-flight task");

            startGate.SetResult(true); // release StartAsync
            await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.That(startCallCount, Is.EqualTo(1),
                "Concurrent EnsureUploadPipelineAsync calls must coalesce -- StartAsync invoked exactly once, no double-start");
        }

        [Test]
        public async Task EnsureUploadPipelineAsync_SequentialCalls_AfterFirstCompletes_CanStartAgain()
        {
            // Coalescing must only dedupe TRULY concurrent (overlapping) calls -- once the in-flight
            // task completes, a later call is a fresh evaluation, not silently swallowed.
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);
            authManager.Setup(a => a.IsConfigured).Returns(true);
            var isRunning = false;
            uploadManager.SetupGet(m => m.IsRunning).Returns(() => isRunning);
            uploadManager.Setup(m => m.StartAsync(It.IsAny<CancellationToken>()))
                .Callback(() => isRunning = true)
                .Returns(Task.CompletedTask);

            await plugin.EnsureUploadPipelineAsync();
            uploadManager.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Once);

            // Simulate the manager stopping again (e.g. a later Disconnect) and a fresh ensure call.
            isRunning = false;
            await plugin.EnsureUploadPipelineAsync();

            uploadManager.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        // ====================================================================
        // ResumeUploadsAsync -- the KIND GATE (round-2 P1)
        // ====================================================================

        [Test]
        public async Task ResumeUploadsAsync_QuotaExceeded_CallsManagerResumeAsync()
        {
            var plugin = CreatePlugin(out _, out var uploadManager);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.QuotaExceeded);
            uploadManager.Setup(m => m.ResumeAsync()).ReturnsAsync(true);

            await plugin.ResumeUploadsAsync();

            uploadManager.Verify(m => m.ResumeAsync(), Times.Once);
        }

        [Test]
        public async Task ResumeUploadsAsync_AuthInvalid_NeverCallsResumeAsync()
        {
            var plugin = CreatePlugin(out _, out var uploadManager);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.AuthInvalid);

            await plugin.ResumeUploadsAsync();

            uploadManager.Verify(m => m.ResumeAsync(), Times.Never,
                "AuthInvalid must never be cleared by the generic Resume entry point -- only a validated key can clear it");
        }

        [Test]
        public async Task ResumeUploadsAsync_None_NeverCallsResumeAsync()
        {
            var plugin = CreatePlugin(out _, out var uploadManager);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.None);

            await plugin.ResumeUploadsAsync();

            uploadManager.Verify(m => m.ResumeAsync(), Times.Never);
        }

        // ====================================================================
        // UploadEnabled false->true transition wrapper
        // ====================================================================

        [Test]
        public async Task UploadEnabled_FalseToTrue_BlockKindNone_StartsPipeline_NeverCallsResume()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: false);
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.None);

            var startCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            uploadManager.Setup(m => m.StartAsync(It.IsAny<CancellationToken>()))
                .Callback(() => startCalled.TrySetResult(true))
                .Returns(Task.CompletedTask);

            plugin.UploadEnabled = true; // false -> true: fire-and-forget wrapper fires

            await startCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

            uploadManager.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
            uploadManager.Verify(m => m.ResumeAsync(), Times.Never, "BlockKind is None -- nothing to resume");
        }

        [Test]
        public async Task UploadEnabled_FalseToTrue_BlockKindQuotaExceeded_CallsResumeAsync_ThenStarts()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: false);
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.QuotaExceeded);
            uploadManager.Setup(m => m.ResumeAsync()).ReturnsAsync(true);

            var startCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            uploadManager.Setup(m => m.StartAsync(It.IsAny<CancellationToken>()))
                .Callback(() => startCalled.TrySetResult(true))
                .Returns(Task.CompletedTask);

            plugin.UploadEnabled = true;

            await startCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

            uploadManager.Verify(m => m.ResumeAsync(), Times.Once,
                "The false->true transition must clear a QuotaExceeded block");
            uploadManager.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task UploadEnabled_FalseToTrue_BlockKindAuthInvalid_NeverCallsResumeAsync()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: false);
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.AuthInvalid);

            var startCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            uploadManager.Setup(m => m.StartAsync(It.IsAny<CancellationToken>()))
                .Callback(() => startCalled.TrySetResult(true))
                .Returns(Task.CompletedTask);

            plugin.UploadEnabled = true;

            // EnsureUploadPipelineAsync still runs (StartAsync signals completion of the whole wrapper);
            // the assertion of interest is that ResumeAsync -- gated to Quota only -- was never called.
            await startCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

            uploadManager.Verify(m => m.ResumeAsync(), Times.Never,
                "AuthInvalid must NEVER be cleared by the toggle -- only a validated key can clear it");
        }

        [Test]
        public void UploadEnabled_TrueToTrue_NoTransition_NeverTriggersWrapper()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);

            plugin.UploadEnabled = true; // already true -- not a transition, guard fails synchronously

            uploadManager.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
            uploadManager.Verify(m => m.ResumeAsync(), Times.Never);
        }

        [Test]
        public void UploadEnabled_TrueToFalse_NeverTriggersWrapper()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);

            plugin.UploadEnabled = false; // true -> false is not the trigger direction

            uploadManager.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
            uploadManager.Verify(m => m.ResumeAsync(), Times.Never);
        }

        // ====================================================================
        // TestConnectionAsync success path -- AuthInvalid clear (round-2 P1 credential gate)
        // ====================================================================

        [Test]
        public async Task TestConnectionAsync_Success_AuthInvalidBlock_ClearsViaResumeAsync_ThenEnsuresPipeline()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);
            authManager.Setup(a => a.ValidateApiKeyAsync(It.IsAny<string>()))
                .ReturnsAsync(new ApiKeyValidationResult { Valid = true, AccountName = "acct" });
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.AuthInvalid);
            uploadManager.Setup(m => m.ResumeAsync()).ReturnsAsync(true);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);
            uploadManager.Setup(m => m.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            plugin.ApiKeyInput = "some-key";

            await plugin.TestConnectionAsync();

            uploadManager.Verify(m => m.ResumeAsync(), Times.Once,
                "A successful validation must be the trigger that clears an AuthInvalid block");
            uploadManager.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Once,
                "...and then the pipeline must be ensured running");
        }

        [Test]
        public async Task TestConnectionAsync_Success_QuotaExceededBlock_NeverCallsResumeAsync()
        {
            // Test-Connection success is scoped to clearing AuthInvalid ONLY -- a Quota block is
            // untouched by this trigger (only the Resume button / toggle clear Quota).
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);
            authManager.Setup(a => a.ValidateApiKeyAsync(It.IsAny<string>()))
                .ReturnsAsync(new ApiKeyValidationResult { Valid = true, AccountName = "acct" });
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.QuotaExceeded);
            uploadManager.SetupGet(m => m.IsRunning).Returns(true);

            plugin.ApiKeyInput = "some-key";

            await plugin.TestConnectionAsync();

            uploadManager.Verify(m => m.ResumeAsync(), Times.Never);
        }

        [Test]
        public async Task TestConnectionAsync_Success_NoBlock_NeverCallsResumeAsync_ButEnsuresPipeline()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);
            authManager.Setup(a => a.ValidateApiKeyAsync(It.IsAny<string>()))
                .ReturnsAsync(new ApiKeyValidationResult { Valid = true, AccountName = "acct" });
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.None);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);
            uploadManager.Setup(m => m.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            plugin.ApiKeyInput = "some-key";

            await plugin.TestConnectionAsync();

            uploadManager.Verify(m => m.ResumeAsync(), Times.Never);
            uploadManager.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Once,
                "Storage-quota plan §4 Step 4: the old unconditional restart-if-not-running spot-fix is replaced by EnsureUploadPipelineAsync");
        }

        // ====================================================================
        // Storage-quota plan directive E (C-P1#2): storing a replacement key invalidates the OLD
        // cached account identity BEFORE the new key becomes active.
        // ====================================================================

        [Test]
        public async Task TestConnectionAsync_Success_ClearsCachedAccountId_BeforeStoringTheNewKey()
        {
            // A 507 arriving under the NEW key must never be attributable to the OLD account, and a
            // stale in-flight quota refresh from the OLD key must be generation-rejected -- both require
            // the atomic clear-and-bump (pass I's TryBeginConnectAccountCycle) to run BEFORE StoreApiKey
            // activates the new key. TryBeginConnectAccountCycle IS the null-clear now -- it replaces the
            // old separate SetAccountId(null) call on the normal (generation-match) path.
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);
            authManager.Setup(a => a.ValidateApiKeyAsync(It.IsAny<string>()))
                .ReturnsAsync(new ApiKeyValidationResult { Valid = true, AccountName = "acct" });
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.None);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);
            uploadManager.Setup(m => m.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var callOrder = new List<string>();
            int connectGeneration = 1;
            uploadManager.Setup(m => m.TryBeginConnectAccountCycle(It.IsAny<int>(), out connectGeneration))
                .Callback(() => callOrder.Add("TryBeginConnectAccountCycle"))
                .Returns(true);
            authManager.Setup(a => a.StoreApiKey(It.IsAny<string>(), It.IsAny<string>()))
                .Callback(() => callOrder.Add("StoreApiKey"));

            plugin.ApiKeyInput = "replacement-key";
            await plugin.TestConnectionAsync();

            uploadManager.Verify(m => m.TryBeginConnectAccountCycle(It.IsAny<int>(), out connectGeneration), Times.Once,
                "Storing a validated replacement key must atomically begin the connect cycle, clearing the cached account identity");
            authManager.Verify(a => a.StoreApiKey(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            uploadManager.Verify(m => m.SetAccountId(null), Times.Never,
                "On the atomic-owns path, the old separate SetAccountId(null) fallback must never run -- the clear already happened inside TryBeginConnectAccountCycle");
            Assert.That(callOrder, Is.EqualTo(new[] { "TryBeginConnectAccountCycle", "StoreApiKey" }),
                "TryBeginConnectAccountCycle must run BEFORE StoreApiKey so the new key can never inherit the old cached identity");
        }

        // ====================================================================
        // Contract-delta round item 2: the validate response's accountId is threaded through the
        // generation-checked SetAccountIdAsync path, applied AFTER StoreApiKey (pass D ordering
        // preserved: null-then-store-then-apply).
        // ====================================================================

        [Test]
        public async Task TestConnectionAsync_Success_AccountIdPresent_ThreadsThroughGenerationCheckedPath_AfterStoreApiKey()
        {
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);
            authManager.Setup(a => a.ValidateApiKeyAsync(It.IsAny<string>()))
                .ReturnsAsync(new ApiKeyValidationResult { Valid = true, AccountName = "acct", AccountId = "acct-99" });
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.None);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);
            uploadManager.SetupGet(m => m.ContextGeneration).Returns(7);
            uploadManager.Setup(m => m.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var callOrder = new List<string>();
            // The generation TryBeginConnectAccountCycle hands back after its own atomic bump (7 -> 8) --
            // this is what the caller must thread into SetAccountIdAsync, not the pre-validate value.
            int connectGeneration = 8;
            uploadManager.Setup(m => m.TryBeginConnectAccountCycle(It.IsAny<int>(), out connectGeneration))
                .Callback(() => callOrder.Add("TryBeginConnectAccountCycle"))
                .Returns(true);
            authManager.Setup(a => a.StoreApiKey(It.IsAny<string>(), It.IsAny<string>()))
                .Callback(() => callOrder.Add("StoreApiKey"));
            uploadManager.Setup(m => m.SetAccountIdAsync("acct-99", 8))
                .Callback(() => callOrder.Add("SetAccountIdAsync"))
                .ReturnsAsync(true);

            plugin.ApiKeyInput = "some-key";
            await plugin.TestConnectionAsync();

            uploadManager.Verify(m => m.TryBeginConnectAccountCycle(7, out connectGeneration), Times.Once,
                "The atomic begin must be attempted against the generation captured BEFORE the validate await");
            uploadManager.Verify(m => m.SetAccountIdAsync("acct-99", 8), Times.Once,
                "The validate response's accountId must be threaded through the SAME generation-checked path SetAccountIdAsync uses everywhere else, using the generation TryBeginConnectAccountCycle handed back for THIS connect cycle");
            uploadManager.Verify(m => m.SetAccountId(null), Times.Never,
                "TryBeginConnectAccountCycle IS the null-clear now -- the old separate SetAccountId(null) call must never run when the cycle is atomically owned");
            Assert.That(callOrder, Is.EqualTo(new[] { "TryBeginConnectAccountCycle", "StoreApiKey", "SetAccountIdAsync" }),
                "Pass D/I ordering: TryBeginConnectAccountCycle atomically clears the old identity, THEN StoreApiKey activates the new key, THEN the new accountId is applied through the generation it handed back -- so a concurrent context change during the window still wins");
        }

        // ====================================================================
        // Pass H P1: `preValidateGeneration` is captured BEFORE the validate await -- if the context
        // generation moves WHILE ValidateApiKeyAsync is in flight (an endpoint reinit/Disconnect racing
        // this connect), the validate response (including accountId) was produced under the OLD context
        // and must never be applied to the NEW one.
        // ====================================================================

        [Test]
        public async Task TestConnectionAsync_Success_ContextMovedDuringValidateAwait_StaleAccountIdDiscarded_ButConnectStillSucceeds()
        {
            // Simulates an endpoint reinit racing the in-flight ValidateApiKeyAsync call by bumping the
            // manager's ContextGeneration from INSIDE the mocked validate callback -- exactly like a
            // concurrent Disconnect/replacement-key/endpoint-swap moving the generation while the HTTP
            // call is outstanding. By the time `result.Valid` is observed, the context has already moved,
            // so TryBeginConnectAccountCycle (compared against the PRE-validate generation, captured as 0)
            // reports the mismatch and returns false -- the pass-D old-account clear then falls back to
            // the plain SetAccountId(null) call, exactly as it did before TryBeginConnectAccountCycle
            // existed.
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);
            var contextGeneration = 0;
            uploadManager.SetupGet(m => m.ContextGeneration).Returns(() => contextGeneration);
            authManager.Setup(a => a.ValidateApiKeyAsync(It.IsAny<string>()))
                .Returns(async () =>
                {
                    contextGeneration++; // context moves WHILE the validate call is still in flight
                    await Task.Yield();
                    return new ApiKeyValidationResult { Valid = true, AccountName = "acct", AccountId = "acct-stale-endpoint-a" };
                });
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.None);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);
            uploadManager.Setup(m => m.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            int mismatchGeneration = 1;
            uploadManager.Setup(m => m.TryBeginConnectAccountCycle(0, out mismatchGeneration)).Returns(false);

            plugin.ApiKeyInput = "some-key";
            await plugin.TestConnectionAsync();

            uploadManager.Verify(m => m.TryBeginConnectAccountCycle(0, out mismatchGeneration), Times.Once,
                "The atomic begin must be attempted against the generation captured BEFORE the validate await (0), not the post-swap value");
            uploadManager.Verify(m => m.SetAccountIdAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never,
                "The validate response's accountId was produced under the OLD (pre-swap) context -- it must never be applied under the NEW context reached during the await, even though validation itself succeeded");
            uploadManager.Verify(m => m.SetAccountId(null), Times.Once,
                "TryBeginConnectAccountCycle returning false means the context moved -- the old-account clear must still happen via the plain fallback, per the pass-D rule");
            authManager.Verify(a => a.StoreApiKey(It.IsAny<string>(), It.IsAny<string>()), Times.Once,
                "The rest of the connect flow (storing the now-current key) must still succeed despite the discarded accountId");
            uploadManager.Verify(m => m.StartAsync(It.IsAny<CancellationToken>()), Times.Once,
                "The pipeline must still be ensured running after a successful connect, even when the stale accountId was discarded");
        }

        [Test]
        public async Task TestConnectionAsync_Success_AccountIdAbsent_NeverCallsSetAccountIdAsync()
        {
            // Tolerate an older backend that omits accountId from the validate response entirely.
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);
            authManager.Setup(a => a.ValidateApiKeyAsync(It.IsAny<string>()))
                .ReturnsAsync(new ApiKeyValidationResult { Valid = true, AccountName = "acct", AccountId = null });
            authManager.Setup(a => a.IsConfigured).Returns(true);
            uploadManager.SetupGet(m => m.BlockKind).Returns(UploadBlockKind.None);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);
            uploadManager.Setup(m => m.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            plugin.ApiKeyInput = "some-key";
            await plugin.TestConnectionAsync();

            uploadManager.Verify(m => m.SetAccountIdAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never,
                "An absent accountId (older backend) must never call SetAccountIdAsync");
        }

        [Test]
        public async Task TestConnectionAsync_Failure_DoesNotClearCachedAccountId()
        {
            // A failed validation never stores a new key -- the existing (still-active) key's cached
            // identity must be left alone.
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: true);
            authManager.Setup(a => a.ValidateApiKeyAsync(It.IsAny<string>()))
                .ReturnsAsync(new ApiKeyValidationResult { Valid = false, ErrorMessage = "Invalid API key" });

            plugin.ApiKeyInput = "bad-key";
            await plugin.TestConnectionAsync();

            uploadManager.Verify(m => m.SetAccountId(It.IsAny<string>()), Times.Never);
        }

        // ====================================================================
        // DisconnectAsync -- clears the cached accountId (directive E, B#4)
        // ====================================================================

        [Test]
        public async Task DisconnectAsync_ClearsCachedAccountId()
        {
            // Storage-quota plan directive E (B#4): a disconnected credential must not leave a stale
            // cached accountId behind for a LATER block (under a different reconnected identity) to
            // wrongly inherit.
            var plugin = CreatePlugin(out var authManager, out var uploadManager, initialUploadEnabled: false);
            uploadManager.SetupGet(m => m.IsRunning).Returns(false);

            await plugin.DisconnectAsync();

            uploadManager.Verify(m => m.SetAccountId(null), Times.Once);
            authManager.Verify(a => a.ClearApiKey(), Times.Once);
        }
    }
}
