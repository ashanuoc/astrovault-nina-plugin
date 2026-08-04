using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Astrovault.Core;
using Astrovault.Data;
using Astrovault.Integration;
using Astrovault.Interfaces;
using Astrovault.Models;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace Astrovault
{
    /// <summary>
    /// Connection status states for the API key authentication flow.
    /// </summary>
    public enum ConnectionStatus
    {
        NotConfigured,
        Configured,    // API key file exists but not yet validated against backend
        Connected,
        InvalidKey,
        Unreachable,
        Error
    }

    /// <summary>
    /// Notification verbosity levels for upload toast notifications.
    /// </summary>
    public enum NotificationVerbosity
    {
        Off,
        ErrorsOnly,
        AllUploads
    }

    /// <summary>
    /// Main plugin class for Astrovault cloud upload.
    /// Exports IPluginManifest for N.I.N.A. plugin discovery.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class AstrovaultPlugin : PluginBase, INotifyPropertyChanged
    {
        private readonly IProfileService profileService;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly IPluginOptionsAccessor pluginSettings;

        // Core services
        private IAuthManager authManager;
        private IUploadQueueRepository queueRepository;
        private IUploadManager uploadManager;
        private ICloudApiClient cloudApiClient;
        private ImageSaveListener imageSaveListener;
        private CancellationTokenSource uploadCts;

        // API key input fields
        private string apiKeyInput = string.Empty;
        private bool isApiKeyRevealed = false;
        private bool isTestingConnection = false;
        private ConnectionStatus connectionStatus = ConnectionStatus.NotConfigured;
        private string connectedAccountName = string.Empty;

        // Storage-quota plan directive B: the single shared coalescing gate for the upload-pipeline
        // "control plane" -- ReinitializeHttpServicesAsync (endpoint reinit) and EnsureUploadPipelineAsync
        // (the G7 restart choke point) both serialize on this SAME semaphore, so Ensure can never race a
        // reinit's stop-swap-dispose window (B#6). Was two separate locks (reinitLock +
        // ensurePipelineGate); the split was exactly the gap the finding identified.
        private readonly SemaphoreSlim pipelineLock = new SemaphoreSlim(1, 1);

        // Background validation state
        private int validationGeneration;
        private CancellationTokenSource validationCts;

        // Progress & status state
        private string currentFileName = string.Empty;
        private double currentProgress;
        private string currentChunkText = string.Empty;
        private bool isUploading;
        private bool wasCircuitOpen;

        // Storage-quota plan §4 Step 6: last successfully-fetched quota usage, or null when never
        // fetched / last fetch was unavailable. Refreshed ONLY on the three allowed triggers (Test-
        // Connection success, queue-idle transition, block transitions) -- no polling/timers.
        private QuotaUsage lastQuotaUsage;

        // Storage-quota plan §4 Step 3: seam for the block-transition toast, swappable via reflection in
        // tests (mirrors the pluginSettings/authManager/uploadManager seams UploadPipelineTriggerTests
        // already uses) so the toast decision is testable without a live WPF Application.Current. Defaults
        // to the real Notification.ShowError in production.
        internal Action<string> showErrorToast = Notification.ShowError;

        [ImportingConstructor]
        public AstrovaultPlugin(
            IProfileService profileService,
            IOptionsVM options,
            IImageSaveMediator imageSaveMediator)
        {
            this.profileService = profileService;
            this.imageSaveMediator = imageSaveMediator;

            this.pluginSettings = new PluginOptionsAccessor(
                profileService,
                Guid.Parse(this.Identifier));

            InitializeServices();

            profileService.ProfileChanged += OnProfileChanged;
            Logger.Info("[Astrovault] Plugin initialized");
        }

        private void InitializeServices()
        {
            try
            {
                InitializeServicesCore();
            }
            catch (Exception ex)
            {
                // REL-16: a malformed STORED endpoint (or any construction fault) must NOT throw out
                // of the MEF importing constructor -- that would brick plugin load entirely. Degrade
                // gracefully to an Error status; the user can fix the endpoint in settings, which
                // triggers ReinitializeHttpServicesAsync to rebuild the services cleanly.
                Logger.Error($"[Astrovault] InitializeServices failed; degrading to Error state (plugin still loads): {ex.Message}");
                connectionStatus = ConnectionStatus.Error;
                connectedAccountName = string.Empty;
                RefreshAuthUI();
            }
        }

        private void InitializeServicesCore()
        {
            var dataFolder = GetDataFolder();

            // REL-16: if the persisted endpoint is malformed (e.g. corrupted on disk or written by an
            // older build), do NOT let it throw during construction. Fall back to the default endpoint
            // so the services still build, and surface an Error status so the user knows to re-enter it.
            var effectiveEndpoint = ApiEndpoint;
            var endpointMalformed = !IsValidEndpoint(effectiveEndpoint);
            if (endpointMalformed)
            {
                Logger.Warning($"[Astrovault] Stored ApiEndpoint is malformed ('{effectiveEndpoint}'); falling back to default and flagging Error");
                effectiveEndpoint = DefaultApiEndpoint;
            }

            // Auth
            authManager = new AuthManager(effectiveEndpoint, dataFolder);

            // REL-16: when the stored endpoint is malformed we keep an Error status even if a key is
            // present -- validating against a fallback URL would be misleading. The user re-enters a
            // valid endpoint (validated by the setter) to recover. Otherwise, show Configured (orange)
            // until background validation completes.
            if (endpointMalformed)
            {
                connectionStatus = ConnectionStatus.Error;
                connectedAccountName = string.Empty;
            }
            else if (authManager.IsConfigured)
            {
                connectionStatus = ConnectionStatus.Configured;
                connectedAccountName = authManager.AccountName ?? string.Empty;
                ValidateConnectionInBackground();
            }

            // Queue (persistent). Plumb the drain-order setting via a getter-delegate so the repo
            // reads the preference without owning IPluginOptionsAccessor. Consulted on every drain.
            queueRepository = new UploadQueueRepository(dataFolder, () => DrainOrderNewestFirst);

            // API client (real client that calls BFF, with queue repo for chunk state persistence).
            // PERF-03: inject the ChunkConcurrency getter-delegate so the client reads the user's
            // bounded-parallel setting without owning IPluginOptionsAccessor (same seam as drain-order).
            cloudApiClient = new AstrovaultApiClient(authManager, effectiveEndpoint, queueRepository, () => ChunkConcurrency);

            // Upload manager
            uploadManager = new UploadManager(queueRepository, cloudApiClient, authManager);
            uploadManager.UploadCompleted += OnUploadCompleted;
            uploadManager.ProgressChanged += OnProgressChanged;
            uploadManager.QueueStateChanged += OnQueueStateChanged;
            uploadManager.UploadBlocked += OnUploadBlocked;

            // Image listener (PathResolver uses profile for base path)
            var pathResolver = new PathResolver(profileService);
            imageSaveListener = new ImageSaveListener(
                imageSaveMediator,
                uploadManager,
                pathResolver,
                () => UploadEnabled && authManager.IsConfigured);

            // Start services
            StartServices();
            RefreshAuthUI();
        }

        /// <summary>
        /// Runs a non-blocking auth/validate call. Uses a generation counter so that
        /// if a newer validation is triggered (e.g., endpoint change), the stale result
        /// is discarded and does not overwrite the newer status.
        /// </summary>
        private void ValidateConnectionInBackground()
        {
            // Cancel any in-flight validation
            validationCts?.Cancel();
            validationCts?.Dispose();
            validationCts = new CancellationTokenSource();

            var myGeneration = Interlocked.Increment(ref validationGeneration);
            var token = validationCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    var apiKey = authManager.GetApiKey();
                    if (string.IsNullOrEmpty(apiKey))
                        return;

                    token.ThrowIfCancellationRequested();

                    var result = await authManager.ValidateApiKeyAsync(apiKey);

                    token.ThrowIfCancellationRequested();

                    // Only apply result if this is still the latest generation.
                    // WR-07: a plain volatile read of the generation counter -- the previous
                    // CompareExchange(field, myGeneration, myGeneration) was a no-op write that
                    // obscured intent. This is behavior-identical and clearer.
                    if (Volatile.Read(ref validationGeneration) != myGeneration)
                        return; // A newer validation was triggered; discard this stale result

                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        // Double-check generation on UI thread in case another reinit happened
                        if (Thread.VolatileRead(ref validationGeneration) != myGeneration)
                            return;

                        if (result.Valid)
                        {
                            connectionStatus = ConnectionStatus.Connected;
                            connectedAccountName = result.AccountName ?? connectedAccountName;
                        }
                        else
                        {
                            connectionStatus = ConnectionStatus.InvalidKey;
                        }
                        RefreshAuthUI();
                    }));
                }
                catch (OperationCanceledException)
                {
                    // Expected when endpoint changes while validation is in-flight
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested)
                        return;

                    if (Thread.VolatileRead(ref validationGeneration) != myGeneration)
                        return;

                    Logger.Warning($"[Astrovault] Background validation failed: {ex.Message}");
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        if (Thread.VolatileRead(ref validationGeneration) != myGeneration)
                            return;

                        connectionStatus = ConnectionStatus.Unreachable;
                        RefreshAuthUI();
                    }));
                }
            });
        }

        /// <summary>
        /// Recreates AuthManager and AstrovaultApiClient with the current ApiEndpoint.
        /// Serialized by the shared <see cref="pipelineLock"/> to prevent concurrent reinit from rapid
        /// endpoint changes -- storage-quota plan directive B: the SAME gate <see
        /// cref="EnsureUploadPipelineAsync"/> uses, so an Ensure call (from the UploadEnabled toggle or
        /// a successful Test-Connection) can never restart the OLD context mid-reinit (B#6): the window
        /// used to exist between this method's StopAsync and its UpdateContext swap, where a concurrent
        /// Ensure -- guarded by a DIFFERENT lock -- could see IsRunning false and restart the manager
        /// against the about-to-be-disposed old auth manager/client.
        /// Stops UploadManager before swap to drain active uploads, preventing ObjectDisposedException.
        /// Normalizes URL trailing slash so HttpClient.BaseAddress resolves relative URIs correctly.
        /// </summary>
        private async Task ReinitializeHttpServicesAsync()
        {
            // Serialize: if another reinit (or a concurrent EnsureUploadPipelineAsync) is already
            // running, wait for it.
            await pipelineLock.WaitAsync();
            try
            {
                Logger.Info($"[Astrovault] Reinitializing HTTP services for endpoint: {ApiEndpoint}");

                // Normalize URL: HttpClient.BaseAddress requires trailing slash for correct
                // relative URI resolution. Without it, the last path segment is dropped.
                var normalizedEndpoint = ApiEndpoint.TrimEnd('/') + "/";

                // Guard: if uploadManager is not yet initialized (e.g., ApiEndpoint setter fires
                // during construction before InitializeServices completes), skip reinitialization.
                if (uploadManager == null)
                {
                    Logger.Warning("[Astrovault] ReinitializeHttpServicesAsync skipped: uploadManager not yet initialized");
                    return;
                }

                // Step A: Stop the upload manager to drain any in-flight upload.
                if (uploadManager.IsRunning)
                {
                    Logger.Info("[Astrovault] Stopping upload manager before reinit...");
                    await uploadManager.StopAsync();
                }

                // Step B: Save references to old services for disposal after swap
                var oldApiClient = cloudApiClient;
                var oldAuthManager = authManager;

                // Step C: Recreate auth manager with normalized URL
                var dataFolder = GetDataFolder();
                authManager = new AuthManager(normalizedEndpoint, dataFolder);

                // Step D: Recreate API client with normalized URL (PERF-03: re-inject the delegate)
                cloudApiClient = new AstrovaultApiClient(authManager, normalizedEndpoint, queueRepository, () => ChunkConcurrency);

                // Step E: Swap BOTH the auth manager and client references in the upload manager
                // (storage-quota plan §4 Step 4, G13 fix) -- safe, the manager is stopped. Swapping only
                // the API client (the old UpdateApiClient) left the manager holding a reference to the
                // AuthManager Step F is about to dispose; ProcessNextJobAsync's key check then silently
                // read from a disposed instance forever (running-but-permanently-idle).
                uploadManager.UpdateContext(authManager, cloudApiClient);

                // Step F: Dispose old instances after swap
                oldApiClient?.Dispose();
                oldAuthManager?.Dispose();

                // Step G: reflect the new configured/not-configured status. Identity reconciliation
                // (storage-quota plan §4 Step 2 point 4) and the actual restart decision both happen
                // inside EnsureUploadPipelineAsync -> StartAsync -> RestoreBlockStateAsync below -- it
                // re-checks UploadEnabled && authManager.IsConfigured && !IsRunning itself, so calling it
                // unconditionally is safe (a no-op when any of those don't hold) and fixes G7's dead end
                // for the "reinit with unconfigured auth" flow that used to leave the manager stopped
                // forever with no restart path.
                if (authManager.IsConfigured)
                {
                    connectionStatus = ConnectionStatus.Configured;
                    connectedAccountName = authManager.AccountName ?? string.Empty;
                    ValidateConnectionInBackground();
                }
                else
                {
                    connectionStatus = ConnectionStatus.NotConfigured;
                    connectedAccountName = string.Empty;
                }

                // Storage-quota plan directive B: call the CORE ensure logic directly, bypassing the
                // gate -- this method already holds pipelineLock, and EnsureUploadPipelineAsync's public
                // wrapper acquires the SAME semaphore (not reentrant), which would deadlock here.
                await EnsureUploadPipelineCoreAsync(acquireGate: false);

                RefreshAuthUI();
                Logger.Info("[Astrovault] HTTP services reinitialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Astrovault] ReinitializeHttpServicesAsync failed: {ex.Message}");
                connectionStatus = ConnectionStatus.Error;
                RefreshAuthUI();
            }
            finally
            {
                pipelineLock.Release();
            }
        }

        // ====================================================================
        // Upload pipeline lifecycle (storage-quota plan §4 Steps 4/5)
        // ====================================================================

        private readonly object ensurePipelineGate = new object();
        private Task ensureUploadPipelineTask;

        /// <summary>
        /// Storage-quota plan §4 Step 4 (G7 fix): the single choke point that (re)starts the upload
        /// pipeline whenever it is legitimately stopped-but-should-be-running. Before this fix the
        /// manager could be left stopped with UploadEnabled on and a valid key -- a dead end until plugin
        /// reload (G7: "no path covers the general stopped+enabled+configured state"). Serialized and
        /// COALESCED: concurrent EXTERNAL callers observe and await the SAME in-flight task instead of
        /// racing separate StartAsync calls (this outer coalescing is UNCHANGED by directive B --
        /// <paramref name="ensurePipelineGate"/> is a plain object, distinct from the shared
        /// <see cref="pipelineLock"/> the core work below acquires). Call sites: the UploadEnabled
        /// false-&gt;true transition (via the fire-and-forget setter wrapper), a successful
        /// Test-Connection, and the tail of ReinitializeHttpServicesAsync (which calls the CORE method
        /// directly -- see there). Exceptions are caught, logged (no key material -- LogSanitizer
        /// conventions apply throughout this class), and surfaced via the existing Error connection
        /// status -- never silently dropped.
        /// </summary>
        public Task EnsureUploadPipelineAsync()
        {
            lock (ensurePipelineGate)
            {
                if (ensureUploadPipelineTask == null || ensureUploadPipelineTask.IsCompleted)
                {
                    ensureUploadPipelineTask = EnsureUploadPipelineCoreAsync(acquireGate: true);
                }
                return ensureUploadPipelineTask;
            }
        }

        /// <summary>
        /// Storage-quota plan directive B (B#6): the actual ensure-running logic. When
        /// <paramref name="acquireGate"/> is true (every EXTERNAL call, via <see
        /// cref="EnsureUploadPipelineAsync"/>) this acquires the SAME <see cref="pipelineLock"/>
        /// <see cref="ReinitializeHttpServicesAsync"/> holds for its whole stop-swap-dispose sequence,
        /// so an Ensure call can never observe the manager stopped mid-reinit and restart it against the
        /// about-to-be-disposed OLD context. When false (ONLY <see cref="ReinitializeHttpServicesAsync"/>'s
        /// own tail call), the gate is skipped because that caller already holds it -- SemaphoreSlim is
        /// not reentrant, so re-acquiring here would deadlock.
        /// </summary>
        private async Task EnsureUploadPipelineCoreAsync(bool acquireGate)
        {
            if (acquireGate)
            {
                await pipelineLock.WaitAsync();
            }
            try
            {
                if (uploadManager == null || authManager == null)
                {
                    return; // WR-04-style no-op in the REL-16 degraded (Error) state
                }

                if (UploadEnabled && authManager.IsConfigured && !uploadManager.IsRunning)
                {
                    await uploadManager.StartAsync(uploadCts?.Token ?? CancellationToken.None);
                    Logger.Info("[Astrovault] Upload pipeline ensured running");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Astrovault] EnsureUploadPipelineAsync failed: {ex.Message}");
                connectionStatus = ConnectionStatus.Error;
                RefreshAuthUI();
            }
            finally
            {
                if (acquireGate)
                {
                    pipelineLock.Release();
                }
            }
        }

        /// <summary>
        /// Storage-quota plan §4 Step 5: the public entry point Step 3's "Resume uploads" banner button
        /// calls (an <c>async void</c> handler in Options.xaml.cs, following the existing try/catch
        /// UI-handler convention used by OnDisconnectClicked/OnRetryAllFailedClicked/etc.). Also invoked
        /// internally by the UploadEnabled false-&gt;true transition.
        /// <para>
        /// KIND GATE (round-2 P1 -- the credential gate): this ONLY clears a <see
        /// cref="UploadBlockKind.QuotaExceeded"/> block. <see cref="UploadBlockKind.AuthInvalid"/> is
        /// NEVER cleared here -- re-enabling or resuming without a working key must not restart doomed
        /// uploads. AuthInvalid clears ONLY via a successful key validation (see TestConnectionAsync's
        /// success path). Calling this while unblocked, or while blocked on AuthInvalid, is a harmless
        /// no-op.
        /// </para>
        /// </summary>
        public async Task ResumeUploadsAsync()
        {
            if (uploadManager == null || uploadManager.BlockKind != UploadBlockKind.QuotaExceeded)
            {
                return; // nothing to resume, or gated: AuthInvalid needs a validated key, not this trigger
            }

            var resumed = await uploadManager.ResumeAsync();
            Logger.Info(resumed
                ? "[Astrovault] Quota block resumed"
                : "[Astrovault] Resume requested but the block-clear could not be persisted durably -- still blocked");
        }

        private string GetDataFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Astrovault");
        }

        private void StartServices()
        {
            try
            {
                Logger.Info("[Astrovault] Starting services...");
                uploadCts = new CancellationTokenSource();

                // Start upload manager in background
                Task.Run(async () =>
                {
                    try
                    {
                        await uploadManager.StartAsync(uploadCts.Token);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[Astrovault] UploadManager crashed: {ex.Message}");
                    }
                });

                imageSaveListener.StartListening();
                Logger.Info("[Astrovault] Services started successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"[Astrovault] Failed to start services: {ex.Message}");
            }
        }

        /// <summary>
        /// REL-13: async, non-blocking stop chain. Awaited end-to-end by <see cref="Teardown"/> --
        /// no <c>.Wait()</c> on the UI thread. Order matters for REL-03/REL-16:
        /// (1) unsubscribe events first so no post-teardown UI callback fires,
        /// (2) stop listening so no NEW captures are accepted,
        /// (3) FLUSH the in-flight fire-and-forget enqueues BEFORE cancelling so a capture racing the
        ///     close still reaches disk (REL-03),
        /// (4) only then cancel and await the upload loop's StopAsync so HTTP clients are disposed
        ///     (in Teardown) strictly AFTER the loop has stopped -- no ObjectDisposedException mid-upload.
        /// </summary>
        private async Task StopServicesAsync()
        {
            // Unsubscribe events before cancellation to prevent post-teardown exceptions
            if (uploadManager != null)
            {
                uploadManager.QueueStateChanged -= OnQueueStateChanged;
                uploadManager.ProgressChanged -= OnProgressChanged;
                uploadManager.UploadCompleted -= OnUploadCompleted;
                uploadManager.UploadBlocked -= OnUploadBlocked;
            }

            // Stop accepting new captures, then flush any enqueue still in flight (REL-03) so a
            // capture that was being queued when NINA closed is not lost.
            imageSaveListener?.StopListening();
            if (imageSaveListener != null)
            {
                await imageSaveListener.FlushPendingAsync(TimeSpan.FromSeconds(5));
            }

            // Now cancel the upload loop and await it to a clean stop (no .Wait() block).
            uploadCts?.Cancel();
            if (uploadManager != null)
            {
                await uploadManager.StopAsync();
            }
            Logger.Info("[Astrovault] Services stopped");
        }

        public override async Task Teardown()
        {
            await StopServicesAsync();
            validationCts?.Cancel();
            validationCts?.Dispose();
            profileService.ProfileChanged -= OnProfileChanged;
            // Dispose clients only AFTER the upload loop has stopped (above) so a disposed HttpClient
            // can never be touched mid-upload.
            imageSaveListener?.Dispose();
            cloudApiClient?.Dispose();
            authManager?.Dispose();
            Logger.Info("[Astrovault] Plugin teardown complete");
            await base.Teardown();
        }

        private void OnProfileChanged(object sender, EventArgs e)
        {
            RaisePropertyChanged(nameof(UploadEnabled));
        }

        private void OnUploadCompleted(object sender, UploadCompletedEventArgs e)
        {
            var status = e.Success ? "completed" : "failed";
            Logger.Info($"[Astrovault] Upload {status}: {e.FileName}");
            Application.Current?.Dispatcher?.BeginInvoke(() => HandleUploadCompletedOnUiThread(e));
        }

        /// <summary>
        /// UI-thread body of the UploadCompleted handler, factored out of <see cref="OnUploadCompleted"/>
        /// so it is directly callable in tests -- the <c>Application.Current?.Dispatcher</c> wrap above is
        /// a no-op in the NUnit host (no live WPF <c>Application</c>), which would otherwise make this
        /// logic untestable. Storage-quota plan §4 Step 6: also the "queue transitioning to idle after
        /// uploads" quota-usage refresh trigger -- fires once whenever THIS completion leaves both the
        /// pending and in-flight counts at zero.
        /// </summary>
        internal void HandleUploadCompletedOnUiThread(UploadCompletedEventArgs e)
        {
            IsUploading = false;
            RefreshQueueUI();
            SendNotification(e);

            if (uploadManager != null && uploadManager.PendingCount == 0 && uploadManager.UploadingCount == 0)
            {
                _ = RefreshQuotaUsageAsync();
            }
        }

        private void OnUploadBlocked(object sender, UploadBlockedEventArgs e)
        {
            Logger.Info($"[Astrovault] Upload blocked: {e.Kind}");
            Application.Current?.Dispatcher?.BeginInvoke(() => HandleUploadBlockedOnUiThread(e));
        }

        /// <summary>
        /// UI-thread body of the UploadBlocked handler (storage-quota plan §4 Step 3), factored out for
        /// the same testability reason as <see cref="HandleUploadCompletedOnUiThread"/>. Only ever
        /// invoked for a LIVE transition (the manager never raises <c>UploadBlocked</c> on a startup
        /// restore -- see <see cref="Astrovault.Interfaces.IUploadManager.UploadBlocked"/>), so:
        /// (1) IsUploading is explicitly ended here so the progress UI can't stick (a blocked job never
        /// reaches <see cref="HandleUploadCompletedOnUiThread"/>, which is the normal place that happens);
        /// (2) the toast fires exactly once per transition, never on restore; (3) the usage line refreshes
        /// (one of the three allowed triggers).
        /// </summary>
        internal void HandleUploadBlockedOnUiThread(UploadBlockedEventArgs e)
        {
            IsUploading = false;
            RefreshQueueUI();
            ShowBlockTransitionToast(e.Kind, e.Snapshot?.Flavor ?? QuotaBlockFlavor.StorageLimitExceeded);
            _ = RefreshQuotaUsageAsync();
        }

        /// <summary>
        /// Storage-quota plan §4 Step 3: fires the one-per-transition <c>Notification.ShowError</c> toast
        /// (via the <see cref="showErrorToast"/> seam). Deliberately does NOT go through
        /// <see cref="SendNotification"/> -- this is unconditional, independent of
        /// <see cref="NotificationVerbosity"/> and <see cref="IsCircuitOpen"/> (a stopped upload pipeline
        /// is categorically more important than a single failed file, so it is never silenced by the
        /// "errors only"/circuit-suppression gates that exist to reduce per-file failure spam). Fixed
        /// local wording only -- never server-provided text, matching the codebase's stance on server-
        /// controlled strings (see <see cref="Core.QuotaInfoParser"/>).
        /// <paramref name="flavor"/> (contract-delta round) picks the matching wording for a
        /// <see cref="UploadBlockKind.QuotaExceeded"/> transition; defaults to
        /// <see cref="QuotaBlockFlavor.StorageLimitExceeded"/> so every existing call site (and every
        /// <see cref="UploadBlockKind.AuthInvalid"/> transition, which never carries a meaningful
        /// flavor) is unaffected.
        /// </summary>
        internal void ShowBlockTransitionToast(UploadBlockKind kind, QuotaBlockFlavor flavor = QuotaBlockFlavor.StorageLimitExceeded)
        {
            var text = kind switch
            {
                UploadBlockKind.QuotaExceeded => flavor == QuotaBlockFlavor.PlanRequired
                    ? PlanRequiredBannerText
                    : "Upload stopped — storage full. Frames are saved locally until you free up space.",
                UploadBlockKind.AuthInvalid =>
                    "Upload stopped — API key rejected. Reconnect in the Astrovault options.",
                _ => null
            };

            if (text == null)
            {
                return;
            }

            try
            {
                showErrorToast?.Invoke(text);
                Logger.Info($"[Astrovault] Notification sent: error - block transition ({kind})");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Astrovault] Block-transition toast failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Storage-quota plan §4 Step 6: fetches the current storage usage and updates the Upload Status
        /// panel's usage line. Called ONLY from the three allowed triggers (Test-Connection success --
        /// awaited directly there; queue-idle transition and block transitions -- fire-and-forget from
        /// their already-UI-marshaled event handlers). No polling/timers anywhere in this class call it.
        /// <see cref="Core.AstrovaultApiClient.GetQuotaAsync"/> never throws (always returns null on any
        /// failure), but this wrapper still catches defensively since it is invoked fire-and-forget from
        /// two call sites and must never fault an unobserved task.
        /// </summary>
        internal async Task RefreshQuotaUsageAsync()
        {
            if (cloudApiClient == null)
            {
                return;
            }

            try
            {
                // Storage-quota plan directive E (A#6, B#4): capture the manager's context generation
                // BEFORE starting the fetch. If an endpoint reinit or disconnect swaps/clears the context
                // while this await is in flight, the generation will have moved on by the time the fetch
                // resolves -- SetAccountIdAsync recognizes that and drops the stale result instead of
                // threading an old context's accountId into a NEW context's block.
                var generation = uploadManager?.ContextGeneration ?? 0;
                var usage = await cloudApiClient.GetQuotaAsync(uploadCts?.Token ?? CancellationToken.None);
                lastQuotaUsage = usage;

                // Property-changed notifications fire regardless of what happens with the accountId
                // threading below -- the displayed usage line only depends on lastQuotaUsage, set above.
                RaisePropertyChanged(nameof(HasQuotaUsage));
                RaisePropertyChanged(nameof(QuotaUsageText));

                // Step 6 / directive E: thread the account id from a successful fetch into the manager's
                // block identity scoping (and, per B#3, reconcile any currently-live block against it).
                if (uploadManager != null && !string.IsNullOrEmpty(usage?.AccountId))
                {
                    await uploadManager.SetAccountIdAsync(usage.AccountId, generation).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Astrovault] Quota usage refresh failed: {ex.Message}");
            }
        }

        private void OnProgressChanged(object sender, UploadProgressEventArgs e)
        {
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                CurrentFileName = e.FileName ?? "Unknown file";
                CurrentProgress = Math.Max(0.0, Math.Min(1.0, e.Progress));
                CurrentChunkText = e.TotalChunks > 0
                    ? $"({e.ChunkIndex}/{e.TotalChunks} chunks)"
                    : string.Empty;
                IsUploading = true;
                RaisePropertyChanged(nameof(ProgressPercentText));
                RefreshQueueUI();
            });
        }

        private void OnQueueStateChanged(object sender, EventArgs e)
        {
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                var circuitNowOpen = IsCircuitOpen;
                if (!wasCircuitOpen && circuitNowOpen)
                {
                    Notification.ShowWarning("Uploads paused \u2014 API unreachable");
                    Logger.Info("[Astrovault] Notification sent: warning - circuit breaker opened");
                }
                else if (wasCircuitOpen && !circuitNowOpen && !IsQuotaBlocked && !IsAuthBlocked)
                {
                    // Storage-quota plan directive H (toast ordering, B#7): EnterBlockAsync resets the
                    // breaker (the response proves connectivity) and sets the manager's in-memory block
                    // latch BEFORE raising QueueStateChanged, so IsQuotaBlocked/IsAuthBlocked already
                    // reflect the NEW block by the time this handler runs for that same transition.
                    // Suppress the misleading "connection restored / uploads resumed" success toast when
                    // a block transition arrives on the SAME state-changed notification -- no upload
                    // actually resumed, the queue just latched. The dedicated block toast (via
                    // OnUploadBlocked/ShowBlockTransitionToast) is the only one shown for that transition.
                    Notification.ShowSuccess("Uploads resumed \u2014 connection restored");
                    Logger.Info("[Astrovault] Notification sent: success - circuit breaker closed");
                }
                wasCircuitOpen = circuitNowOpen;
                RefreshQueueUI();
            });
        }

        // ====================================================================
        // Plugin Settings (persisted via IPluginOptionsAccessor)
        // ====================================================================

        // P19-02: the production HTTPS host is the default/fallback endpoint. This is the ONE place
        // the default lives -- both the getter default and the startup fallback reference this const.
        // Canonical production host is api.astrospherehub.com (confirmed live 2026-07); users normally
        // never change this -- the endpoint field is tucked behind the Options "Advanced" expander.
        private const string DefaultApiEndpoint = "https://api.astrospherehub.com/";

        // Superseded default endpoints, scrubbed to DefaultApiEndpoint on read via an EXACT-ORDINAL
        // match ONLY (so a deliberate custom endpoint always survives). One-time UX upgrade for existing
        // installs -- NOT a security control (the send-boundary HTTPS gate is). Entries:
        //   - LegacyVaultEndpoint: the prior "vault." production hostname, superseded by the canonical
        //     "api." host. Both resolve to the same backend today, so migrating an install is seamless.
        private const string LegacyVaultEndpoint = "https://vault.astrospherehub.com/";

        public string ApiEndpoint
        {
            get
            {
                var stored = pluginSettings.GetValueString(nameof(ApiEndpoint), DefaultApiEndpoint);
                // UX scrub only -- the send-boundary HTTPS gate (AuthManager/AstrovaultApiClient) is the
                // security control; this just upgrades a known superseded default for existing installs.
                // Exact-ordinal match against a known superseded literal ONLY so a custom endpoint survives.
                if (string.Equals(stored, LegacyVaultEndpoint, StringComparison.Ordinal))
                {
                    pluginSettings.SetValueString(nameof(ApiEndpoint), DefaultApiEndpoint);
                    return DefaultApiEndpoint;
                }
                return stored;
            }
            set
            {
                // REL-15: validate BEFORE persisting. An absolute http/https URL is required so a
                // malformed value can never be stored (which would otherwise brick the NEXT plugin
                // load during MEF construction -- REL-16). Reject/ignore an invalid value and surface
                // an Error status rather than throwing or persisting garbage.
                if (!IsValidEndpoint(value))
                {
                    Logger.Warning($"[Astrovault] Rejected invalid ApiEndpoint (not an absolute http/https URL): '{value}'");
                    connectionStatus = ConnectionStatus.Error;
                    RefreshAuthUI();
                    return;
                }

                pluginSettings.SetValueString(nameof(ApiEndpoint), value);
                RaisePropertyChanged();
                // Recreate HTTP services with new endpoint (HttpClient.BaseAddress is immutable after first use).
                // Fire-and-forget: reinit is serialized by the shared pipelineLock so rapid changes queue safely.
                if (authManager != null) // guard against calls during construction
                {
                    _ = ReinitializeHttpServicesAsync();
                }
            }
        }

        /// <summary>
        /// REL-15/REL-16: an endpoint is valid only when it parses as an ABSOLUTE http/https URI.
        /// Used by the setter (reject before persist) and the startup ctor guard (degrade gracefully
        /// on a malformed STORED value instead of throwing during MEF construction).
        /// </summary>
        internal static bool IsValidEndpoint(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                   && Uri.TryCreate(value, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>
        /// P19-01: the "is it safe to send the API key here?" policy gate, layered ON TOP of
        /// IsValidEndpoint (which only checks absolute-http/https syntax). HTTPS is always allowed;
        /// plain http is allowed ONLY for loopback. This is the load-bearing security control consumed
        /// by the send-boundary gates (AuthManager.ValidateApiKeyAsync, AstrovaultApiClient.UploadFileAsync)
        /// so the X-API-Key header can never cross a non-loopback http:// boundary, regardless of how the
        /// endpoint got persisted.
        /// Uri.IsLoopback covers "localhost", "127.0.0.1", and the no-host case; IPAddress.IsLoopback is
        /// added to also accept IPv6 "::1" and the full 127.0.0.0/8 range that Uri.IsLoopback misses.
        /// </summary>
        internal static bool IsSecureEndpoint(string value)
        {
            if (!IsValidEndpoint(value)) return false;            // must already be an absolute http/https URL
            var uri = new Uri(value, UriKind.Absolute);
            if (uri.Scheme == Uri.UriSchemeHttps) return true;    // https always allowed
            if (uri.IsLoopback) return true;                      // localhost, 127.0.0.1, no-host
            if (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip)) return true; // ::1, 127.0.0.0/8
            return false;                                         // non-loopback http -> blocked
        }

        public bool UploadEnabled
        {
            get => pluginSettings.GetValueBoolean(nameof(UploadEnabled), false);
            set
            {
                var wasEnabled = UploadEnabled;
                pluginSettings.SetValueBoolean(nameof(UploadEnabled), value);
                RaisePropertyChanged();
                Logger.Info($"[Astrovault] Upload enabled: {value}");

                // Storage-quota plan §4 Step 4/5: the false->true transition is one of the two triggers
                // allowed to clear a QuotaExceeded block (never AuthInvalid -- ResumeUploadsAsync owns
                // that kind gate) and must also ensure the pipeline is actually running (G7's dead end:
                // toggling this checkbox alone never used to start/stop anything). Property setters can't
                // await -- fire-and-forget through a wrapper that owns its own try/catch (mirrors
                // ReinitializeHttpServicesAsync's self-contained exception handling elsewhere in this
                // class), so the exception is never unobserved/dropped.
                if (!wasEnabled && value && authManager != null)
                {
                    _ = OnUploadEnabledTurnedOnAsync();
                }
            }
        }

        /// <summary>
        /// Fire-and-forget wrapper for the UploadEnabled false-&gt;true transition (storage-quota plan §4
        /// Step 4/5). Owns its own try/catch so an unobserved exception can never fault -- the setter
        /// cannot await this. Resumes a QuotaExceeded block first (a no-op if unblocked or blocked on
        /// AuthInvalid -- ResumeUploadsAsync is kind-gated), then ensures the pipeline is running.
        /// </summary>
        private async Task OnUploadEnabledTurnedOnAsync()
        {
            try
            {
                await ResumeUploadsAsync();
                await EnsureUploadPipelineAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"[Astrovault] Upload-enabled resume/ensure failed: {ex.Message}");
                connectionStatus = ConnectionStatus.Error;
                RefreshAuthUI();
            }
        }

        /// <summary>
        /// Queue drain order. True = Newest first (LIFO, the default), false = Oldest first (FIFO).
        /// Persisted via IPluginOptionsAccessor; read by UploadQueueRepository through an injected
        /// getter-delegate so the repo never accesses settings directly. Default LIFO preserves
        /// existing behavior.
        /// </summary>
        public bool DrainOrderNewestFirst
        {
            get => pluginSettings.GetValueBoolean(nameof(DrainOrderNewestFirst), true);
            set
            {
                pluginSettings.SetValueBoolean(nameof(DrainOrderNewestFirst), value);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(DrainOrderIndex));
                Logger.Info($"[Astrovault] Drain order set to {(value ? "Newest first (LIFO)" : "Oldest first (FIFO)")}");
            }
        }

        /// <summary>
        /// Integer-backed property for the drain-order ComboBox SelectedIndex.
        /// Index 0 = Newest first (LIFO), index 1 = Oldest first (FIFO).
        /// </summary>
        public int DrainOrderIndex
        {
            get => DrainOrderNewestFirst ? 0 : 1;
            set => DrainOrderNewestFirst = (value == 0);
        }

        /// <summary>
        /// PERF-03 / D-08: number of chunks uploaded in parallel for chunked (>= 5 MB) uploads.
        /// Persisted via IPluginOptionsAccessor, clamped to [1,4], default 2. Read by
        /// AstrovaultApiClient through an injected getter-delegate so the client never accesses
        /// settings directly. Concurrency = 1 reproduces the exact sequential behavior.
        /// </summary>
        public int ChunkConcurrency
        {
            get
            {
                var stored = pluginSettings.GetValueInt32(nameof(ChunkConcurrency), 2);
                if (stored < 1) return 1;
                if (stored > 4) return 4;
                return stored;
            }
            set
            {
                var clamped = value < 1 ? 1 : (value > 4 ? 4 : value);
                pluginSettings.SetValueInt32(nameof(ChunkConcurrency), clamped);
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(ChunkConcurrencyIndex));
                Logger.Info($"[Astrovault] Chunk concurrency set to {clamped}");
            }
        }

        /// <summary>
        /// Integer-backed property for the ChunkConcurrency ComboBox SelectedIndex.
        /// Index 0..3 maps to concurrency 1..4.
        /// </summary>
        public int ChunkConcurrencyIndex
        {
            get => ChunkConcurrency - 1;
            set => ChunkConcurrency = value + 1;
        }

        public NotificationVerbosity NotificationVerbosity
        {
            get
            {
                var stored = pluginSettings.GetValueString(nameof(NotificationVerbosity), "ErrorsOnly");
                return Enum.TryParse<NotificationVerbosity>(stored, out var result) ? result : NotificationVerbosity.ErrorsOnly;
            }
            set
            {
                pluginSettings.SetValueString(nameof(NotificationVerbosity), value.ToString());
                RaisePropertyChanged();
            }
        }

        // ====================================================================
        // API Key Input Properties
        // ====================================================================

        public string ApiKeyInput
        {
            get => apiKeyInput;
            set
            {
                if (apiKeyInput != value)
                {
                    apiKeyInput = value ?? string.Empty;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(HasApiKeyInput));
                }
            }
        }

        public bool HasApiKeyInput => !string.IsNullOrWhiteSpace(ApiKeyInput);

        public bool IsApiKeyRevealed
        {
            get => isApiKeyRevealed;
            set
            {
                if (isApiKeyRevealed != value)
                {
                    isApiKeyRevealed = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(ApiKeyMasked));
                }
            }
        }

        public string ApiKeyMasked => new string('\u2022', ApiKeyInput?.Length ?? 0);

        // ====================================================================
        // Connection Status Properties
        // ====================================================================

        public Brush StatusDotBrush
        {
            get
            {
                return connectionStatus switch
                {
                    ConnectionStatus.Connected => new SolidColorBrush(Color.FromRgb(50, 205, 50)),  // green
                    ConnectionStatus.Configured => new SolidColorBrush(Color.FromRgb(255, 165, 0)), // orange
                    ConnectionStatus.NotConfigured => Brushes.Gray,
                    _ => new SolidColorBrush(Color.FromRgb(220, 50, 50))  // red for errors
                };
            }
        }

        public string StatusText
        {
            get
            {
                return connectionStatus switch
                {
                    ConnectionStatus.Connected => $"Connected \u2014 {TruncateAccountName(connectedAccountName)}",
                    ConnectionStatus.Configured => "Configured \u2014 verifying connection...",
                    ConnectionStatus.NotConfigured => "Not configured",
                    ConnectionStatus.InvalidKey => "Invalid API key",
                    ConnectionStatus.Unreachable => "Server unreachable \u2014 check your connection",
                    ConnectionStatus.Error => "Unexpected error",
                    _ => "Not configured"
                };
            }
        }

        public string StatusTooltip
        {
            get
            {
                if (connectionStatus == ConnectionStatus.Connected)
                {
                    return connectedAccountName;
                }
                return null;
            }
        }

        public bool IsConnected => connectionStatus == ConnectionStatus.Connected;

        public bool IsTestingConnection
        {
            get => isTestingConnection;
            set
            {
                if (isTestingConnection != value)
                {
                    isTestingConnection = value;
                    RaisePropertyChanged();
                }
            }
        }

        // ====================================================================
        // UI Binding Properties
        // ====================================================================

        public int PendingUploadsCount => uploadManager?.PendingCount ?? 0;

        // ====================================================================
        // Upload Progress & Status Properties
        // ====================================================================

        public string CurrentFileName
        {
            get => currentFileName;
            private set { currentFileName = value; RaisePropertyChanged(); }
        }

        public double CurrentProgress
        {
            get => currentProgress;
            private set { currentProgress = value; RaisePropertyChanged(); }
        }

        public string CurrentChunkText
        {
            get => currentChunkText;
            private set { currentChunkText = value; RaisePropertyChanged(); }
        }

        public bool IsUploading
        {
            get => isUploading;
            private set { isUploading = value; RaisePropertyChanged(); }
        }

        // Queue count properties (delegate to UploadManager)
        public int PendingCount => uploadManager?.PendingCount ?? 0;
        public int UploadingCount => uploadManager?.UploadingCount ?? 0;
        public int CompletedCount => uploadManager?.CompletedCount ?? 0;
        public int FailedCount => uploadManager?.FailedCount ?? 0;
        public bool HasFailedUploads => FailedCount > 0;

        // Warning banner properties (delegate to UploadManager)
        public bool IsCircuitOpen => uploadManager?.IsCircuitOpen ?? false;
        public bool IsCapacityWarning => uploadManager?.IsCapacityWarning ?? false;
        public bool IsStaleQueue => uploadManager?.IsStaleQueue ?? false;
        public int StaleJobCount => uploadManager?.StaleJobCount ?? 0;

        // ====================================================================
        // Blocked-state UI (storage-quota plan §4 Step 3)
        // ====================================================================

        /// <summary>Storage quota exceeded -- queue stopped. Drives the quota banner + Resume button.</summary>
        public bool IsQuotaBlocked => uploadManager?.BlockKind == UploadBlockKind.QuotaExceeded;

        /// <summary>API key rejected -- queue stopped. Drives the auth banner (no Resume button --
        /// only a validated key can clear this, see <see cref="TestConnectionAsync"/>).</summary>
        public bool IsAuthBlocked => uploadManager?.BlockKind == UploadBlockKind.AuthInvalid;

        /// <summary>True when the most recent block transition could not be durably persisted --
        /// delegates to the manager's fail-safe flag. Drives a distinct small warning banner.</summary>
        public bool HasBlockPersistFailureWarning => uploadManager?.HasBlockPersistFailureWarning ?? false;

        /// <summary>
        /// Fixed local wording for a <see cref="QuotaBlockFlavor.PlanRequired"/> block -- the
        /// account has no storage plan at all, so "free up space" is a dead end and there are no
        /// meaningful byte numbers to show (limitBytes is 0 for this flavor -- see
        /// <see cref="QuotaUsageText"/>'s companion limitBytes==0 handling). Never the server's message
        /// text (LogSanitizer / QuotaInfoParser stance).
        /// </summary>
        private const string PlanRequiredBannerText =
            "Upload stopped — this account has no storage plan. Choose a plan on the AstroVault website, then resume. Frames are saved locally.";

        /// <summary>
        /// Persistent quota-blocked banner text (survives restart -- the latch is persisted). Branches
        /// on the persisted <see cref="UploadBlockSnapshot.Flavor"/> first (contract-delta round):
        /// <see cref="QuotaBlockFlavor.PlanRequired"/> always gets the fixed no-numbers wording
        /// above, regardless of what the snapshot's byte fields happen to hold. Otherwise (the default
        /// <see cref="QuotaBlockFlavor.StorageLimitExceeded"/> flavor -- also what an older
        /// persisted block without this field defaults to), numbers come ONLY from the validated
        /// <see cref="Astrovault.Models.UploadBlockSnapshot"/> recorded when the block was entered,
        /// binary-GiB-formatted and labeled "GB" (matches the site); generic wording when the snapshot's
        /// numbers were unavailable at latch time.
        /// </summary>
        public string QuotaBlockedBannerText
        {
            get
            {
                var snapshot = uploadManager?.BlockSnapshot;
                var flavor = snapshot?.Flavor ?? QuotaBlockFlavor.StorageLimitExceeded;

                if (flavor == QuotaBlockFlavor.PlanRequired)
                {
                    return PlanRequiredBannerText;
                }

                // Pass H P2: LimitBytes must also be POSITIVE -- a no-plan account whose 507 arrived
                // without a recognizable flavor code still has limitBytes 0, and "0.0 GB of 0.0 GB
                // used" is forbidden output. Zero-limit snapshots fall through to the generic wording.
                if (snapshot?.UsedBytes.HasValue == true && snapshot.LimitBytes.HasValue == true
                    && snapshot.LimitBytes.Value > 0)
                {
                    return "Upload stopped — storage full. "
                        + $"{ByteFormatter.FormatGB(snapshot.UsedBytes.Value)} of {ByteFormatter.FormatGB(snapshot.LimitBytes.Value)} used. "
                        + "Frames are saved locally. Free up space or upgrade your plan, then resume.";
                }
                return "Upload stopped — storage full. Frames are saved locally. Free up space or upgrade your plan, then resume.";
            }
        }

        /// <summary>Persistent auth-blocked banner text (survives restart). Fixed wording -- an
        /// invalid/revoked key carries no numbers to display.</summary>
        public string AuthBlockedBannerText =>
            "Upload stopped — API key rejected. Check your key in the Astrovault options, then reconnect.";

        /// <summary>
        /// Whether the circuit-open banner should be visible. Block banners take visual precedence over
        /// the circuit banner (storage-quota plan §4 Step 2/3) so the two can never mislead
        /// simultaneously -- in practice a block always resets/closes the breaker before latching (see
        /// <c>UploadManager.EnterBlockAsync</c>), but this is enforced at the UI layer too, defensively.
        /// <see cref="IsCircuitOpen"/> itself is left unchanged (still used for the SendNotification
        /// error-suppression check) -- this is a SEPARATE, banner-visibility-only property.
        /// </summary>
        public bool ShowCircuitOpenBanner => IsCircuitOpen && !IsQuotaBlocked && !IsAuthBlocked;

        // ====================================================================
        // Quota usage line (storage-quota plan §4 Step 6)
        // ====================================================================

        /// <summary>Whether a usable quota-usage fetch has ever completed (hidden when null/no data,
        /// e.g. never fetched yet, an older server without the endpoint, or unavailable numbers).
        /// Also hidden when <c>LimitBytes</c> is exactly 0 (contract-delta round): the quota endpoint
        /// is always-200 and can return <c>limitBytes: 0</c> for a no-plan account, where
        /// "X.X GB of 0.0 GB used" would be a meaningless fraction -- simplest fix is hiding the line
        /// entirely rather than fabricating a plan-name/absence display for it.</summary>
        public bool HasQuotaUsage => lastQuotaUsage?.UsedBytes.HasValue == true
            && lastQuotaUsage.LimitBytes.HasValue == true
            && lastQuotaUsage.LimitBytes.Value != 0;

        /// <summary>"12.4 GB of 10.0 GB used (Plan Name)" for the Upload Status panel. Plan name suffix
        /// only when present. Refreshed ONLY on Test-Connection success, queue-idle transition, and
        /// block transitions -- see <see cref="RefreshQuotaUsageAsync"/>. No polling.</summary>
        public string QuotaUsageText
        {
            get
            {
                if (!HasQuotaUsage)
                {
                    return string.Empty;
                }

                // Two decimals (owner request, dev-testing round): one decimal is blind near the
                // limit, exactly where the number matters most.
                var text = $"{ByteFormatter.FormatGB2(lastQuotaUsage.UsedBytes.Value)} of {ByteFormatter.FormatGB2(lastQuotaUsage.LimitBytes.Value)} used";
                if (!string.IsNullOrWhiteSpace(lastQuotaUsage.PlanName))
                {
                    text += $" ({lastQuotaUsage.PlanName})";
                }

                // Free-space suffix when it is getting tight (< 512 MiB): at that point "free" is the
                // number an imaging session actually needs, and the used/limit pair can no longer
                // show it. Prefer the server's remainingBytes; derive defensively when absent.
                var remaining = lastQuotaUsage.RemainingBytes
                    ?? Math.Max(0L, lastQuotaUsage.LimitBytes.Value - lastQuotaUsage.UsedBytes.Value);
                if (remaining < 512L * 1024 * 1024)
                {
                    text += $" · {ByteFormatter.FormatCompact(remaining)} free";
                }
                return text;
            }
        }

        /// <summary>
        /// Whether a corrupt queue was quarantined on load (manual-restore-only). Bound by the
        /// existing warning-banner section, mirroring IsStaleQueue.
        /// </summary>
        public bool HasCorruptQueueWarning => uploadManager?.HasCorruptQueueWarning ?? false;

        /// <summary>
        /// Computed text for the corrupt-queue warning banner.
        /// </summary>
        public string CorruptQueueText =>
            $"Queue file was corrupt and quarantined to {uploadManager?.CorruptQueueFileName ?? "a .corrupt file"}. Started a fresh queue; restore manually if needed.";

        /// <summary>
        /// Computed text for stale queue warning banner.
        /// </summary>
        public string StaleQueueText => $"{StaleJobCount} uploads from previous session";

        /// <summary>
        /// Computed text for progress percentage display.
        /// </summary>
        public string ProgressPercentText => $"{CurrentProgress:P0}";

        // ====================================================================
        // Notification Helpers
        // ====================================================================

        /// <summary>
        /// Integer-backed property for ComboBox SelectedIndex binding.
        /// Maps directly to NotificationVerbosity enum values (Off=0, ErrorsOnly=1, AllUploads=2).
        /// </summary>
        public int NotificationVerbosityIndex
        {
            get => (int)NotificationVerbosity;
            set
            {
                NotificationVerbosity = (NotificationVerbosity)value;
                RaisePropertyChanged();
            }
        }

        private void SendNotification(UploadCompletedEventArgs e)
        {
            var verbosity = NotificationVerbosity;

            if (verbosity == NotificationVerbosity.Off)
            {
                Logger.Info("[Astrovault] Notification skipped (verbosity=Off)");
                return;
            }

            if (e.Success)
            {
                if (verbosity == NotificationVerbosity.AllUploads)
                {
                    Notification.ShowSuccess($"{e.FileName} uploaded");
                    Logger.Info($"[Astrovault] Notification sent: success - {e.FileName} uploaded");
                }
                return;
            }

            // Error notification -- suppress individual errors when circuit is open
            if (IsCircuitOpen)
            {
                Logger.Info("[Astrovault] Notification suppressed (circuit open)");
                return;
            }

            var actionable = ToActionableError(e.FileName, e.ErrorMessage, e.StatusCode, e.FailureKind);
            Notification.ShowError(actionable);
            Logger.Info($"[Astrovault] Notification sent: error - {actionable}");
        }

        // ====================================================================
        // Error Message Mapping
        // ====================================================================

        /// <summary>
        /// Maps raw upload error messages to actionable user-facing text.
        /// Returns "{fileName}: {actionable message}" for every input.
        /// G5 fix: <paramref name="statusCode"/>/<paramref name="failureKind"/> (when supplied by a
        /// caller that already ran the status through <see cref="Core.FailureClassifier"/>) are
        /// consulted FIRST -- a typed classification, not a substring of the message text. Only
        /// failures with no status code (statusCode 0 -- local exceptions, the HTTPS send-boundary
        /// gate, exhausted session restarts, or any legacy caller that omits the new parameters)
        /// fall through to the original string-substring mapping below.
        /// </summary>
        internal static string ToActionableError(
            string fileName,
            string errorMessage,
            int statusCode = 0,
            FailureKind failureKind = FailureKind.Transient)
        {
            if (string.IsNullOrEmpty(errorMessage))
                return errorMessage is null
                    ? $"{fileName}: Unknown error"
                    : $"{fileName}: Upload failed (unknown reason)";

            if (failureKind == FailureKind.PermanentAuth)
                return $"{fileName}: API key rejected. Reconnect with a valid key in plugin settings.";

            if (failureKind == FailureKind.PermanentQuota)
                return $"{fileName}: Storage quota exceeded. Free up space or upgrade your plan, then resume.";

            if (failureKind == FailureKind.PermanentSkipFile && (statusCode == 400 || statusCode == 413))
                return $"{fileName}: Server rejected this file ({(statusCode == 413 ? "too large" : "invalid request")}). Skipped -- other uploads continue.";

            switch (statusCode)
            {
                case 429:
                    return $"{fileName}: Rate limited. Will retry with backoff.";
                case 500:
                case 502:
                case 503:
                    return $"{fileName}: Server error. Will retry automatically.";
            }

            // Legacy string-substring fallback: statusCode 0 / FailureKind not one of the above
            // (local exceptions, the HTTPS gate, exhausted-restart messages, generic Exception.Message).
            if (errorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return $"{fileName}: Source file was deleted or moved. Check that the file still exists.";
            if (errorMessage.Contains("access denied", StringComparison.OrdinalIgnoreCase)
                || errorMessage.Contains("unauthorized access", StringComparison.OrdinalIgnoreCase))
                return $"{fileName}: Permission denied. Check file permissions or antivirus settings.";
            if (errorMessage.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase)
                || errorMessage.Contains("401", StringComparison.Ordinal))
                return $"{fileName}: API key rejected. Reconnect with a valid key in plugin settings.";
            if (errorMessage.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || errorMessage.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                return $"{fileName}: Network timeout. Will retry automatically.";
            if (errorMessage.Contains("session expired", StringComparison.OrdinalIgnoreCase))
                return $"{fileName}: Upload session expired after multiple restarts. Retry manually.";
            if (errorMessage.Contains("500", StringComparison.Ordinal)
                || errorMessage.Contains("502", StringComparison.Ordinal)
                || errorMessage.Contains("503", StringComparison.Ordinal)
                || errorMessage.Contains("internal server", StringComparison.OrdinalIgnoreCase))
                return $"{fileName}: Server error. Will retry automatically.";
            if (errorMessage.Contains("429", StringComparison.Ordinal)
                || errorMessage.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
                return $"{fileName}: Rate limited. Will retry with backoff.";

            return $"{fileName}: {errorMessage}";
        }

        // ====================================================================
        // Helper Methods
        // ====================================================================

        /// <summary>
        /// Truncates account name at 30 characters with ellipsis.
        /// Full name available via StatusTooltip.
        /// </summary>
        private static string TruncateAccountName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            if (name.Length > 30)
            {
                return name[..30] + "\u2026";
            }

            return name;
        }

        // ====================================================================
        // Auth Action Methods
        // ====================================================================

        /// <summary>
        /// Validates the entered API key against the server.
        /// On success: stores key, sets Connected status, clears input field.
        /// On failure: sets appropriate error status.
        /// </summary>
        public async Task TestConnectionAsync()
        {
            IsTestingConnection = true;
            RefreshAuthUI();

            try
            {
                Logger.Info($"[Astrovault] Testing connection with key {LogSanitizer.MaskApiKey(ApiKeyInput)}");

                // Pass H P1: captured BEFORE the validate await. If the context generation moves while
                // the HTTP call is in flight (an endpoint reinit racing this connect), everything in
                // `result` -- including accountId -- was produced by the OLD context and must not be
                // applied to the new one.
                var preValidateGeneration = uploadManager?.ContextGeneration ?? 0;

                var result = await authManager.ValidateApiKeyAsync(ApiKeyInput);

                if (result.Valid)
                {
                    // Storage-quota plan directive E (C-P1#2) + pass I fix: a REPLACEMENT key must not
                    // inherit the OLD account's cached identity, and the staleness decision for the
                    // validate response must be ATOMIC with that clear. TryBeginConnectAccountCycle
                    // compares against preValidateGeneration and performs the clear+bump in one lock
                    // hold: on success this connect owns connectContextGeneration; on failure the
                    // context moved (an endpoint swap raced the validate await, or between the await
                    // and here -- the check-then-act gap pass H's fix still had), the validate
                    // response's accountId is discarded, and the old-account clear still happens via
                    // the plain SetAccountId(null) below so the pass-D rule holds regardless.
                    var connectContextGeneration = 0;
                    var ownsConnectCycle = uploadManager != null
                        && uploadManager.TryBeginConnectAccountCycle(preValidateGeneration, out connectContextGeneration);
                    if (uploadManager != null && !ownsConnectCycle)
                    {
                        uploadManager.SetAccountId(null);
                        Logger.Debug("[Astrovault] Context changed around key validation -- validate-response accountId discarded as stale");
                    }
                    authManager.StoreApiKey(ApiKeyInput, result.AccountName);
                    connectionStatus = ConnectionStatus.Connected;
                    connectedAccountName = result.AccountName ?? string.Empty;
                    // SC6 / P19-06: clear the typed key and re-hide it on success so no key residue
                    // lingers in the input/reveal state. The connected status/tooltip still carries the
                    // account name for the UI; it must NOT appear in the shared log.
                    ApiKeyInput = string.Empty;
                    IsApiKeyRevealed = false;
                    Logger.Info("[Astrovault] Connection successful");

                    // Contract-delta round item 2: the validate response's optional accountId (when
                    // present) is threaded into the manager's block identity scoping right here -- AFTER
                    // StoreApiKey (pass D ordering preserved above), through the SAME generation-checked
                    // path SetAccountIdAsync uses everywhere else -- so account identity is known at
                    // connect time, before any quota fetch (RefreshQuotaUsageAsync below).
                    // Pass H/I P1: applied ONLY when this connect atomically owns the cycle -- a stale
                    // accountId from the pre-swap context (endpoint A) can never land under the
                    // post-swap one (endpoint B), no matter where the swap interleaved. Identity then
                    // arrives through the quota fetch under the CURRENT context instead; nothing is
                    // lost, only deferred. Movement AFTER the atomic begin is caught by the
                    // generation-checked apply itself.
                    if (uploadManager != null && !string.IsNullOrEmpty(result.AccountId) && ownsConnectCycle)
                    {
                        await uploadManager.SetAccountIdAsync(result.AccountId, connectContextGeneration).ConfigureAwait(true);
                    }

                    // Storage-quota plan §4 Step 5 (round-2 P1, the credential gate): a successful key
                    // validation is the ONLY trigger allowed to clear an AuthInvalid block -- the generic
                    // Resume button/toggle must never restart doomed uploads against a still-dead key.
                    // Clear it here BEFORE ensuring the pipeline runs.
                    if (uploadManager != null && uploadManager.BlockKind == UploadBlockKind.AuthInvalid)
                    {
                        var resumed = await uploadManager.ResumeAsync();
                        Logger.Info(resumed
                            ? "[Astrovault] Auth block cleared after successful key validation"
                            : "[Astrovault] Auth-block clear could not be persisted durably -- still blocked");
                    }

                    // Storage-quota plan §4 Step 4: replaces the old spot-fix (unconditional
                    // restart-if-not-running -- Bugfix: DisconnectAsync stops the upload loop for WR-06
                    // key-revocation safety, but the reconnect path never restarted it, so after a
                    // Disconnect -> Connect cycle captured images enqueued but never uploaded until a full
                    // plugin/NINA reload) with the single coalesced EnsureUploadPipelineAsync choke point
                    // shared by every step-4 trigger.
                    await EnsureUploadPipelineAsync();

                    // Storage-quota plan §4 Step 6: Test-Connection success is one of the three allowed
                    // quota-usage refresh triggers. Awaited directly (unlike the other two triggers,
                    // which fire-and-forget from an already-UI-marshaled background event) since this
                    // method already runs on the UI thread via the button click and callers already
                    // await the whole thing.
                    await RefreshQuotaUsageAsync();
                }
                else
                {
                    connectionStatus = ClassifyError(result.ErrorMessage);
                    Logger.Warning($"[Astrovault] Connection failed: {result.ErrorMessage}");
                    // P19-06: a failed connect also clears the typed key and re-hides it; the user re-pastes.
                    ApiKeyInput = string.Empty;
                    IsApiKeyRevealed = false;
                }
            }
            catch (Exception ex)
            {
                connectionStatus = ConnectionStatus.Error;
                Logger.Error($"[Astrovault] Connection test error: {ex.Message}");
                // P19-06: clear + re-hide on the error path too, so no key residue survives any exit.
                ApiKeyInput = string.Empty;
                IsApiKeyRevealed = false;
            }
            finally
            {
                IsTestingConnection = false;
                RefreshAuthUI();
            }
        }

        /// <summary>
        /// Clears stored API key and resets to NotConfigured status.
        /// Thin overload that disconnects WITHOUT touching auto-upload; delegates to
        /// DisconnectAsync(alsoDisableUpload: false) so existing callers are unchanged.
        /// </summary>
        public async Task DisconnectAsync()
        {
            await DisconnectAsync(alsoDisableUpload: false);
        }

        /// <summary>
        /// Clears stored API key and resets to NotConfigured status, optionally also disabling
        /// automatic upload (P19-05 re-consent). The disconnect confirmation prompt lives in the
        /// Options code-behind (plan 06); when the user opts to also turn off auto-upload it passes
        /// alsoDisableUpload: true and this disables UploadEnabled before clearing the key.
        /// WR-06: stop and drain the upload manager BEFORE clearing the key so an in-flight upload
        /// (which captured the key into a local) does not keep uploading with a credential the user
        /// just revoked. Mirrors ReinitializeHttpServicesAsync's stop-before-swap ordering.
        /// </summary>
        public async Task DisconnectAsync(bool alsoDisableUpload)
        {
            try
            {
                if (uploadManager != null && uploadManager.IsRunning)
                {
                    Logger.Info("[Astrovault] Stopping upload manager before disconnect...");
                    await uploadManager.StopAsync();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Astrovault] Failed to stop upload manager during disconnect: {ex.Message}");
            }

            // P19-05: honor the consent choice -- turn off auto-upload BEFORE clearing the key so a
            // re-connect later does not silently resume uploading without the user re-enabling it.
            if (alsoDisableUpload)
            {
                UploadEnabled = false;
            }

            authManager.ClearApiKey();
            // Storage-quota plan directive E (B#4): a disconnected credential must not leave a STALE
            // cached accountId behind for a later block to inherit -- the unconditional SetAccountId
            // both clears it immediately and bumps ContextGeneration, so any quota fetch already in
            // flight when the key was cleared resolves as stale (SetAccountIdAsync) and is dropped.
            uploadManager?.SetAccountId(null);
            connectionStatus = ConnectionStatus.NotConfigured;
            connectedAccountName = string.Empty;
            RefreshAuthUI();
            Logger.Info("[Astrovault] Disconnected");
        }

        // ====================================================================
        // Queue Action Methods (Gap Closure: INT-01)
        // ====================================================================

        /// <summary>
        /// Retries all failed upload jobs by resetting them to Pending status.
        /// Relays to uploadManager.RetryAllFailedAsync() which handles iteration and logging.
        /// QueueStateChanged fires inside RetryFailedJobAsync, triggering RefreshQueueUI.
        /// </summary>
        public async Task RetryAllFailedAsync()
        {
            if (uploadManager == null) return;  // WR-04: no-op in the REL-16 degraded (Error) state
            await uploadManager.RetryAllFailedAsync();
        }

        /// <summary>
        /// Dismisses all failed upload jobs, removing them from the visible queue.
        /// Does not delete source files. QueueStateChanged fires inside DismissAllFailedAsync.
        /// </summary>
        public async Task DismissAllFailedAsync()
        {
            if (uploadManager == null) return;  // WR-04: no-op in the REL-16 degraded (Error) state
            await uploadManager.DismissAllFailedAsync();
        }

        /// <summary>
        /// Resets the historical counters (Total uploaded + Failed) to 0. Relays to
        /// uploadManager.ResetCountsAsync(), which clears the running total and the retained
        /// Completed/Failed records; QueueStateChanged fires there to refresh the UI. Pending and
        /// in-progress uploads are preserved, and no source files are deleted.
        /// </summary>
        public async Task ResetCountsAsync()
        {
            if (uploadManager == null) return;  // WR-04: "Reset Counts" is always enabled; no-op when degraded
            await uploadManager.ResetCountsAsync();
        }

        /// <summary>
        /// Classifies error message text into a ConnectionStatus value.
        /// </summary>
        private static ConnectionStatus ClassifyError(string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage))
            {
                return ConnectionStatus.Error;
            }

            if (errorMessage.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase))
            {
                return ConnectionStatus.InvalidKey;
            }

            if (errorMessage.Contains("Server unreachable", StringComparison.OrdinalIgnoreCase))
            {
                return ConnectionStatus.Unreachable;
            }

            return ConnectionStatus.Error;
        }

        /// <summary>
        /// Raises PropertyChanged for all auth-related UI properties.
        /// </summary>
        private void RefreshAuthUI()
        {
            RaisePropertyChanged(nameof(IsConnected));
            RaisePropertyChanged(nameof(StatusDotBrush));
            RaisePropertyChanged(nameof(StatusText));
            RaisePropertyChanged(nameof(StatusTooltip));
            RaisePropertyChanged(nameof(IsTestingConnection));
            RaisePropertyChanged(nameof(HasApiKeyInput));
        }

        /// <summary>
        /// Raises PropertyChanged for all queue-related UI properties.
        /// </summary>
        private void RefreshQueueUI()
        {
            RaisePropertyChanged(nameof(PendingCount));
            RaisePropertyChanged(nameof(UploadingCount));
            RaisePropertyChanged(nameof(CompletedCount));
            RaisePropertyChanged(nameof(FailedCount));
            RaisePropertyChanged(nameof(HasFailedUploads));
            RaisePropertyChanged(nameof(IsCircuitOpen));
            RaisePropertyChanged(nameof(IsCapacityWarning));
            RaisePropertyChanged(nameof(IsStaleQueue));
            RaisePropertyChanged(nameof(StaleJobCount));
            RaisePropertyChanged(nameof(StaleQueueText));
            RaisePropertyChanged(nameof(HasCorruptQueueWarning));
            RaisePropertyChanged(nameof(CorruptQueueText));
            RaisePropertyChanged(nameof(PendingUploadsCount));
            // Storage-quota plan §4 Step 3: blocked-state visibility bundle.
            RaisePropertyChanged(nameof(IsQuotaBlocked));
            RaisePropertyChanged(nameof(IsAuthBlocked));
            RaisePropertyChanged(nameof(QuotaBlockedBannerText));
            RaisePropertyChanged(nameof(AuthBlockedBannerText));
            RaisePropertyChanged(nameof(HasBlockPersistFailureWarning));
            RaisePropertyChanged(nameof(ShowCircuitOpenBanner));
        }

        // ====================================================================
        // INotifyPropertyChanged Implementation
        // ====================================================================

        public event PropertyChangedEventHandler PropertyChanged;

        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
