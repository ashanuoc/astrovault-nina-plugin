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

        // Reinit serialization lock
        private readonly SemaphoreSlim reinitLock = new SemaphoreSlim(1, 1);

        // Background validation state
        private int validationGeneration;
        private CancellationTokenSource validationCts;

        // Progress & status state
        private string currentFileName = string.Empty;
        private double currentProgress;
        private string currentChunkText = string.Empty;
        private bool isUploading;
        private bool wasCircuitOpen;

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
        /// Serialized by reinitLock to prevent concurrent reinit from rapid endpoint changes.
        /// Stops UploadManager before swap to drain active uploads, preventing ObjectDisposedException.
        /// Normalizes URL trailing slash so HttpClient.BaseAddress resolves relative URIs correctly.
        /// </summary>
        private async Task ReinitializeHttpServicesAsync()
        {
            // Serialize: if another reinit is already running, wait for it
            await reinitLock.WaitAsync();
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

                // Step E: Swap the client reference in upload manager (safe -- manager is stopped)
                uploadManager.UpdateApiClient(cloudApiClient);

                // Step F: Dispose old instances after swap
                oldApiClient?.Dispose();
                oldAuthManager?.Dispose();

                // Step G: Restart the upload manager if we have a valid config
                if (authManager.IsConfigured)
                {
                    connectionStatus = ConnectionStatus.Configured;
                    connectedAccountName = authManager.AccountName ?? string.Empty;
                    ValidateConnectionInBackground();

                    // Restart upload processing with fresh client
                    await uploadManager.StartAsync(CancellationToken.None);
                }
                else
                {
                    connectionStatus = ConnectionStatus.NotConfigured;
                    connectedAccountName = string.Empty;
                }

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
                reinitLock.Release();
            }
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
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                IsUploading = false;
                RefreshQueueUI();
                SendNotification(e);
            });
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
                else if (wasCircuitOpen && !circuitNowOpen)
                {
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
                // Fire-and-forget: reinit is serialized by reinitLock so rapid changes queue safely.
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
                pluginSettings.SetValueBoolean(nameof(UploadEnabled), value);
                RaisePropertyChanged();
                Logger.Info($"[Astrovault] Upload enabled: {value}");
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

            var actionable = ToActionableError(e.FileName, e.ErrorMessage);
            Notification.ShowError(actionable);
            Logger.Info($"[Astrovault] Notification sent: error - {actionable}");
        }

        // ====================================================================
        // Error Message Mapping
        // ====================================================================

        /// <summary>
        /// Maps raw upload error messages to actionable user-facing text.
        /// Returns "{fileName}: {actionable message}" for every input.
        /// </summary>
        internal static string ToActionableError(string fileName, string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage))
                return errorMessage is null
                    ? $"{fileName}: Unknown error"
                    : $"{fileName}: Upload failed (unknown reason)";

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

                var result = await authManager.ValidateApiKeyAsync(ApiKeyInput);

                if (result.Valid)
                {
                    authManager.StoreApiKey(ApiKeyInput, result.AccountName);
                    connectionStatus = ConnectionStatus.Connected;
                    connectedAccountName = result.AccountName ?? string.Empty;
                    // SC6 / P19-06: clear the typed key and re-hide it on success so no key residue
                    // lingers in the input/reveal state. The connected status/tooltip still carries the
                    // account name for the UI; it must NOT appear in the shared log.
                    ApiKeyInput = string.Empty;
                    IsApiKeyRevealed = false;
                    Logger.Info("[Astrovault] Connection successful");

                    // Bugfix: DisconnectAsync stops the upload loop (WR-06 key-revocation safety), but the
                    // reconnect path never restarted it -- so after a Disconnect -> Connect cycle, captured
                    // images enqueued but never uploaded until a full plugin/NINA reload. Resume draining
                    // when the loop is not already running (a fresh plugin load already started it in
                    // StartServices; not-running here is exactly the post-Disconnect reconnect case).
                    if (uploadManager != null && !uploadManager.IsRunning)
                    {
                        await uploadManager.StartAsync(uploadCts?.Token ?? CancellationToken.None);
                        Logger.Info("[Astrovault] Upload manager restarted after reconnect");
                    }
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
