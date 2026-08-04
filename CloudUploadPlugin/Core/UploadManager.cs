using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Data;
using Astrovault.Interfaces;
using Astrovault.Models;
using NINA.Core.Utility;

namespace Astrovault.Core
{
    /// <summary>
    /// Background upload processor with jittered exponential backoff retry
    /// and circuit breaker protection against sustained API outages.
    /// Processes jobs from the queue and uploads via ICloudApiClient.
    /// </summary>
    public class UploadManager : IUploadManager
    {
        private readonly IUploadQueueRepository queueRepository;
        private ICloudApiClient apiClient;
        // Storage-quota plan §4 Step 4 (G13 fix): mutable so UpdateContext can atomically swap in a
        // fresh, LIVE auth manager on endpoint reinit. Was readonly -- a reinit that recreated the
        // AuthManager and disposed the old one (while this field still pointed at it) left the manager
        // reading a dead instance's key forever (running-but-permanently-idle, silent).
        private IAuthManager authManager;
        private readonly CircuitBreaker circuitBreaker = new CircuitBreaker();
        private readonly SemaphoreSlim lifecycleLock = new SemaphoreSlim(1, 1);

        // Storage-quota plan §4 Step 5: lets ResumeAsync wake the idle drain loop immediately instead of
        // waiting out the up to ~1s idle poll interval, so the pinned resume job is attempted right away.
        private readonly SemaphoreSlim wakeSignal = new SemaphoreSlim(0, 1);

        private CancellationTokenSource cts;
        private Task processingTask;
        private int pendingCount;
        private int completedCount;
        private int failedCount;
        private int uploadingCount;

        private const int MaxRetries = 5;
        private const double BaseDelaySeconds = 2.0;
        private const double MaxDelaySeconds = 60.0;
        private const long CapacityWarningThresholdBytes = 10L * 1024 * 1024 * 1024; // 10 GB

        public event EventHandler<UploadProgressEventArgs> ProgressChanged;
        public event EventHandler<UploadCompletedEventArgs> UploadCompleted;
        public event EventHandler QueueStateChanged;
        public event EventHandler<UploadBlockedEventArgs> UploadBlocked;

        public int PendingCount => pendingCount;
        public int CompletedCount => completedCount;
        public int FailedCount => failedCount;
        public int UploadingCount => uploadingCount;
        public bool IsRunning => processingTask != null && !processingTask.IsCompleted;

        /// <inheritdoc />
        public bool IsCircuitOpen => circuitBreaker.IsOpen;

        /// <inheritdoc />
        public bool IsCapacityWarning { get; private set; }

        /// <inheritdoc />
        public bool IsStaleQueue => queueRepository.IsStaleQueue;

        /// <inheritdoc />
        public int StaleJobCount => queueRepository.StaleJobCount;

        /// <inheritdoc />
        public bool HasCorruptQueueWarning => queueRepository.HasCorruptQueueWarning;

        /// <inheritdoc />
        public string CorruptQueueFileName => queueRepository.CorruptQueueFileName;

        // ====================================================================
        // Blocked-state machine (storage-quota plan §4 Step 2)
        // ====================================================================

        // Storage-quota plan directive B (the single transition gate): these three fields are always
        // read/written together under blockStateLock, a lightweight SYNCHRONOUS monitor deliberately
        // SEPARATE from the async lifecycleLock. EnterBlockAsync's in-memory mutation runs INSIDE the
        // drain loop (the very task StopAsync awaits WHILE holding lifecycleLock); if this mutation
        // took lifecycleLock too, a StopAsync-in-progress would deadlock against its own drain loop.
        // blockStateLock only ever guards a few plain field assignments (no I/O, no awaiting), so it is
        // always held for a bounded, tiny duration regardless of which caller (drain loop or an
        // external transition like ResumeAsync/RestoreBlockStateAsync, both under lifecycleLock) holds
        // it at any moment.
        private readonly object blockStateLock = new object();
        private UploadBlockKind blockKind = UploadBlockKind.None;
        private UploadBlockSnapshot blockSnapshot;
        private bool hasBlockPersistFailureWarning;

        // Storage-quota plan directive E (C-P1#4b): a lightweight synchronous gate (mirrors
        // blockStateLock's pattern) guarding the (accountId, contextGeneration) pair as a single unit,
        // so a reader (EnterBlockAsync's latch-time snapshot, RestoreBlockStateAsync's fallback) never
        // observes a stale accountId racing a concurrent SetAccountId/SetAccountIdAsync write -- a
        // "mixed identity". Deliberately SEPARATE from lifecycleLock (SetAccountIdAsync already holds
        // that for its own longer async work) so this stays a tiny, always-uncontended, never-awaited-
        // across gate purely for the two plain fields below.
        private readonly object accountIdentityLock = new object();

        // Storage-quota plan §4 Step 6: set via SetAccountId after a successful GetQuotaAsync. Threaded
        // into EnterBlockAsync's identity input and used as RestoreBlockStateAsync's fallback when its
        // optional parameter is omitted -- activates account-based block scoping once known. Always
        // read/written under accountIdentityLock together with contextGeneration below.
        private string accountId;

        // Storage-quota plan directive E (A#6, B#4): bumped by UpdateContext and by the unconditional
        // SetAccountId (an authoritative direct clear/set) so a stale in-flight quota fetch's eventual
        // SetAccountIdAsync call -- captured against an OLDER generation -- is recognized as stale and
        // dropped instead of persisting an accountId that belonged to a context that no longer exists.
        private int contextGeneration;

        /// <inheritdoc />
        public UploadBlockKind BlockKind { get { lock (blockStateLock) { return blockKind; } } }

        /// <inheritdoc />
        public UploadBlockSnapshot BlockSnapshot { get { lock (blockStateLock) { return blockSnapshot; } } }

        /// <inheritdoc />
        public bool HasBlockPersistFailureWarning { get { lock (blockStateLock) { return hasBlockPersistFailureWarning; } } }

        /// <inheritdoc />
        public int ContextGeneration { get { lock (accountIdentityLock) { return contextGeneration; } } }

        public UploadManager(
            IUploadQueueRepository queueRepository,
            ICloudApiClient apiClient,
            IAuthManager authManager)
        {
            this.queueRepository = queueRepository;
            this.apiClient = apiClient;
            this.authManager = authManager;
        }

        /// <summary>
        /// Test seam: shrinks the internal circuit breaker's recovery timeout so the deterministic
        /// TC-16 auto-recovery E2E test can drive the Open -> HalfOpen probe transition in a few
        /// hundred milliseconds instead of waiting a real 60s. Production code never calls this, so
        /// default breaker behavior (60s) is unchanged.
        /// </summary>
        internal void SetCircuitBreakerRecoveryTimeoutForTest(TimeSpan timeout)
        {
            circuitBreaker.RecoveryTimeout = timeout;
        }

        /// <inheritdoc />
        public void UpdateContext(IAuthManager newAuthManager, ICloudApiClient newClient)
        {
            // Storage-quota plan §4 Step 4 (G13 fix): both references are swapped together, atomically
            // from the caller's perspective (this method does no awaiting), so ProcessNextJobAsync can
            // never observe a stale authManager paired with a fresh apiClient (or vice versa). Callers
            // must only invoke this while the manager is stopped (same discipline as before). Guarded by
            // lifecycleLock (directive B's single transition gate) -- synchronous Wait/Release since this
            // method itself never awaits.
            lifecycleLock.Wait();
            try
            {
                authManager = newAuthManager ?? throw new ArgumentNullException(nameof(newAuthManager));
                apiClient = newClient ?? throw new ArgumentNullException(nameof(newClient));
                // Storage-quota plan directive E (A#6, B#4): a new context means a new identity -- the
                // cached accountId belonged to the OLD endpoint/credential pair and must never leak into
                // a block latched under the new one. SetAccountId(null) both clears it and bumps
                // ContextGeneration, so any quota fetch still in flight against the OLD context that
                // resolves later is recognized as stale by SetAccountIdAsync and dropped.
                SetAccountId(null);
                Logger.Info("[UploadManager] Context updated (auth manager + API client) for new endpoint");
            }
            finally
            {
                lifecycleLock.Release();
            }
        }

        /// <inheritdoc />
        public void SetAccountId(string accountId)
        {
            // Storage-quota plan directive E (C-P1#4b): accountId and contextGeneration are always
            // mutated together, under accountIdentityLock, so a concurrent reader (GetCachedAccountId)
            // can never observe one without the other having moved together.
            lock (accountIdentityLock)
            {
                contextGeneration++;
                this.accountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId;
            }
        }

        /// <inheritdoc />
        public bool TryBeginConnectAccountCycle(int expectedGeneration, out int connectGeneration)
        {
            // Pass I fix: the connect flow's clear-old-account step must be ATOMIC with the staleness
            // decision. A check-then-act (compare generation, then SetAccountId(null)) leaves a window
            // where an endpoint swap lands between the two, rebases the clear onto the NEW context, and
            // lets the OLD endpoint's validate-response accountId through. Here the compare and the
            // clear+bump happen under one accountIdentityLock hold: either this connect cycle owns the
            // resulting generation, or the caller learns the context moved and discards the stale data.
            lock (accountIdentityLock)
            {
                if (contextGeneration != expectedGeneration)
                {
                    connectGeneration = contextGeneration;
                    return false;
                }

                contextGeneration++;
                accountId = null;
                connectGeneration = contextGeneration;
                return true;
            }
        }

        /// <inheritdoc />
        public async Task<bool> SetAccountIdAsync(string accountId, int expectedContextGeneration)
        {
            await lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!TryApplyAccountIdForGeneration(accountId, expectedContextGeneration))
                {
                    Logger.Info("[UploadManager] Dropped stale accountId (context changed since the fetch started)");
                    return false;
                }

                var normalized = string.IsNullOrWhiteSpace(accountId) ? null : accountId;
                if (!string.IsNullOrEmpty(normalized))
                {
                    // Hardening round F (Layer 1a): pass the generation this accountId was just
                    // validated against -- TryApplyAccountIdForGeneration above already proved it equals
                    // the CURRENT contextGeneration at this instant, so it doubles as the entry snapshot
                    // the reconciliation re-checks after its own fingerprint await.
                    await ReconcileLiveBlockForAccountChangeLockedAsync(normalized, expectedContextGeneration).ConfigureAwait(false);
                }

                return true;
            }
            finally
            {
                lifecycleLock.Release();
            }
        }

        /// <summary>
        /// Storage-quota plan directive E (C-P1#4b): reads the cached accountId under the SAME
        /// <see cref="accountIdentityLock"/> gate <see cref="SetAccountId"/>/<see cref="SetAccountIdAsync"/>
        /// write it under, so a caller (<see cref="EnterBlockAsync"/>'s latch-time identity snapshot,
        /// <see cref="RestoreBlockStateAsync"/>'s fallback) never observes a value racing a concurrent
        /// account-identity transition.
        /// </summary>
        private string GetCachedAccountId()
        {
            lock (accountIdentityLock)
            {
                return accountId;
            }
        }

        /// <summary>
        /// D-verdict fix (P1 #1 / item 4): atomically snapshots BOTH the cached accountId and the
        /// generation it was read under, so a caller about to await something (a latch-time quota
        /// lookup, a fingerprint compute) can later tell whether the context moved out from under it
        /// while it was suspended.
        /// </summary>
        private (string AccountId, int Generation) GetCachedAccountIdWithGeneration()
        {
            lock (accountIdentityLock)
            {
                return (accountId, contextGeneration);
            }
        }

        /// <summary>
        /// D-verdict fix (P1 #1): applies <paramref name="newAccountId"/> to the cached field ONLY IF
        /// <see cref="contextGeneration"/> still equals <paramref name="expectedGeneration"/> at the
        /// instant of the write (checked and written atomically under the same
        /// <see cref="accountIdentityLock"/> gate every other reader/writer of the pair uses). Shared by
        /// <see cref="SetAccountIdAsync"/> (the ordinary quota-refresh path) and
        /// <see cref="EnterBlockAsync"/>'s latch-time lookup, so BOTH use IDENTICAL staleness-rejection
        /// semantics instead of the old unconditional <see cref="SetAccountId"/> call, which could
        /// repopulate a stale account after a NEWER context (e.g. a key/endpoint change) had already
        /// superseded it.
        /// </summary>
        private bool TryApplyAccountIdForGeneration(string newAccountId, int expectedGeneration)
        {
            lock (accountIdentityLock)
            {
                if (contextGeneration != expectedGeneration)
                {
                    return false;
                }

                accountId = string.IsNullOrWhiteSpace(newAccountId) ? null : newAccountId;
                return true;
            }
        }

        /// <summary>
        /// D-verdict fix (P1 #1 / item 4): re-validates a locally-held accountId candidate against the
        /// CURRENT <see cref="contextGeneration"/> right before it is used to build a persisted block
        /// identity. If nothing has changed the context since <paramref name="capturedGeneration"/> was
        /// captured, the candidate (a cached value, or a lookup result already applied via
        /// <see cref="TryApplyAccountIdForGeneration"/>) is still current and is returned unchanged. If
        /// the generation MOVED (a concurrent <see cref="SetAccountId"/>/<see cref="SetAccountIdAsync"/>/
        /// <see cref="UpdateContext"/> ran while the caller was awaiting), the candidate is discarded in
        /// favor of a fresh read of the CURRENT cached accountId (which may be null) -- callers must
        /// never let a value computed against a superseded context reach disk.
        /// Also returns the generation OBSERVED at the instant of this decision (pass F): that value --
        /// not a later re-read of <see cref="ContextGeneration"/> -- is the ONLY correct self-heal
        /// baseline, because any movement AFTER this observation (including during the identity's
        /// key read or the repository commit) must register as a change against it.
        /// </summary>
        private (string AccountId, int ObservedGeneration) ReconcileAccountIdForCurrentGeneration(string candidateAccountId, int capturedGeneration)
        {
            lock (accountIdentityLock)
            {
                return contextGeneration == capturedGeneration
                    ? (candidateAccountId, contextGeneration)
                    : (accountId, contextGeneration);
            }
        }

        /// <summary>
        /// Storage-quota plan directive E (B#3): reconciles a currently-live block against a
        /// newly-arrived, CURRENT-generation accountId. Must be called only while holding
        /// <see cref="lifecycleLock"/> (both callers, <see cref="SetAccountIdAsync"/> and the Layer-2
        /// self-heal <see cref="SelfHealBlockIdentityAfterCommitAsync"/>, already do).
        /// <paramref name="capturedGeneration"/> is the generation <paramref name="newAccountId"/> is
        /// known-current as of (hardening round F, Layer 1a): re-checked against
        /// <see cref="ContextGeneration"/> after this method's own fingerprint-compute await, aborting
        /// the WHOLE reconciliation (no clear, no upgrade) if it moved, instead of committing an effect
        /// built from a now-stale fingerprint. A block whose persisted identity has no known accountId,
        /// or the SAME accountId, is left untouched -- there is nothing foreign to prove. A block scoped
        /// to a KNOWN, DIFFERENT account is durably cleared, honoring the same fail-safe discipline as
        /// every other <see cref="Astrovault.Interfaces.IUploadQueueRepository.ClearBlockAsync"/> caller
        /// (directive C): the in-memory latch is released only after the commit succeeds, and both notification
        /// events fire either way (directive H).
        /// </summary>
        private async Task ReconcileLiveBlockForAccountChangeLockedAsync(string newAccountId, int capturedGeneration)
        {
            if (BlockKind == UploadBlockKind.None)
            {
                return;
            }

            var persistedIdentity = queueRepository.CurrentBlock?.Identity;
            if (persistedIdentity == null)
            {
                return; // Nothing persisted to compare against -- do not guess.
            }

            // Storage-quota plan directive E (C-P1#3): reuse the SAME matcher RestoreBlockStateAsync's
            // startup reconciliation uses -- instead of a separate accountId-only comparison -- so the
            // live path follows the IDENTICAL endpoint-aware rule (a matching accountId STRING on a
            // DIFFERENT endpoint is not the same identity: the same account could plausibly exist against
            // a different custom backend) and the identical accountless fingerprint+endpoint rule below.
            var apiKey = authManager.GetApiKey();
            string currentFingerprint = null;
            if (!string.IsNullOrEmpty(apiKey))
            {
                currentFingerprint = await queueRepository.ComputeIdentityFingerprintAsync(apiKey).ConfigureAwait(false);
            }

            // Race hardening (Layer 1a): the fingerprint compute above is the
            // ONLY await before this reconciliation commits an effect (clear or upgrade), and it captured
            // `apiKey` BEFORE awaiting -- so a concurrent identity change (e.g. a re-auth/reinstall
            // calling the plain, non-locking SetAccountId, which only needs accountIdentityLock and is
            // never blocked by lifecycleLock) can move contextGeneration while this call is suspended
            // without disturbing the already-captured, now-STALE apiKey/fingerprint pair. Trusting them
            // here could clear a block that is genuinely owned by the NEW identity (pass E scenario 1: a
            // reinstalled account's own genuine block cleared by a stale fingerprint from the OLD one).
            // Abort the ENTIRE reconciliation -- no clear, no upgrade -- and let the reconciliation that
            // accompanies the newer identity change (already queued or about to run) repair the block
            // against CURRENT data instead.
            if (ContextGeneration != capturedGeneration)
            {
                Logger.Info("[UploadManager] Live block reconciliation aborted -- context changed while awaiting the identity fingerprint; a fresh reconciliation will run against current data");
                return;
            }

            var currentEndpoint = UploadBlockIdentityMatcher.CanonicalizeEndpoint(apiClient.BaseUrl);

            var matchesCurrentIdentity = UploadBlockIdentityMatcher.MatchesCurrent(
                persistedIdentity, currentEndpoint, newAccountId, currentFingerprint);

            if (string.IsNullOrEmpty(persistedIdentity.AccountId))
            {
                // Storage-quota plan directive E (C-P1#4a): an ACCOUNTLESS block (entered before the
                // account was known) is UPGRADED -- never cleared -- once a matching fingerprint+endpoint
                // PROVES it is the same install/key that entered it, durably rewriting the identity to
                // carry the accountId so a LATER key rotation on this SAME account retains it via the
                // AccountId branch below.
                if (matchesCurrentIdentity)
                {
                    // Require currentFingerprint to be present so an upgrade only fires on a genuine
                    // proof, never on MatchesCurrent's "current identity unavailable" fail-safe retain
                    // (which also reports matchesCurrentIdentity == true).
                    if (!string.IsNullOrEmpty(currentFingerprint))
                    {
                        await UpgradeLiveBlockWithAccountIdLockedAsync(newAccountId).ConfigureAwait(false);
                    }
                    return;
                }

                // D-verdict fix (P1 #2): matchesCurrentIdentity is false here ONLY when currentFingerprint
                // was present (MatchesCurrent's own "missing current identity" fail-safe already returned
                // true otherwise, handled by the branch above) -- i.e. a fingerprint/endpoint MISMATCH
                // PROVEN against a known current identity. Honor the matcher's verdict exactly as
                // RestoreBlockStateAsync's startup reconciliation already does instead of retaining a
                // proven-foreign accountless block forever: fall through to the SAME durable-clear path
                // used below for a known-foreign accountId block.
            }
            else if (matchesCurrentIdentity)
            {
                return; // Same identity (account + endpoint) -- keep the block as-is.
            }

            var pinJobId = queueRepository.CurrentBlock?.FailedJobId;
            var committed = await queueRepository.ClearBlockAsync(pinJobId).ConfigureAwait(false);

            if (!committed)
            {
                var stillLatchedKind = BlockKind;
                var stillLatchedSnapshot = BlockSnapshot;
                SetInMemoryBlock(stillLatchedKind, stillLatchedSnapshot, persistFailed: true);
                Logger.Error("[UploadManager] Account-change reconciliation wanted to clear a foreign block but the commit FAILED to persist durably -- staying latched");
                OnQueueStateChanged();
                OnUploadBlocked(stillLatchedKind, stillLatchedSnapshot, persistFailed: true);
                return;
            }

            SetInMemoryBlock(UploadBlockKind.None, null);
            Logger.Info("[UploadManager] Live block cleared -- the account switched to a different, now-valid identity");
            OnQueueStateChanged();
            SignalWake();
        }

        /// <summary>
        /// Storage-quota plan directive E (C-P1#4a): durably rewrites the currently-latched block's
        /// persisted identity to carry <paramref name="accountId"/>, via
        /// <see cref="Astrovault.Interfaces.IUploadQueueRepository.UpgradeBlockAccountIdAsync"/> (one
        /// semaphore-held commit). Purely a repository-side identity-metadata change -- the manager's own
        /// in-memory Kind/Snapshot are untouched and no UI event is raised (there is no observable
        /// transition: the block stays latched on the same kind with the same numbers).
        /// </summary>
        private async Task UpgradeLiveBlockWithAccountIdLockedAsync(string accountId)
        {
            var committed = await queueRepository.UpgradeBlockAccountIdAsync(accountId).ConfigureAwait(false);
            if (committed)
            {
                Logger.Info("[UploadManager] Live block upgraded to account-scoped identity -- a later key rotation on this account will retain it");
            }
            else
            {
                Logger.Warning("[UploadManager] Live block accountId upgrade could not be persisted durably -- will retry on a later reconciliation");
            }
        }

        /// <inheritdoc />
        public async Task InitializeCountsAsync()
        {
            await RecomputeCountsFromRepoAsync().ConfigureAwait(false);
            OnQueueStateChanged(); // Notify UI to refresh counts immediately after startup seed
        }

        /// <summary>
        /// SEC-03: Recomputes the queue counters from the authoritative repository
        /// state instead of trusting drifting per-field Interlocked deltas (which can
        /// go negative or stale when UI actions and the worker loop race). Does NOT
        /// raise QueueStateChanged -- callers decide whether to notify the UI.
        /// </summary>
        private async Task RecomputeCountsFromRepoAsync()
        {
            Interlocked.Exchange(ref pendingCount, await queueRepository.GetJobCountAsync(UploadStatus.Pending).ConfigureAwait(false));
            // Completed counter is the repo's LIFETIME running total, NOT GetJobCountAsync(Completed):
            // the latter counts only RETAINED completed records (capped at MaxCompletedJobsRetained),
            // so sourcing the counter from it clamped the display to the cap and made it oscillate
            // (live +1 to cap+1, then recompute back down to the cap) instead of climbing. CompletedTotal
            // is decoupled from the retention cap and monotonic.
            Interlocked.Exchange(ref completedCount, queueRepository.CompletedTotal);
            Interlocked.Exchange(ref failedCount, await queueRepository.GetJobCountAsync(UploadStatus.Failed).ConfigureAwait(false));
            // Derive uploadingCount from the repo's InProgress count -- the SAME authoritative-source
            // pattern as the other three counters. Hard-zeroing it here (the old behavior) was only
            // correct at load time; at RUNTIME this method also runs on every EnqueueJobAsync (each new
            // captured frame) and on the worker-loop probe seam, so zeroing clobbered the live count of
            // an actively-uploading job -- the UI "Uploading" counter reset to 0 and the in-flight job
            // vanished from every bucket until it finished. GetJobCountAsync(InProgress) is 0 at load
            // (LoadQueueFromDisk resets InProgress -> Pending) and accurate while uploads are in flight.
            Interlocked.Exchange(ref uploadingCount, await queueRepository.GetJobCountAsync(UploadStatus.InProgress).ConfigureAwait(false));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsRunning)
                {
                    Logger.Warning("[UploadManager] Already running");
                    return;
                }

                cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Storage-quota plan (C-P1#1 fix): defensive same-process reset -- mirrors what
                // LoadQueueFromDisk already does on a FRESH load (reset InProgress -> Pending). A job can
                // be left InProgress within the LIVE repository instance (e.g. a previous processing
                // loop's clean-cancellation path did not run) -- an endpoint reinit or Disconnect ->
                // Reconnect cycle that restarts the SAME repository instance would otherwise leave that
                // job permanently invisible to PeekAsync (which only ever selects Pending jobs) until a
                // full process restart forces a fresh disk load. A no-op when nothing is InProgress.
                await queueRepository.ResetAllInProgressToPendingAsync().ConfigureAwait(false);
                await InitializeCountsAsync().ConfigureAwait(false);
                // Storage-quota plan §4 Step 2, point 2: restore any persisted block BEFORE the drain
                // loop starts, so a block latched in a previous session survives this NINA restart --
                // the very first ProcessNextJobAsync tick already honors it (zero HTTP attempts).
                await RestoreBlockStateAsync().ConfigureAwait(false);
                processingTask = ProcessQueueAsync(cts.Token);
                Logger.Info("[UploadManager] Started");
            }
            finally
            {
                lifecycleLock.Release();
            }
        }

        public async Task StopAsync()
        {
            // Storage-quota plan directive B (the single transition gate, A#2): hold lifecycleLock for
            // the ENTIRE stop sequence -- including awaiting the captured processing task to actual
            // completion -- not just the cancel. The old code released the lock right after Cancel(),
            // so a concurrent StartAsync could see IsRunning still true (the old task hadn't finished
            // yet) and return as a no-op "already running", or the old task could finish and a NEW one
            // get installed before this method ever read processingTask, making StopAsync await the
            // REPLACEMENT worker instead of the one it meant to stop. Holding the lock the whole way
            // through eliminates both: no other lifecycle operation (StartAsync, ResumeAsync,
            // SetAccountIdAsync, UpdateContext -- all gated on the SAME lock) can run until this stop has
            // truly finished. Safe against the drain loop itself: ProcessQueueAsync and everything it
            // calls (including EnterBlockAsync's in-memory mutation, guarded by the separate
            // blockStateLock) never takes lifecycleLock, so awaiting the captured task from inside this
            // lock cannot deadlock.
            await lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsRunning)
                {
                    return;
                }

                var capturedTask = processingTask;
                cts?.Cancel();

                try
                {
                    await capturedTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping
                }

                Logger.Info("[UploadManager] Stopped");
            }
            finally
            {
                lifecycleLock.Release();
            }
        }

        public async Task EnqueueJobAsync(UploadJob job)
        {
            await queueRepository.EnqueueAsync(job).ConfigureAwait(false);
            // WR-01 / SEC-03: recompute counters from the authoritative repo rather than trusting a
            // per-field Interlocked.Increment. A concurrent RecomputeCountsFromRepoAsync (worker loop /
            // UI action) uses Interlocked.Exchange, which clobbers a racing increment -- so the delta
            // could be lost (under-count) or double-counted. This mirrors the pattern already used by
            // RetryFailedJobAsync / DismissAllFailedAsync.
            await RecomputeCountsFromRepoAsync().ConfigureAwait(false);
            await UpdateCapacityWarning().ConfigureAwait(false);
            OnQueueStateChanged();
        }

        /// <inheritdoc />
        public async Task RetryFailedJobAsync(Guid jobId)
        {
            await queueRepository.RetryJobAsync(jobId).ConfigureAwait(false);
            // SEC-03: recompute from the repo after the UI action rather than trusting
            // per-field deltas that can drift negative/stale across worker+UI threads.
            await RecomputeCountsFromRepoAsync().ConfigureAwait(false);
            Logger.Info($"[UploadManager] Retrying failed job: {jobId}");
            OnQueueStateChanged();
        }

        /// <inheritdoc />
        public async Task DismissAllFailedAsync()
        {
            await queueRepository.RemoveFailedJobsAsync().ConfigureAwait(false);
            // SEC-03: recompute from the repo after the UI action.
            await RecomputeCountsFromRepoAsync().ConfigureAwait(false);
            Logger.Info("[UploadManager] Dismissed all failed jobs");
            OnQueueStateChanged();
        }

        /// <inheritdoc />
        public async Task ResetCountsAsync()
        {
            await queueRepository.ResetCountsAsync().ConfigureAwait(false);
            // Recompute so the displayed Total uploaded + Failed reflect the now-cleared history.
            await RecomputeCountsFromRepoAsync().ConfigureAwait(false);
            Logger.Info("[UploadManager] Reset counts (Total uploaded + Failed)");
            OnQueueStateChanged();
        }

        /// <inheritdoc />
        public async Task RetryAllFailedAsync()
        {
            var failedJobs = (await queueRepository.GetJobsByStatusAsync(UploadStatus.Failed).ConfigureAwait(false)).ToList();
            Logger.Info($"[UploadManager] Retrying {failedJobs.Count} failed jobs");
            foreach (var job in failedJobs)
            {
                await RetryFailedJobAsync(job.Id).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Main processing loop - picks jobs from queue and uploads them.
        /// </summary>
        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            Logger.Info("[UploadManager] ProcessQueueAsync loop starting");
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var processedJob = await ProcessNextJobAsync(cancellationToken).ConfigureAwait(false);
                    // PERF-02: only idle when there was no work this iteration. When a job
                    // was processed, loop immediately so the queue drains faster than ~1/s.
                    if (!processedJob)
                    {
                        // Storage-quota plan §4 Step 5: wait up to 1s as before, but ResumeAsync can
                        // release wakeSignal to cut that short -- the pinned resume job is attempted
                        // right away instead of waiting out the idle poll interval.
                        await wakeSignal.WaitAsync(1000, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error($"[UploadManager] Processing error: {ex.Message}");
                    await Task.Delay(5000, cancellationToken).ConfigureAwait(false); // Wait before retry
                }
            }
        }

        /// <summary>
        /// Processes a single job from the queue.
        /// </summary>
        /// <returns>True if a job was processed (caller should loop immediately);
        /// false if no work was available (caller should idle before re-checking).</returns>
        internal async Task<bool> ProcessNextJobAsync(CancellationToken cancellationToken)
        {
            // Storage-quota plan §4 Step 2, point 1: while latched, attempt NOTHING -- zero HTTP
            // requests, zero dequeuing. The processing task stays alive (IsRunning remains true); this
            // method just reports "no work done" each tick so ProcessQueueAsync idles instead of
            // spinning. Checked before EVERYTHING else (auth, breaker, peek) so a block truly means zero
            // attempts, not "zero successful attempts". Reads through the BlockKind property (directive
            // B: blockStateLock-guarded) rather than the raw field so this hot-path read is never torn
            // against a concurrent SetInMemoryBlock from ResumeAsync/RestoreBlockStateAsync/reconciliation.
            if (BlockKind != UploadBlockKind.None)
            {
                return false;
            }

            // Check if API key is configured before processing uploads
            var apiKey = authManager.GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                Logger.Info("[UploadManager] Skipping - not authenticated");
                return false;
            }

            // Circuit breaker check -- skip upload when API is unreachable. ShouldAttempt() transitions
            // Open -> HalfOpen (returns true once) when the recovery timeout has elapsed, granting a probe.
            var wasOpenBeforeProbe = circuitBreaker.IsOpen;
            if (!circuitBreaker.ShouldAttempt())
            {
                return false;
            }

            var job = await queueRepository.PeekAsync().ConfigureAwait(false);

            // REL-06 half-open probe promotion: this is the TC-16 dead-end fix. When the breaker was Open
            // and has just permitted a probe (HalfOpen), but PeekAsync returns null because every job is
            // Failed, there is nothing to probe with and the breaker dead-ends in HalfOpen forever. Promote
            // the TRANSIENT-Failed jobs (permanent failures excluded) back to Pending so the probe has work
            // to send; their NextRetryAfter is cleared (immediately due) and LastFailureReason preserved.
            if (job == null && wasOpenBeforeProbe)
            {
                var promoted = await queueRepository.PromoteTransientFailedToPendingAsync().ConfigureAwait(false);
                if (promoted > 0)
                {
                    await RecomputeCountsFromRepoAsync().ConfigureAwait(false);
                    OnQueueStateChanged();
                    job = await queueRepository.PeekAsync().ConfigureAwait(false);
                }
            }

            if (job == null)
            {
                return false; // Queue empty (or only not-yet-due / permanent-failed jobs) -- idle before re-checking
            }

            Logger.Info($"[UploadManager] Processing job: {job.Id}");
            await UploadJobAsync(job, cancellationToken).ConfigureAwait(false);
            return true; // work done -- caller loops immediately (no idle delay)
        }

        /// <summary>
        /// Uploads a single job with progress reporting.
        /// </summary>
        internal async Task UploadJobAsync(UploadJob job, CancellationToken cancellationToken)
        {
            await queueRepository.UpdateJobStatusAsync(job.Id, UploadStatus.InProgress).ConfigureAwait(false);
            Interlocked.Increment(ref uploadingCount);
            Logger.Info($"[UploadManager] Uploading: {LogSanitizer.MaskPath(job.LocalPath)}");

            var progress = new Progress<double>(p => OnProgressChanged(job, p));

            UploadResult result;
            try
            {
                result = await apiClient.UploadFileAsync(
                    job,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // REL-04: Clean cancellation/shutdown is NOT a transient failure.
                // Do not increment the retry count or feed the circuit breaker --
                // just unwind the in-flight counter so it doesn't drift.
                Interlocked.Decrement(ref uploadingCount);
                // Storage-quota plan (C-P1#1 fix): durably reset THIS job InProgress -> Pending NOW, in
                // the SAME repository instance/process -- do not rely solely on a future disk reload
                // (LoadQueueFromDisk) or the defensive StartAsync-time bulk reset above. A same-process
                // restart (endpoint reinit, Disconnect -> Reconnect) that never tears down this repository
                // instance would otherwise leave the job stuck InProgress and invisible to PeekAsync until
                // a full process restart.
                await queueRepository.ResetJobToPendingIfInProgressAsync(job.Id).ConfigureAwait(false);
                Logger.Info($"[UploadManager] Upload cancelled (clean shutdown): {LogSanitizer.MaskPath(job.LocalPath)}");
                throw;
            }
            catch (Exception ex)
            {
                // Only the API-CALL boundary is caught here -- an exception from the call itself
                // (network/serialization/etc, not from downstream failure handling below) is a genuine
                // unclassified transient failure.
                await HandleUploadFailure(job, UploadResult.Fail(ex.Message), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (result.Success)
            {
                await HandleUploadSuccess(job).ConfigureAwait(false);
                return;
            }

            // Storage-quota plan directive G (enter-block exception boundary): HandleUploadFailure --
            // and the latch path it may enter via EnterBlockAsync (identity construction, SetBlockAsync,
            // the authoritative recount) -- is DELIBERATELY OUTSIDE the try/catch above. An exception
            // thrown from in here must never re-enter HandleUploadFailure as a FABRICATED Transient
            // result (the old broad catch did exactly that): that would lose the original 401/403/507
            // classification and feed the breaker / schedule a retry -- precisely what the latch exists
            // to prevent. Fail safe instead: for a quota/auth result, stay latched in memory with the
            // ORIGINAL kind and surface the persist-failure warning; for any other kind, just log and
            // unwind the in-flight counter so it cannot drift.
            try
            {
                await HandleUploadFailure(job, result, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error($"[UploadManager] HandleUploadFailure threw while processing a {result.FailureKind} result -- failing safe: {ex.Message}");
                Interlocked.Decrement(ref uploadingCount);

                if (result.FailureKind == FailureKind.PermanentQuota || result.FailureKind == FailureKind.PermanentAuth)
                {
                    var kind = result.FailureKind == FailureKind.PermanentQuota
                        ? UploadBlockKind.QuotaExceeded
                        : UploadBlockKind.AuthInvalid;
                    var snapshot = new UploadBlockSnapshot
                    {
                        UsedBytes = result.QuotaInfo?.UsedBytes,
                        LimitBytes = result.QuotaInfo?.LimitBytes,
                        Flavor = result.QuotaInfo?.Flavor ?? QuotaBlockFlavor.StorageLimitExceeded
                    };
                    SetInMemoryBlock(kind, snapshot, persistFailed: true);
                    OnQueueStateChanged();
                    OnUploadBlocked(kind, snapshot, persistFailed: true);
                }
            }
        }

        internal async Task HandleUploadSuccess(UploadJob job)
        {
            var wasOpen = circuitBreaker.IsOpen;
            circuitBreaker.RecordSuccess();

            if (wasOpen)
            {
                await queueRepository.ResetRetryCountsForTransientAsync().ConfigureAwait(false);
                Logger.Info("[UploadManager] Uploads resumed -- API connection restored");
            }

            // Phase 7: Retain completed job (CompletedAt set by repository). This also bumps the repo's
            // lifetime CompletedTotal exactly once for this job.
            await queueRepository.UpdateJobStatusAsync(job.Id, UploadStatus.Completed).ConfigureAwait(false);
            Interlocked.Decrement(ref uploadingCount);
            Interlocked.Decrement(ref pendingCount);
            // Source the displayed count from the repo's authoritative running total (read AFTER the
            // status update bumped it). Using Exchange rather than a local +1 keeps the live counter and
            // the repo total as a SINGLE source of truth -- no increment-vs-recompute double-count race.
            Interlocked.Exchange(ref completedCount, queueRepository.CompletedTotal);

            await UpdateCapacityWarning().ConfigureAwait(false);

            OnQueueStateChanged();
            Logger.Info($"[UploadManager] Upload completed: {LogSanitizer.MaskPath(job.LocalPath)}");
            OnUploadCompleted(job, true, null);
        }

        internal async Task HandleUploadFailure(
            UploadJob job,
            UploadResult result,
            CancellationToken cancellationToken)
        {
            // Storage-quota plan §4 Step 2: a 507 (quota) or 401/403 (auth) latches the WHOLE queue
            // instead of following either the permanent-skip-file or transient-retry paths below.
            // Checked FIRST and directly on FailureKind (not the derived IsPermanent flag) because
            // IsPermanent deliberately stays false for these two kinds (see UploadResult.IsPermanent) --
            // this explicit check is what makes the block-latch branch happen at all.
            if (result.FailureKind == FailureKind.PermanentQuota || result.FailureKind == FailureKind.PermanentAuth)
            {
                await EnterBlockAsync(job, result).ConfigureAwait(false);
                return;
            }

            // Permanent failures skip retries entirely (e.g., file not found, access denied, server
            // 400/413). REL-06/D-12: mark the terminal failure as PERMANENT (excluded from
            // auto-promotion -- never retried forever) and persist LastFailureReason for the existing
            // notification path.
            if (result.IsPermanent)
            {
                // Storage-quota plan directive F: a SERVER-originated permanent-skip response (400/413,
                // StatusCode set by FailureClassifier) still proves connectivity -- the request
                // round-tripped. Reset the breaker's consecutive-failure count / close a Half-Open probe
                // BEFORE marking the file failed, exactly like the quota/auth block-latch path already
                // does (EnterBlockAsync) -- otherwise a 400/413 landing on the Half-Open probe leaves the
                // breaker stuck Half-Open forever (ShouldAttempt keeps granting probes, IsOpen never
                // clears). LOCAL-origin permanent failures (file missing/access-denied, the HTTPS
                // send-boundary gate, exhausted session restarts -- StatusCode 0, never reached the
                // server) stay neutral: they prove nothing about the server's reachability.
                if (result.StatusCode == 400 || result.StatusCode == 413)
                {
                    circuitBreaker.RecordConnectivityProof();
                }

                await queueRepository.MarkFailedAsync(job.Id, result.ErrorMessage, isPermanent: true).ConfigureAwait(false);
                Interlocked.Decrement(ref uploadingCount);
                Interlocked.Decrement(ref pendingCount);
                Interlocked.Increment(ref failedCount);
                await UpdateCapacityWarning().ConfigureAwait(false);
                Logger.Error($"[UploadManager] Permanent failure (no retry): {LogSanitizer.MaskPath(job.LocalPath)} - {result.ErrorMessage}");
                // D-12: surface the persisted last-failure reason via the EXISTING notification path
                // (no new UI surface). LastFailureReason == result.ErrorMessage here; prefer the
                // persisted field so the notification reflects exactly what was stored as history.
                OnUploadCompleted(job, false, job.LastFailureReason ?? result.ErrorMessage, result.StatusCode, result.FailureKind);
                return;
            }

            var breakerWasOpen = circuitBreaker.IsOpen;

            // Transient failures count toward the circuit breaker.
            circuitBreaker.RecordFailure();

            if (circuitBreaker.IsOpen && !breakerWasOpen)
            {
                Logger.Warning("[UploadManager] Uploads paused -- API unreachable after 5 failures");
                OnQueueStateChanged();
            }

            // Retry logic with jittered exponential backoff.
            // REL-01: IncrementRetryCountAsync mutates the same job object PeekAsync
            // returned (production aliasing), so job.RetryCount already reflects the
            // post-increment value. Read it once -- adding +1 here double-counts and
            // fails the job after 4 attempts instead of the intended 5.
            await queueRepository.IncrementRetryCountAsync(job.Id).ConfigureAwait(false);
            var retryCount = job.RetryCount;

            if (retryCount >= MaxRetries)
            {
                // REL-06/D-12: terminal TRANSIENT failure (retry budget exhausted). Marked NOT permanent
                // so it is auto-promotable on a half-open probe; LastFailureReason persisted (D-12).
                await queueRepository.MarkFailedAsync(job.Id, result.ErrorMessage, isPermanent: false).ConfigureAwait(false);
                Interlocked.Decrement(ref uploadingCount);
                Interlocked.Decrement(ref pendingCount);
                Interlocked.Increment(ref failedCount);
                await UpdateCapacityWarning().ConfigureAwait(false);
                Logger.Error($"[UploadManager] Upload failed permanently: {LogSanitizer.MaskPath(job.LocalPath)}");
                // D-12: surface the persisted last-failure reason via the EXISTING notification path.
                OnUploadCompleted(job, false, job.LastFailureReason ?? result.ErrorMessage, result.StatusCode, result.FailureKind);
            }
            else
            {
                // REL-07: non-blocking backoff. Instead of blocking the single-threaded drain loop with
                // Task.Delay, persist a NextRetryAfter timestamp; the not-yet-due PeekAsync filter skips
                // the job until it is due, so other jobs keep draining and the loop never freezes.
                Interlocked.Decrement(ref uploadingCount);
                var delaySeconds = CalculateBackoffDelay(retryCount);
                var retryAt = DateTime.UtcNow.AddSeconds(delaySeconds);
                await queueRepository.ScheduleRetryAsync(job.Id, retryAt, result.ErrorMessage).ConfigureAwait(false);
                Logger.Warning($"[UploadManager] Retry {retryCount}/{MaxRetries} after {delaySeconds:F1}s (non-blocking): {LogSanitizer.MaskPath(job.LocalPath)}");
            }
        }

        // ====================================================================
        // Blocked-state machine (storage-quota plan §4 Step 2)
        // ====================================================================

        /// <summary>
        /// Latches the queue into a blocked state on a 507 (quota) or 401/403 (auth) failure from ANY
        /// upload path (single-shot, initiate, chunk, complete, or resume-status -- Step 1 threads the
        /// typed result through every one of them, so this single choke point covers all of them). The
        /// failing job returns to Pending (never Failed, nothing discarded); no retry is scheduled and
        /// the circuit breaker is NOT fed a failure (the caller already returned before reaching that
        /// code). The HTTP response itself proves connectivity, so the breaker's consecutive-failure
        /// count is reset (and a Half-Open probe closed) BEFORE latching -- an upload that could not
        /// complete because storage is full/the key is dead is not the same signal as "the API is
        /// unreachable".
        /// </summary>
        private async Task EnterBlockAsync(UploadJob job, UploadResult result)
        {
            var kind = result.FailureKind == FailureKind.PermanentQuota
                ? UploadBlockKind.QuotaExceeded
                : UploadBlockKind.AuthInvalid;

            // Plan point 5: the response proves connectivity -- reset the breaker BEFORE latching.
            circuitBreaker.RecordConnectivityProof();

            var snapshot = new UploadBlockSnapshot
            {
                UsedBytes = result.QuotaInfo?.UsedBytes,
                LimitBytes = result.QuotaInfo?.LimitBytes,
                // Contract-delta round: carries the flavor (StorageLimitExceeded/PlanRequired) parsed
                // from the flat 507 body's `error` field straight through into the latched/persisted
                // state, so the correct banner survives a NINA restart. Meaningless-but-harmless default
                // for an AuthInvalid latch (result.QuotaInfo is always null there -- never a 507).
                Flavor = result.QuotaInfo?.Flavor ?? QuotaBlockFlavor.StorageLimitExceeded
            };

            // Storage-quota plan directive E (A#6): at latch time, for a QUOTA block only (the key is
            // still valid here -- this branch is never reached for AuthInvalid, where the key is dead
            // and a lookup would just fail with another 401/403), if no accountId is cached yet attempt
            // ONE bounded GetQuotaAsync to acquire it BEFORE persisting -- upgrading the block's scoping
            // from fingerprint to account immediately rather than waiting for some later incidental
            // refresh. Bounded entirely by GetQuotaAsync's own short per-call timeout (it never throws);
            // a defensive try/catch keeps any unexpected fault from ever escaping into the broader
            // latch path (directive G's exception-boundary discipline applies here too). Failure simply
            // means persisting with a null accountId as before -- never delays the latch beyond that one
            // bounded call.
            // Storage-quota plan directive E (C-P1#4b): a generation-consistent snapshot, read under the
            // SAME gate SetAccountId/SetAccountIdAsync write it under, so a concurrent account-identity
            // transition (e.g. a background quota refresh landing mid-latch) can never hand this a
            // half-written value. D-verdict fix (P1 #1): the generation is captured ALONGSIDE the value
            // so the bounded lookup below (which awaits, and can therefore straddle a concurrent context
            // change) can be re-validated against it once it returns.
            var (cachedAccountId, capturedGeneration) = GetCachedAccountIdWithGeneration();
            var effectiveAccountId = cachedAccountId;
            if (kind == UploadBlockKind.QuotaExceeded && string.IsNullOrEmpty(effectiveAccountId))
            {
                try
                {
                    var usage = await apiClient.GetQuotaAsync(CancellationToken.None).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(usage?.AccountId))
                    {
                        // D-verdict fix (P1 #1): apply through the SAME generation-checked path
                        // SetAccountIdAsync uses -- never an unconditional SetAccountId. A stale result
                        // (the context moved on, e.g. a key replacement, while this lookup was in flight)
                        // must be rejected here, not silently repopulate a superseded account.
                        if (TryApplyAccountIdForGeneration(usage.AccountId, capturedGeneration))
                        {
                            effectiveAccountId = usage.AccountId; // cache for future latches/comparisons this session
                        }
                        else
                        {
                            Logger.Info("[UploadManager] Dropped stale latch-time accountId lookup -- context changed while the lookup was in flight");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[UploadManager] Latch-time accountId lookup failed (non-fatal, persisting without it): {ex.Message}");
                }
            }

            // D-verdict fix (P1 #1 / item 4): re-validate right before the identity is built for
            // persistence. Covers BOTH outcomes above -- a rejected stale lookup (effectiveAccountId is
            // still whatever was cached at entry) and the narrower case where the generation moved AGAIN
            // in the window after a successful apply -- by discarding the candidate and using the CURRENT
            // cached value whenever the generation no longer matches what was captured at entry. Never
            // lets a value computed against a superseded context reach the persisted block.
            // Pass F fix: the self-heal baseline is the generation OBSERVED by the reconcile decision
            // itself -- never a later re-read. A bump landing anywhere after this point (the GetApiKey
            // read below, or during the SetBlockAsync commit) therefore always registers as movement
            // against this baseline and triggers the post-commit self-heal, keeping the "at most one
            // reconciliation cycle" repair bound honest for the ENTIRE identity-build-and-commit span.
            var (reconciledAccountId, preCommitGeneration) = ReconcileAccountIdForCurrentGeneration(effectiveAccountId, capturedGeneration);
            effectiveAccountId = reconciledAccountId;

            var identity = new UploadBlockIdentityInput
            {
                EndpointUrl = UploadBlockIdentityMatcher.CanonicalizeEndpoint(apiClient.BaseUrl),
                AccountId = effectiveAccountId, // Step 6 / directive E: SetAccountId(Async), or the bounded lookup above
                ApiKey = authManager.GetApiKey()
            };

            var committed = await queueRepository.SetBlockAsync(kind, job.Id, snapshot, identity).ConfigureAwait(false);

            // Authoritative recompute (SEC-03 pattern): the job moved InProgress -> Pending inside the
            // SAME commit SetBlockAsync just made, so trust the repo rather than hand-adjusting counters.
            await RecomputeCountsFromRepoAsync().ConfigureAwait(false);

            SetInMemoryBlock(kind, snapshot, persistFailed: !committed);

            // Storage-quota plan directive H (visibility on failure paths, B#2): BOTH events fire on
            // EVERY live transition -- committed or not -- through the SAME dispatcher path, after the
            // in-memory transition has already been applied above. A failed commit used to raise
            // NEITHER event, which left the UI's IsUploading flag stuck and the block banner/warning
            // invisible until some unrelated later queue event happened to refresh it. IsPersistFailure
            // on the typed event lets a subscriber tell "durably committed" from "in-memory only,
            // surface the persist-failure warning" apart -- never claiming a durability that did not
            // happen while still making the transition visible either way.
            if (committed)
            {
                Logger.Warning($"[UploadManager] Upload blocked ({kind}) -- queue stopped, job returned to Pending");
            }
            else
            {
                Logger.Error($"[UploadManager] Upload blocked ({kind}) but FAILED to persist durably -- stopped in-memory only");
            }
            OnQueueStateChanged();
            // Storage-quota plan §4 Step 3: the dedicated typed event, ONLY from a LIVE transition here
            // (never from RestoreBlockStateAsync) -- so a UI subscriber sees exactly one event per real
            // transition and nothing on a startup replay.
            OnUploadBlocked(kind, snapshot, persistFailed: !committed);

            // Hardening round F (Layer 2): the just-persisted block may have been built from an identity
            // that stopped being current anywhere after the reconcile decision above (preCommitGeneration
            // is the generation that decision OBSERVED, so movement during the key read or the commit
            // both register here). A failed commit has nothing to self-heal -- the repository
            // was never actually written with this identity, so there is no "just-persisted block" to
            // reconcile; the existing persist-failure retry path already covers that case. Only schedule
            // the self-heal when the commit actually landed.
            if (committed && ContextGeneration != preCommitGeneration)
            {
                Logger.Info("[UploadManager] Context changed while the block was being persisted -- scheduling a self-heal reconciliation against the just-persisted identity");
                // Deliberately NOT awaited -- see SelfHealBlockIdentityAfterCommitAsync's doc for why
                // EnterBlockAsync (which runs inside the drain loop task StopAsync captures and awaits
                // WHILE HOLDING lifecycleLock) must never itself await lifecycleLock.
                LastSelfHealTaskForTests = SelfHealBlockIdentityAfterCommitAsync();
            }
        }

        // Test seam only (InternalsVisibleTo CloudUploadPlugin.Tests): SelfHealBlockIdentityAfterCommitAsync
        // below is deliberately fire-and-forget from EnterBlockAsync's perspective, so a deterministic
        // test needs a handle to await its completion instead of sleeping/polling. Production code never
        // reads this field -- it exists purely so tests can observe when the detached self-heal has
        // finished.
        internal Task LastSelfHealTaskForTests { get; private set; }

        /// <summary>
        /// Race hardening (Layer 2, review residue): the irreducible window Layer 1's
        /// pre-commit re-validation cannot close -- a context change landing DURING the repository commit
        /// itself -- cannot be closed by holding accountIdentityLock across an await (forbidden by the
        /// established lock design). Handled by SELF-HEALING instead: runs the SAME generation-safe live
        /// reconciliation <see cref="SetAccountIdAsync"/> uses, against whatever identity is CURRENT right
        /// now, targeting the block <see cref="EnterBlockAsync"/> JUST persisted. The matcher then
        /// clears/upgrades/retains it exactly as it would for any other live reconciliation.
        ///
        /// Only reconciles when a CURRENT accountId is available (mirrors <see cref="SetAccountIdAsync"/>'s
        /// own guard) -- if the race was an endpoint/key-only change with no account known yet,
        /// <see cref="UploadBlockIdentityMatcher.MatchesCurrent"/>'s own "incomplete comparison" rule
        /// already keeps the block retained until an accountId arrives, exactly as it does for every other
        /// live/restore reconciliation in this class.
        ///
        /// MUST NOT be awaited from inside <see cref="EnterBlockAsync"/>: that method runs inside the
        /// drain loop task (<c>ProcessQueueAsync</c>) that <see cref="StopAsync"/> captures and awaits
        /// WHILE HOLDING <see cref="lifecycleLock"/> (see the comment on <see cref="blockStateLock"/>
        /// above) -- an inline await here would deadlock a StopAsync-in-progress against its own drain
        /// loop, exactly what blockStateLock was introduced to prevent. Scheduling this as an independent,
        /// detached task lets it acquire lifecycleLock like any other external lifecycle transition
        /// (StartAsync/StopAsync/SetAccountIdAsync/UpdateContext/ResumeAsync) without the drain loop ever
        /// blocking on it.
        ///
        /// System invariant this establishes: under a concurrent identity change, a block may persist with
        /// the prior identity for at most one reconciliation cycle; the worst-case user-visible effect is
        /// a single upload attempt that re-latches with the correct identity -- never a retry storm, never
        /// a lost frame.
        ///
        /// Never throws -- entirely self-contained so an unobserved fault in this detached task can never
        /// escape (directive G's exception-boundary discipline applies here too); a fault just means this
        /// cycle's self-heal did not run, and the NEXT live/restore reconciliation still catches it.
        /// </summary>
        private async Task SelfHealBlockIdentityAfterCommitAsync()
        {
            try
            {
                await lifecycleLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    var (currentAccountId, currentGeneration) = GetCachedAccountIdWithGeneration();
                    if (!string.IsNullOrEmpty(currentAccountId))
                    {
                        await ReconcileLiveBlockForAccountChangeLockedAsync(currentAccountId, currentGeneration).ConfigureAwait(false);
                    }
                }
                finally
                {
                    lifecycleLock.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[UploadManager] Self-heal reconciliation after a concurrent identity change faulted (non-fatal, a later reconciliation will still repair it): {ex.Message}");
            }
        }

        /// <summary>
        /// Restores the manager's in-memory block latch from the repository's persisted state so a
        /// block SURVIVES a NINA restart (plan point 2), and reconciles identity (plan point 4): a block
        /// scoped to a DIFFERENT current identity is durably cleared; a block whose current identity is
        /// UNAVAILABLE (e.g. auth.dat lost while queue.dat survived) is RETAINED as-is, never treated as
        /// foreign. <paramref name="accountId"/> is optional -- the quota endpoint (plan step 6) does not
        /// exist yet, so callers without one fall back to fingerprint+endpoint comparison. Called once
        /// from <see cref="StartAsync"/>; also an internal test seam (InternalsVisibleTo
        /// CloudUploadPlugin.Tests) so persistence/identity-scoping tests can drive it directly and
        /// deterministically without going through the full start lifecycle.
        /// </summary>
        internal async Task RestoreBlockStateAsync(string accountId = null)
        {
            // Storage-quota plan §4 Step 6: an explicit argument (test seam / a future direct caller)
            // wins; otherwise fall back to whatever SetAccountId has recorded for this session (set
            // after a prior successful GetQuotaAsync -- may still be null on a cold start before the
            // plugin's first quota fetch completes, in which case identity comparison degrades to
            // fingerprint+endpoint exactly as it did before this field existed).
            // D-verdict fix (item 4): when falling back to the cache, capture the generation ALONGSIDE
            // it -- the fingerprint-compute await below can straddle a concurrent context change, the
            // same hazard EnterBlockAsync's latch-time lookup has (P1 #1). An explicit caller-supplied
            // accountId was never sourced from the cache, so it is never subject to this re-validation.
            var usingCachedAccountId = accountId == null;
            string effectiveAccountId;
            var capturedGeneration = 0;
            if (usingCachedAccountId)
            {
                (effectiveAccountId, capturedGeneration) = GetCachedAccountIdWithGeneration();
            }
            else
            {
                effectiveAccountId = accountId;
            }

            var persisted = queueRepository.CurrentBlock;
            if (persisted == null || persisted.Kind == UploadBlockKind.None)
            {
                SetInMemoryBlock(UploadBlockKind.None, null);
                return;
            }

            var apiKey = authManager.GetApiKey();
            string currentFingerprint = null;
            if (!string.IsNullOrEmpty(apiKey))
            {
                currentFingerprint = await queueRepository.ComputeIdentityFingerprintAsync(apiKey).ConfigureAwait(false);
            }
            var currentEndpoint = UploadBlockIdentityMatcher.CanonicalizeEndpoint(apiClient.BaseUrl);

            // Race hardening (Layer 1b): the earlier fix this replaces only
            // re-validated the accountId HALF of the pair -- currentFingerprint above was computed from
            // an apiKey read BEFORE this await, so a concurrent context change (key rotation / reinstall)
            // during the await leaves it stale too, and a partially-corrected accountId compared against
            // a stale fingerprint is exactly the mixed-identity read this whole gate exists to prevent.
            // If the context moved at all while the fingerprint compute was in flight, do not attempt a
            // partial repair: RETAIN the block as-is (fail-safe, matching MatchesCurrent's own "current
            // identity unavailable/incomplete" retain rules) and leave the repair to a later
            // reconciliation (the live path, once a fresh accountId/fingerprint pair is available).
            if (usingCachedAccountId && ContextGeneration != capturedGeneration)
            {
                SetInMemoryBlock(persisted.Kind, persisted.Snapshot);
                Logger.Info("[UploadManager] Restore reconciliation retained the block (fail-safe) -- context changed while awaiting the identity fingerprint; a later reconciliation will repair it against current data");
                OnQueueStateChanged();
                return;
            }

            if (UploadBlockIdentityMatcher.MatchesCurrent(persisted.Identity, currentEndpoint, effectiveAccountId, currentFingerprint))
            {
                SetInMemoryBlock(persisted.Kind, persisted.Snapshot);
                Logger.Info($"[UploadManager] Restored persisted block on load: {persisted.Kind}");
                OnQueueStateChanged();
                return;
            }

            var cleared = await queueRepository.ClearBlockAsync(null).ConfigureAwait(false);
            if (cleared)
            {
                SetInMemoryBlock(UploadBlockKind.None, null);
                Logger.Info("[UploadManager] Persisted block belonged to a different identity -- cleared on load");
            }
            else
            {
                // Storage-quota plan directive C: a failed ClearBlockAsync must NEVER release the
                // in-memory latch (the old code unconditionally cleared it here regardless of the
                // commit's result). Retain the ORIGINAL persisted block and surface the persist-failure
                // warning -- the foreign-identity clear will be retried on the next commit rather than
                // silently starting uploads against an unreleased-but-forgotten block.
                SetInMemoryBlock(persisted.Kind, persisted.Snapshot, persistFailed: true);
                Logger.Warning("[UploadManager] Persisted block belonged to a different identity, but the clear could not be persisted -- retaining it in memory (fail-safe) and will retry on next commit");
            }
            OnQueueStateChanged();
        }

        /// <inheritdoc />
        public async Task<bool> ResumeAsync()
        {
            // Storage-quota plan §4 Step 5 (P0): explicit resume, never inferred from StartAsync (the
            // processing task stays alive throughout a block -- there is no stopped state to catch).
            // KIND-AGNOSTIC: this clears whatever is currently latched. The KIND GATE (AuthInvalid must
            // NEVER be cleared by a generic resume trigger) is the CALLER's responsibility -- see
            // AstrovaultPlugin.ResumeUploadsAsync (checks BlockKind == QuotaExceeded before calling this)
            // and the Test-Connection success path (checks BlockKind == AuthInvalid before calling this).
            //
            // Storage-quota plan directive B (the single transition gate, A#4): held under lifecycleLock
            // for the WHOLE operation -- the SAME gate StartAsync/StopAsync hold -- so a concurrent
            // startup restore (RestoreBlockStateAsync, called from inside StartAsync's own hold) can
            // never resurrect a block this call just durably cleared, or vice versa. Safe against the
            // drain loop: ResumeAsync is only ever invoked from an external caller (a UI action), never
            // from inside ProcessQueueAsync, so awaiting here cannot deadlock against a StopAsync that is
            // itself awaiting the drain loop under the same lock.
            await lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (BlockKind == UploadBlockKind.None)
                {
                    return true; // nothing to resume
                }

                // Pin the job that triggered the block so PeekAsync attempts it first -- read from the
                // repository's PERSISTED state (not an in-memory field) so this works identically whether
                // the block was entered this session or restored after a NINA restart (a fresh manager
                // instance has no memory of which job it was).
                var pinJobId = queueRepository.CurrentBlock?.FailedJobId;
                var committed = await queueRepository.ClearBlockAsync(pinJobId).ConfigureAwait(false);

                if (!committed)
                {
                    // Fail-safe: stay latched (same kind/snapshot), just flip the persist-failure warning
                    // -- never claim a clear that did not durably happen.
                    var stillLatchedKind = BlockKind;
                    var stillLatchedSnapshot = BlockSnapshot;
                    SetInMemoryBlock(stillLatchedKind, stillLatchedSnapshot, persistFailed: true);
                    Logger.Error("[UploadManager] Resume requested but the block-clear commit FAILED to persist durably -- staying latched");
                    // Storage-quota plan directive H: a failed ClearBlockAsync still notifies the UI --
                    // the manager remains latched on the SAME kind, so re-raise the typed event marked
                    // persist-failure (never a bare silent no-op) alongside QueueStateChanged, through the
                    // same dispatcher path a live transition uses.
                    OnQueueStateChanged();
                    OnUploadBlocked(stillLatchedKind, stillLatchedSnapshot, persistFailed: true);
                    return false;
                }

                SetInMemoryBlock(UploadBlockKind.None, null);
                Logger.Info(pinJobId.HasValue
                    ? $"[UploadManager] Resumed -- block cleared, job {pinJobId} pinned to be attempted next"
                    : "[UploadManager] Resumed -- block cleared (no job to pin)");
                OnQueueStateChanged();
                SignalWake();
                return true;
            }
            finally
            {
                lifecycleLock.Release();
            }
        }

        /// <summary>
        /// Storage-quota plan §4 Step 5: releases <see cref="wakeSignal"/> so an idling drain loop (see
        /// <see cref="ProcessQueueAsync"/>) wakes immediately instead of waiting out the ~1s idle poll
        /// interval. Safe to call whether or not the loop is currently waiting on the signal, or even
        /// running at all -- a pending release is simply consumed by the very next wait, and a redundant
        /// release (one already pending) is swallowed rather than thrown.
        /// </summary>
        private void SignalWake()
        {
            try
            {
                wakeSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // A wake is already pending -- no need to stack a second one.
            }
        }

        /// <summary>
        /// Updates the manager's in-memory block latch fields as a single logical unit under
        /// <see cref="blockStateLock"/> (storage-quota plan directive B) so every transition sets
        /// kind/snapshot/warning-flag together, atomically with respect to every reader/writer -- a
        /// non-blocked kind always clears the snapshot, never leaves a stale one behind, and a
        /// concurrent reader (ProcessNextJobAsync's block-gate check, the public properties) never
        /// observes a torn combination of old-kind/new-snapshot or vice versa.
        /// </summary>
        private void SetInMemoryBlock(UploadBlockKind kind, UploadBlockSnapshot snapshot, bool persistFailed = false)
        {
            lock (blockStateLock)
            {
                blockKind = kind;
                blockSnapshot = kind == UploadBlockKind.None ? null : snapshot;
                hasBlockPersistFailureWarning = persistFailed;
            }
        }

        /// <summary>Minimum backoff floor so a fast-failing job cannot burn its whole retry budget
        /// in under a second (REL-07 jitter floor).</summary>
        private const double MinBackoffSeconds = 1.0;

        /// <summary>
        /// Calculates jittered exponential backoff delay (AWS Full Jitter) with a minimum floor.
        /// Returns a random delay between <see cref="MinBackoffSeconds"/> (~1s) and
        /// min(maxDelay, base * 2^attempt). The floor prevents a fast-failing job from exhausting its
        /// entire retry budget in well under a second (REL-07).
        /// </summary>
        internal static double CalculateBackoffDelay(int retryCount)
        {
            var exponentialCeiling = Math.Min(MaxDelaySeconds, BaseDelaySeconds * Math.Pow(2, retryCount));
            return Math.Max(MinBackoffSeconds, Random.Shared.NextDouble() * exponentialCeiling);
        }

        /// <summary>
        /// Updates the capacity warning flag based on total pending queue size.
        /// Logs a warning only on false-to-true transition to avoid flapping spam.
        /// </summary>
        private async Task UpdateCapacityWarning()
        {
            var totalSize = await queueRepository.GetTotalPendingSizeAsync().ConfigureAwait(false);
            var wasWarning = IsCapacityWarning;
            IsCapacityWarning = totalSize > CapacityWarningThresholdBytes;
            if (IsCapacityWarning != wasWarning)
            {
                if (IsCapacityWarning)
                {
                    Logger.Warning("[UploadManager] Upload queue exceeds 10GB -- uploads may be falling behind");
                }
                OnQueueStateChanged();
            }
        }

        private void OnQueueStateChanged()
        {
            // SEC-07: isolate each subscriber. An exception here previously hit the
            // ProcessQueueAsync catch and stalled the queue for 5s per raise.
            RaiseEvent(QueueStateChanged, EventArgs.Empty);
        }

        /// <summary>
        /// SEC-07: Raises an event with per-subscriber isolation. Iterates the
        /// invocation list and wraps each handler in its own try/catch so a single
        /// throwing subscriber cannot stall the upload queue or prevent the other
        /// subscribers from running.
        /// </summary>
        private void RaiseEvent<TArgs>(EventHandler<TArgs> handler, TArgs args)
        {
            var invocationList = handler?.GetInvocationList();
            if (invocationList == null) return;
            foreach (var d in invocationList)
            {
                try
                {
                    ((EventHandler<TArgs>)d)(this, args);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[UploadManager] Event subscriber threw (isolated): {ex.Message}");
                }
            }
        }

        private void RaiseEvent(EventHandler handler, EventArgs args)
        {
            var invocationList = handler?.GetInvocationList();
            if (invocationList == null) return;
            foreach (var d in invocationList)
            {
                try
                {
                    ((EventHandler)d)(this, args);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[UploadManager] Event subscriber threw (isolated): {ex.Message}");
                }
            }
        }

        internal void OnProgressChanged(UploadJob job, double progress)
        {
            // CR-01: this runs on the Progress<double> callback concurrently with bounded-parallel
            // chunk tasks that Add() to job.ChunksSent. Reading job.ChunksSent.Count off-lock here can
            // throw ("Collection was modified") or return a torn count. Derive BOTH bytesUploaded and
            // the displayed chunk index from the byte-accurate progress FRACTION the API client already
            // reports under its progress lock -- never touch the live ChunksSent list.
            var fraction = Math.Max(0.0, Math.Min(1.0, progress));
            long bytesUploaded = (long)(fraction * job.FileSize);
            if (bytesUploaded > job.FileSize) bytesUploaded = job.FileSize;

            // Displayed chunk index (UI shows "(idx/total chunks)"). Derived from the fraction so the
            // count tracks real byte progress without reading the live list. 0 for single-file uploads.
            int chunkIndex = 0;
            if (job.TotalChunks > 0)
            {
                chunkIndex = (int)Math.Round(fraction * job.TotalChunks);
                if (chunkIndex > job.TotalChunks) chunkIndex = job.TotalChunks;
            }

            var args = new UploadProgressEventArgs
            {
                JobId = job.Id,
                Progress = progress,
                FileName = System.IO.Path.GetFileName(job.LocalPath),
                ChunkIndex = chunkIndex,
                TotalChunks = job.TotalChunks,
                BytesUploaded = bytesUploaded,
                TotalBytes = job.FileSize
            };

            // SEC-07: isolate each subscriber so a throwing handler cannot stall the
            // upload queue or take the other subscribers down.
            RaiseEvent(ProgressChanged, args);
        }

        private void OnUploadCompleted(
            UploadJob job,
            bool success,
            string errorMessage,
            int statusCode = 0,
            FailureKind failureKind = FailureKind.Transient)
        {
            var args = new UploadCompletedEventArgs
            {
                JobId = job.Id,
                Success = success,
                FileName = System.IO.Path.GetFileName(job.LocalPath),
                ErrorMessage = errorMessage,
                StatusCode = statusCode,
                FailureKind = failureKind
            };

            // SEC-07: isolate each subscriber so a throwing handler cannot stall the queue.
            RaiseEvent(UploadCompleted, args);
        }

        /// <summary>
        /// Storage-quota plan §4 Step 3 / directive H: raises the typed block-transition event. Called
        /// from <see cref="EnterBlockAsync"/> and <see cref="ResumeAsync"/>'s failure path -- both
        /// COMMITTED and persist-FAILED outcomes (never from <see cref="RestoreBlockStateAsync"/>, a
        /// startup replay) so a subscriber sees exactly one event per LIVE transition, marked via
        /// <paramref name="persistFailed"/> when the underlying commit could not be persisted durably.
        /// </summary>
        private void OnUploadBlocked(UploadBlockKind kind, UploadBlockSnapshot snapshot, bool persistFailed = false)
        {
            var args = new UploadBlockedEventArgs { Kind = kind, Snapshot = snapshot, IsPersistFailure = persistFailed };

            // SEC-07: isolate each subscriber so a throwing handler cannot stall the queue.
            RaiseEvent(UploadBlocked, args);
        }
    }
}
