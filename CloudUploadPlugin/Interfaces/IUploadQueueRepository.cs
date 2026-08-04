using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Astrovault.Models;

namespace Astrovault.Interfaces
{
    /// <summary>
    /// Contract for persistent upload queue storage.
    /// Implementations can use JSON files, SQLite, or other storage mechanisms.
    /// </summary>
    public interface IUploadQueueRepository
    {
        /// <summary>
        /// Adds a new upload job to the queue.
        /// </summary>
        Task EnqueueAsync(UploadJob job);

        /// <summary>
        /// Gets the next pending job without removing it from the queue.
        /// </summary>
        /// <returns>Next pending job, or null if queue is empty.</returns>
        Task<UploadJob> PeekAsync();

        /// <summary>
        /// Gets all jobs with the specified status.
        /// </summary>
        Task<IEnumerable<UploadJob>> GetJobsByStatusAsync(UploadStatus status);

        /// <summary>
        /// Gets all pending jobs ordered by queue time.
        /// </summary>
        Task<IEnumerable<UploadJob>> GetPendingJobsAsync();

        /// <summary>
        /// Updates the status of a job.
        /// </summary>
        Task UpdateJobStatusAsync(Guid jobId, UploadStatus status, string errorMessage = null);

        /// <summary>
        /// Increments the retry count for a job.
        /// </summary>
        Task IncrementRetryCountAsync(Guid jobId);

        /// <summary>
        /// Removes a completed or cancelled job from the queue.
        /// </summary>
        Task RemoveJobAsync(Guid jobId);

        /// <summary>
        /// Gets the count of jobs with the specified status.
        /// </summary>
        Task<int> GetJobCountAsync(UploadStatus status);

        /// <summary>
        /// Gets the total size in bytes of all pending jobs.
        /// Used for capacity limit enforcement.
        /// </summary>
        Task<long> GetTotalPendingSizeAsync();

        /// <summary>
        /// Updates the chunked upload resume state for a job.
        /// Called after each successful chunk upload for maximum crash safety.
        /// </summary>
        Task UpdateJobChunkStateAsync(Guid jobId, string sessionId, List<int> chunksSent, int totalChunks);

        /// <summary>
        /// Resets retry counts for all Pending jobs that failed due to transient errors.
        /// Called when circuit breaker closes after an outage -- outage failures
        /// should not penalize jobs that would succeed on a healthy API.
        /// </summary>
        Task ResetRetryCountsForTransientAsync();

        /// <summary>
        /// REL-07: Schedules a transient-failed job for a non-blocking retry. Sets Status to Pending,
        /// records the backoff deadline (NextRetryAfter) so the not-yet-due filter skips it until due,
        /// and records the diagnostic reason as LastFailureReason. Replaces the blocking Task.Delay backoff.
        /// </summary>
        Task ScheduleRetryAsync(Guid jobId, DateTime nextRetryAfter, string reason);

        /// <summary>
        /// Marks a job terminally Failed and records its diagnostic reason (D-12) plus the
        /// transient-vs-permanent marker (REL-06). isPermanent = true excludes the job from
        /// auto-promotion; false marks it auto-promotable on a half-open probe. LastFailureReason is set
        /// so the failure surfaces via the existing notification path and is preserved as history.
        /// </summary>
        Task MarkFailedAsync(Guid jobId, string reason, bool isPermanent);

        /// <summary>
        /// REL-06: Promotes TRANSIENT-Failed jobs back to Pending so the circuit breaker's half-open
        /// probe has work to send, breaking the HalfOpen dead-end where PeekAsync returns null because
        /// every job is Failed. PERMANENT-failed jobs are excluded (never auto-promoted, never retried
        /// forever). Resets the retry budget and CLEARS NextRetryAfter (immediately due) while PRESERVING
        /// LastFailureReason as history. Returns the number of jobs promoted.
        /// </summary>
        Task<int> PromoteTransientFailedToPendingAsync();

        /// <summary>
        /// Resets a failed job to Pending for retry. Clears RetryCount, ErrorMessage, FailedAt,
        /// and all chunked upload resume state (UploadSessionId, ChunksSent, TotalChunks, SessionRestartCount).
        /// </summary>
        /// <param name="jobId">The job to retry.</param>
        Task RetryJobAsync(Guid jobId);

        /// <summary>
        /// Removes all jobs with Failed status (user "dismiss all" action).
        /// </summary>
        Task RemoveFailedJobsAsync();

        /// <summary>
        /// Gets completed jobs ordered by CompletedAt descending (most recent first).
        /// Used by Phase 8 dashboard for "recently uploaded" list.
        /// </summary>
        Task<IEnumerable<UploadJob>> GetCompletedJobsAsync();

        /// <summary>
        /// Lifetime running total of successfully completed uploads. Monotonic and persisted across
        /// restarts. This is DECOUPLED from the retained completed-job records (which are capped at
        /// MaxCompletedJobsRetained for storage hygiene) -- so the displayed "Completed" count keeps
        /// climbing past the retention cap instead of being clamped to it. The UI count must be sourced
        /// from this, never from GetJobCountAsync(Completed) which only counts retained records.
        /// </summary>
        int CompletedTotal { get; }

        /// <summary>
        /// Resets the HISTORICAL counters: zeroes the lifetime <see cref="CompletedTotal"/> and removes
        /// the retained Completed AND terminal Failed job records, so "Total uploaded" and "Failed" read
        /// 0 (e.g. starting a new project/night). Pending and InProgress jobs are PRESERVED -- those are
        /// live state, not history, so resetting must never drop a not-yet-uploaded image. No source
        /// files are deleted.
        /// </summary>
        Task ResetCountsAsync();

        /// <summary>
        /// Whether the queue has stale jobs from a previous session (older than 6 hours).
        /// </summary>
        bool IsStaleQueue { get; }

        /// <summary>
        /// Number of stale pending/failed jobs from previous session.
        /// </summary>
        int StaleJobCount { get; }

        /// <summary>
        /// Whether the on-disk queue was found corrupt/undecryptable on load and quarantined.
        /// <para>
        /// Recovery contract: when true, the bad queue.dat was renamed to a
        /// <c>queue.dat.corrupt-&lt;timestamp&gt;</c> file (see <see cref="CorruptQueueFileName"/>) and the
        /// repository continued with an EMPTY live queue. The quarantine file is preserved for
        /// MANUAL inspection/restore only -- it is never auto-restored, and subsequent saves never
        /// overwrite it. The last-good <c>.bak</c> (if any) is also preserved as a separate rollback artifact.
        /// </para>
        /// Mirrors the <see cref="IsStaleQueue"/> warning pattern so the existing banner can surface it.
        /// </summary>
        bool HasCorruptQueueWarning { get; }

        /// <summary>
        /// File name (not full path) of the quarantined corrupt queue, e.g.
        /// <c>queue.dat.corrupt-20260610T030000Z</c>, or null when no corruption was detected.
        /// The file is retained for manual recovery; it is never auto-imported.
        /// </summary>
        string CorruptQueueFileName { get; }

        // ====================================================================
        // Blocked-state machine (storage-quota plan §4 Step 2)
        // ====================================================================

        /// <summary>
        /// The currently persisted block (Kind == <see cref="UploadBlockKind.None"/> when not
        /// blocked). Restored from disk at construction time so a block SURVIVES a NINA restart.
        /// Safe to read without external synchronization -- transitions always swap in a brand-new,
        /// fully-formed instance under the repository's internal lock, so a caller never observes a
        /// torn/partial state.
        /// </summary>
        UploadBlockState CurrentBlock { get; }

        /// <summary>
        /// Atomically transitions the queue into a blocked state (storage-quota plan §4 Step 2, point 2):
        /// in ONE semaphore-held commit, the job identified by <paramref name="failedJobId"/> is returned
        /// to <see cref="UploadStatus.Pending"/> (never <see cref="UploadStatus.Failed"/>, nothing
        /// discarded, <see cref="UploadJob.QueuedAt"/> untouched) AND the block state (kind/snapshot/identity)
        /// is persisted together with it. <paramref name="identity"/>'s raw API key is used only to derive
        /// a salted fingerprint internally -- it is never itself persisted (point 4: "never a bare digest").
        /// <paramref name="snapshot"/>'s numeric fields are copied through as given; the repository always
        /// stamps its own commit-time UTC timestamp rather than trusting a caller-supplied clock.
        /// </summary>
        /// <returns>
        /// True when the commit durably succeeded. False when the disk write failed -- the in-memory
        /// state (job Pending, <see cref="CurrentBlock"/> reflecting the new block) is still applied
        /// (fail-safe: the caller must treat this as "stopped, but NOT proven durable" and surface a
        /// distinct warning -- never claim a durability that did not happen).
        /// </returns>
        Task<bool> SetBlockAsync(UploadBlockKind kind, Guid failedJobId, UploadBlockSnapshot snapshot, UploadBlockIdentityInput identity);

        /// <summary>
        /// Atomically clears the persisted block (<see cref="CurrentBlock"/> becomes
        /// <see cref="UploadBlockState.None"/>) and durably pins <paramref name="resumePriorityJobId"/>
        /// as the next job <see cref="PeekAsync"/> must return, ahead of the normal drain order.
        /// <see cref="UploadJob.QueuedAt"/> is never mutated for the pinned job. Pass null to clear the
        /// block without pinning any job (e.g. a foreign-identity block discarded on load).
        /// </summary>
        /// <returns>True when the commit durably succeeded; see <see cref="SetBlockAsync"/>'s remarks
        /// on the fail-safe meaning of a false return.</returns>
        Task<bool> ClearBlockAsync(Guid? resumePriorityJobId);

        /// <summary>
        /// Computes a salted HMAC-SHA256 fingerprint of <paramref name="apiKey"/> using this
        /// repository's per-installation identity salt (stored inside the same DPAPI-encrypted
        /// envelope as the queue itself, generated lazily on first use and persisted immediately so
        /// later calls -- including after a restart -- derive the SAME fingerprint for the SAME key).
        /// The salt itself is never exposed -- only the resulting fingerprint. Returns null for a
        /// null/empty <paramref name="apiKey"/> (a "missing current identity" input).
        /// </summary>
        Task<string> ComputeIdentityFingerprintAsync(string apiKey);

        /// <summary>
        /// Storage-quota plan (C-P1#1 fix): durably resets a SPECIFIC job back to
        /// <see cref="UploadStatus.Pending"/>, but ONLY if it is CURRENTLY <see cref="UploadStatus.InProgress"/>
        /// -- a job that has already moved to some other status is left untouched. Called from
        /// <c>UploadManager</c>'s clean-cancellation catch so a cleanly-cancelled upload (endpoint
        /// reinit, Disconnect, shutdown) is immediately visible to <see cref="PeekAsync"/> again within
        /// the SAME repository instance/process, without waiting for a fresh disk load
        /// (<c>LoadQueueFromDisk</c> already resets EVERY InProgress job on a fresh load; this is the
        /// same-process counterpart for one specific job).
        /// </summary>
        /// <returns>True when nothing needed resetting, or the reset persisted durably; false when the
        /// in-memory reset happened but the commit failed to persist (the in-memory state stays
        /// Pending regardless -- callers are not required to act on a false return, it exists so the
        /// failure can be logged/observed honestly instead of silently discarded).</returns>
        Task<bool> ResetJobToPendingIfInProgressAsync(Guid jobId);

        /// <summary>
        /// Storage-quota plan (C-P1#1 fix): durably resets EVERY job currently
        /// <see cref="UploadStatus.InProgress"/> back to <see cref="UploadStatus.Pending"/>, in ONE
        /// semaphore-held commit -- the same-process defensive counterpart to what <c>LoadQueueFromDisk</c>
        /// already does for a fresh load. Called from <c>UploadManager.StartAsync</c> so a job left
        /// InProgress by an abnormal termination of a PREVIOUS processing loop can never remain
        /// permanently invisible to <see cref="PeekAsync"/> within the live repository instance.
        /// </summary>
        /// <returns>
        /// The number of jobs affected IN MEMORY -- always the count of jobs flipped Pending regardless of
        /// whether the subsequent disk persist succeeded. Unlike <see cref="ResetJobToPendingIfInProgressAsync"/>,
        /// this method has no boolean success channel to report a failed persist through: a failed commit
        /// still logs a warning internally (never silently), but the returned count does NOT tell the
        /// caller whether the reset reached disk -- only how many jobs were reset in memory. Callers that
        /// need durability confirmation must not infer it from a non-zero return.
        /// </returns>
        Task<int> ResetAllInProgressToPendingAsync();

        /// <summary>
        /// Storage-quota plan directive E (C-P1#4a): durably rewrites the CURRENTLY latched block's
        /// identity to carry <paramref name="accountId"/>, in ONE semaphore-held commit -- the "upgrade"
        /// path for a block that was entered before its account id was known (fingerprint+endpoint
        /// scoping only). Leaves the block's Kind/Snapshot/FailedJobId and the identity's
        /// EndpointUrl/KeyFingerprint untouched; only AccountId changes. A no-op (returns true) when
        /// nothing is currently latched. Never claims a durability that did not happen: on a failed disk
        /// write, the in-memory identity is rolled back to its pre-attempt value and false is returned.
        /// </summary>
        Task<bool> UpgradeBlockAccountIdAsync(string accountId);
    }
}
