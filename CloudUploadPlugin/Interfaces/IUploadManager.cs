using System;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Models;

namespace Astrovault.Interfaces
{
    /// <summary>
    /// Event args for upload progress updates.
    /// </summary>
    public class UploadProgressEventArgs : EventArgs
    {
        public Guid JobId { get; set; }
        public double Progress { get; set; }
        public string FileName { get; set; }

        /// <summary>Current chunk index (0-based). 0 for small files using single upload path.</summary>
        public int ChunkIndex { get; set; }

        /// <summary>Total number of chunks. 0 for small files using single upload path.</summary>
        public int TotalChunks { get; set; }

        /// <summary>Total bytes uploaded so far across all chunks.</summary>
        public long BytesUploaded { get; set; }

        /// <summary>Total file size in bytes.</summary>
        public long TotalBytes { get; set; }
    }

    /// <summary>
    /// Event args for upload completion (success or failure).
    /// </summary>
    public class UploadCompletedEventArgs : EventArgs
    {
        public Guid JobId { get; set; }
        public bool Success { get; set; }
        public string FileName { get; set; }
        public string ErrorMessage { get; set; }

        /// <summary>HTTP status code the failure classified from, or 0 for local/unclassified failures.</summary>
        public int StatusCode { get; set; }

        /// <summary>Typed classification of the failure (storage-quota plan §4 Step 1). Defaults to
        /// Transient; meaningless when <see cref="Success"/> is true.</summary>
        public FailureKind FailureKind { get; set; } = FailureKind.Transient;
    }

    /// <summary>
    /// Event args for a block-state transition (storage-quota plan §4 Step 3). Carries the SAME
    /// kind/snapshot payload the manager already latches internally -- deliberately no "is this a
    /// restore replay" flag, because the event is only ever raised on a LIVE transition (see
    /// <see cref="IUploadManager.UploadBlocked"/>); a restore-on-startup never raises it.
    /// </summary>
    public class UploadBlockedEventArgs : EventArgs
    {
        public UploadBlockKind Kind { get; set; }
        public UploadBlockSnapshot Snapshot { get; set; }

        /// <summary>
        /// Storage-quota plan directive H (visibility on failure paths): true when this transition's
        /// underlying repository commit (SetBlockAsync entering a block, or ClearBlockAsync failing to
        /// release one) could NOT be persisted durably. The manager is fail-safe stopped/still-latched
        /// in memory either way; this flag lets a subscriber distinguish "block entered/retained and
        /// durably committed" from "block entered/retained but only in memory -- surface the distinct
        /// persist-failure warning" without silently staying invisible (the old behavior: a failed
        /// commit never raised this event at all).
        /// </summary>
        public bool IsPersistFailure { get; set; }
    }

    /// <summary>
    /// Contract for the background upload manager.
    /// Orchestrates the upload queue processing with retry logic.
    /// </summary>
    public interface IUploadManager
    {
        /// <summary>
        /// Starts the background upload processor.
        /// </summary>
        Task StartAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Stops the background upload processor gracefully.
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// Adds a new job to the upload queue.
        /// </summary>
        Task EnqueueJobAsync(UploadJob job);

        /// <summary>
        /// Fired when upload progress changes for a job.
        /// </summary>
        event EventHandler<UploadProgressEventArgs> ProgressChanged;

        /// <summary>
        /// Fired when an upload completes (success or failure).
        /// </summary>
        event EventHandler<UploadCompletedEventArgs> UploadCompleted;

        /// <summary>
        /// Fired when queue counters or warning flags change (circuit breaker, capacity).
        /// UI should refresh queue-related bindings on this event.
        /// </summary>
        event EventHandler QueueStateChanged;

        /// <summary>
        /// Storage-quota plan §4 Step 3: fired on a LIVE transition into a block (kind + snapshot),
        /// AFTER the durable commit has already succeeded -- mirrors <c>EnterBlockAsync</c>'s existing
        /// commit-then-notify ordering for <see cref="QueueStateChanged"/>. Deliberately NOT raised by
        /// a startup restore (<c>RestoreBlockStateAsync</c> only raises <see cref="QueueStateChanged"/>)
        /// so a UI toast subscriber can fire exactly once per real transition and never replay on every
        /// NINA launch that reloads an already-blocked queue.
        /// </summary>
        event EventHandler<UploadBlockedEventArgs> UploadBlocked;

        /// <summary>
        /// Number of jobs waiting to be uploaded.
        /// </summary>
        int PendingCount { get; }

        /// <summary>
        /// Whether the upload processor is currently running.
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Whether the circuit breaker is currently open (uploads paused due to API outage).
        /// UI can bind to this property for status display.
        /// </summary>
        bool IsCircuitOpen { get; }

        /// <summary>
        /// Whether the upload queue exceeds the 10GB capacity warning threshold.
        /// UI can bind to this for status display.
        /// </summary>
        bool IsCapacityWarning { get; }

        /// <summary>
        /// Whether the queue has stale jobs from a previous session (older than 6 hours).
        /// UI can bind to this for a startup prompt.
        /// </summary>
        bool IsStaleQueue { get; }

        /// <summary>
        /// Number of stale pending/failed jobs from previous session.
        /// </summary>
        int StaleJobCount { get; }

        /// <summary>
        /// Whether the on-disk queue was found corrupt on load and quarantined (manual-restore-only).
        /// UI can bind to this to surface a warning banner.
        /// </summary>
        bool HasCorruptQueueWarning { get; }

        /// <summary>
        /// File name of the quarantined corrupt queue (for the warning banner), or null if none.
        /// </summary>
        string CorruptQueueFileName { get; }

        /// <summary>
        /// Number of completed uploads (lifetime total from retained queue).
        /// </summary>
        int CompletedCount { get; }

        /// <summary>
        /// Number of permanently failed uploads.
        /// </summary>
        int FailedCount { get; }

        /// <summary>
        /// Number of uploads currently in progress (0 or 1 for single-threaded queue).
        /// </summary>
        int UploadingCount { get; }

        /// <summary>
        /// Current queue-blocking latch (storage-quota plan §4 Step 2). <see cref="UploadBlockKind.None"/>
        /// when not blocked. Block banners take precedence over the circuit banner
        /// (<see cref="IsCircuitOpen"/>) -- UI should check this FIRST (full banner UI arrives in Step 3).
        /// </summary>
        UploadBlockKind BlockKind { get; }

        /// <summary>
        /// Numbers recorded when the current block was entered (usedBytes/limitBytes/timestamp), or
        /// null when not blocked or the numbers were unavailable at latch time.
        /// </summary>
        UploadBlockSnapshot BlockSnapshot { get; }

        /// <summary>
        /// True when the most recent block transition could NOT be durably persisted -- the manager is
        /// fail-safe stopped in memory only and will NOT survive a NINA restart until a later commit
        /// succeeds. A distinct warning surface for this lands in Step 3; this flag is the read state
        /// that surface will bind to.
        /// </summary>
        bool HasBlockPersistFailureWarning { get; }

        /// <summary>
        /// Seeds cached counters from repository on startup.
        /// Fixes PendingCount showing 0 after restart.
        /// </summary>
        Task InitializeCountsAsync();

        /// <summary>
        /// Retries a specific failed job (resets to Pending).
        /// </summary>
        Task RetryFailedJobAsync(Guid jobId);

        /// <summary>
        /// Dismisses all failed jobs from the queue.
        /// </summary>
        Task DismissAllFailedAsync();

        /// <summary>
        /// Retries all failed upload jobs by resetting them to Pending status.
        /// </summary>
        Task RetryAllFailedAsync();

        /// <summary>
        /// Resets the historical counters: "Total uploaded" and "Failed" both go to 0 (clears the
        /// running total + retained Completed/Failed records). Pending and Uploading are live state and
        /// are NOT reset. Does not delete source files.
        /// </summary>
        Task ResetCountsAsync();

        /// <summary>
        /// Atomically swaps BOTH the auth manager and API client references used for uploads
        /// (storage-quota plan §4 Step 4, G13 fix). Called when ApiEndpoint changes and services are
        /// reinitialized. Caller must stop the manager first to ensure no in-flight uploads. Replaces
        /// the old API-client-only <c>UpdateApiClient</c>: swapping only the client left the manager
        /// holding a reference to whatever <see cref="Astrovault.Interfaces.IAuthManager"/> the caller
        /// then disposed -- the manager's key check silently read from a disposed instance forever
        /// (running-but-permanently-idle). Both references must come from the SAME reinit so they refer
        /// to the same live endpoint/credential pair.
        /// </summary>
        /// <param name="newAuthManager">The new (live) auth manager.</param>
        /// <param name="newClient">The new API client with updated BaseAddress.</param>
        void UpdateContext(IAuthManager newAuthManager, ICloudApiClient newClient);

        /// <summary>
        /// Storage-quota plan §4 Step 6: stores the account id fetched from a successful
        /// <see cref="Astrovault.Interfaces.ICloudApiClient.GetQuotaAsync"/> call so subsequent block
        /// entries/restores are scoped by ACCOUNT rather than falling back to the raw-key fingerprint
        /// (the backend confirmed the quota response carries a stable <c>accountId</c> -- storage-quota
        /// plan §4 Step 2 point 4 names this as the identity-scoping upgrade). Threaded into
        /// <c>EnterBlockAsync</c>'s identity input and used as the default for
        /// <c>RestoreBlockStateAsync</c>'s optional parameter. UNCONDITIONAL and immediate -- always
        /// applies regardless of <see cref="ContextGeneration"/>, and bumps it (directive E, A#6/B#4):
        /// used by tests and by an authoritative direct clear (context swap, disconnect). A null/empty
        /// value clears back to fingerprint-only scoping until the next successful fetch. Prefer <see
        /// cref="SetAccountIdAsync"/> for a real asynchronous quota-fetch result, where a stale in-flight
        /// fetch (racing an endpoint change) must not be allowed to persist into a block.
        /// </summary>
        void SetAccountId(string accountId);

        /// <summary>
        /// Pass I fix: atomically begins a connect cycle for the Test-Connection flow. Under the same
        /// gate <see cref="SetAccountId"/> uses, compares <see cref="ContextGeneration"/> against
        /// <paramref name="expectedGeneration"/> (captured BEFORE the validate HTTP await): on a match,
        /// clears the cached accountId, bumps the generation, and hands back the new value in
        /// <paramref name="connectGeneration"/> -- the cycle this connect now owns. On a mismatch (an
        /// endpoint swap or another connect raced the validate), nothing is mutated and false is
        /// returned: the caller must discard the validate response's accountId as belonging to the old
        /// context (and perform its old-account clear via <see cref="SetAccountId"/> separately).
        /// The compare and the clear+bump are ONE atomic operation -- the check-then-act gap this
        /// replaces was itself a race window.
        /// </summary>
        bool TryBeginConnectAccountCycle(int expectedGeneration, out int connectGeneration);

        /// <summary>
        /// Storage-quota plan directive E (A#6, B#3, B#4): the context-generation-guarded counterpart to
        /// <see cref="SetAccountId"/>, for the ONE real asynchronous call site (a completed
        /// <c>GetQuotaAsync</c> fetch). <paramref name="expectedContextGeneration"/> must be the value of
        /// <see cref="ContextGeneration"/> captured by the caller BEFORE starting the fetch; if the
        /// context has since changed (an endpoint reinit or disconnect bumped the generation while the
        /// fetch was in flight), the stale result is dropped -- it is NEVER threaded into a future block.
        /// When the generation still matches, this ALSO reconciles a currently-live block (B#3): if the
        /// persisted block belongs to a KNOWN, DIFFERENT account than <paramref name="accountId"/>, it is
        /// durably cleared (honoring the same fail-safe discipline as every other clear -- the in-memory
        /// latch is released only after a successful commit) and the drain loop is woken. A block with an
        /// unknown or matching persisted account is left untouched.
        /// </summary>
        /// <returns>True when the accountId was applied (and any live-block reconciliation attempted);
        /// false when the generation was stale and the call was dropped entirely.</returns>
        Task<bool> SetAccountIdAsync(string accountId, int expectedContextGeneration);

        /// <summary>
        /// Storage-quota plan directive E: monotonically increasing counter bumped by <see
        /// cref="UpdateContext"/> and by <see cref="SetAccountId"/> (an authoritative direct clear, e.g.
        /// disconnect). A caller starting an async accountId fetch captures this value BEFORE the fetch
        /// and passes it to <see cref="SetAccountIdAsync"/> so a result that resolves after the context
        /// has since changed is recognized as stale and dropped.
        /// </summary>
        int ContextGeneration { get; }

        /// <summary>
        /// Storage-quota plan §4 Step 5 (P0): explicitly resumes from a blocked state. NOT inferred from
        /// <see cref="StartAsync"/> -- the drain loop's processing task stays alive while blocked
        /// (<see cref="IsRunning"/> remains true throughout), so there is no stopped-&gt;started
        /// transition for StartAsync to hook. Durably clears the persisted block via
        /// <see cref="Astrovault.Interfaces.IUploadQueueRepository.ClearBlockAsync"/>, pinning the job
        /// that triggered the block (read from the repository's persisted state, so this works whether
        /// the block was entered this session or restored after a NINA restart) so
        /// <see cref="Astrovault.Interfaces.IUploadQueueRepository.PeekAsync"/> attempts it first. The
        /// in-memory latch (<see cref="BlockKind"/>) is released ONLY after that commit succeeds
        /// (fail-safe: on a failed commit the manager stays latched and <see
        /// cref="HasBlockPersistFailureWarning"/> is set -- never claim a clear that did not happen).
        /// KIND-AGNOSTIC: this clears whichever kind is currently latched. The KIND GATE (which triggers
        /// may call this for which kind) is the CALLER's responsibility -- see
        /// <c>AstrovaultPlugin.ResumeUploadsAsync</c> (QuotaExceeded only) and the Test-Connection
        /// success path (AuthInvalid only).
        /// </summary>
        /// <returns>True when the block was cleared (or nothing was blocked); false when the durable
        /// commit failed and the manager remains latched.</returns>
        Task<bool> ResumeAsync();
    }
}
