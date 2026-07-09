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
    }
}
