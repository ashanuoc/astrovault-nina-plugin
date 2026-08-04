using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Core;
using Astrovault.Interfaces;
using Astrovault.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NINA.Core.Utility;

// SECURITY NOTE: Queue data is encrypted using Windows DPAPI (CurrentUser)
// before storage. File paths and metadata are protected at rest.

namespace Astrovault.Data
{
    /// <summary>
    /// JSON file-based persistent upload queue.
    /// Thread-safe and survives application restarts.
    /// </summary>
    public class UploadQueueRepository : IUploadQueueRepository
    {
        private readonly string queueFilePath;
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
        private List<UploadJob> jobs = new List<UploadJob>();

        /// <summary>
        /// Drain-order selector. Returns true for Newest-first (LIFO), false for Oldest-first (FIFO).
        /// Injected by AstrovaultPlugin (the only owner of IPluginOptionsAccessor) so the repo never
        /// touches settings directly. Defaults to Newest-first (LIFO) when not provided -- zero
        /// behavior change for existing callers (e.g. tests using new UploadQueueRepository(dataFolder)).
        /// Consulted on every drain so a live settings change takes effect immediately.
        /// </summary>
        private readonly Func<bool> getNewestFirst;

        private const int MaxCompletedJobsRetained = 50;

        /// <summary>
        /// Lifetime running total of completed uploads. Monotonic, persisted in the envelope, and
        /// DECOUPLED from the retained completed-job records (capped at MaxCompletedJobsRetained). The
        /// displayed "Completed" counter is sourced from this so it keeps climbing past the retention
        /// cap instead of oscillating at it. Mutated only under <see cref="semaphore"/>.
        /// </summary>
        private int completedTotal;

        /// <summary>Terminal Failed jobs older than this are auto-pruned (D-06).</summary>
        private static readonly TimeSpan FailedJobMaxAge = TimeSpan.FromDays(7);

        /// <summary>Maximum number of newest terminal Failed jobs retained (D-06).</summary>
        private const int MaxFailedJobsRetained = 200;

        /// <summary>
        /// Current on-disk queue schema version (the {SchemaVersion, Jobs, CompletedTotal, Block,
        /// ResumePriorityJobId, IdentitySalt} envelope). v2 added the persisted CompletedTotal running
        /// total; v1 files (no CompletedTotal) are migrated on load by seeding the total from the
        /// retained completed-job count. v3 (storage-quota plan §4 Step 2) additively adds the
        /// blocked-state fields -- a v2 (or v1) file loads fine, with Block defaulting to "not blocked",
        /// no resume pin, and no identity salt (lazily generated + persisted on first use).
        /// </summary>
        private const int CurrentSchemaVersion = 3;

        /// <summary>
        /// JSON settings for queue persistence. Statuses are serialized by NAME (StringEnumConverter)
        /// so enum-value reordering/removal across upgrades cannot shift other values, and unknown
        /// names are tolerated on read (we sanitize them post-deserialize rather than throwing).
        /// </summary>
        private static readonly JsonSerializerSettings QueueJsonSettings = new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter() },
            Formatting = Formatting.None,
            // Unknown JSON members on UploadJob (e.g. fields added by other plans on old files)
            // are ignored rather than throwing.
            MissingMemberHandling = MissingMemberHandling.Ignore,
        };

        /// <summary>
        /// On-disk envelope wrapping the persisted jobs with a schema version for safe upgrades.
        /// </summary>
        private sealed class QueueEnvelope
        {
            public int SchemaVersion { get; set; }
            public List<UploadJob> Jobs { get; set; } = new List<UploadJob>();
        }

        /// <summary>
        /// Lifetime running total of completed uploads (see field <see cref="completedTotal"/>).
        /// Read without the semaphore via Volatile.Read -- int reads are atomic and writes happen
        /// under the semaphore, so a cross-thread reader (UploadManager counter recompute) sees a
        /// consistent, never-torn value.
        /// </summary>
        public int CompletedTotal => System.Threading.Volatile.Read(ref completedTotal);

        /// <summary>Whether the queue has stale jobs from a previous session (older than 6 hours).</summary>
        public bool IsStaleQueue { get; private set; }

        /// <summary>Number of stale pending/failed jobs from previous session.</summary>
        public int StaleJobCount { get; private set; }

        /// <summary>Age of the oldest stale pending/failed job.</summary>
        public TimeSpan StaleQueueAge { get; private set; }

        /// <summary>
        /// Whether the on-disk queue was found corrupt/undecryptable on load and quarantined.
        /// <para>
        /// Recovery contract: when true, the bad queue.dat was renamed to a
        /// <c>queue.dat.corrupt-&lt;timestamp&gt;</c> file (see <see cref="CorruptQueueFileName"/>) and this
        /// repository continued with an EMPTY live queue. The quarantine file is preserved for MANUAL
        /// inspection/restore ONLY -- it is never auto-restored, and subsequent saves never overwrite it.
        /// The last-good <c>.bak</c> (if any) is also preserved as a separate rollback artifact. The user
        /// decides whether to recover from either file.
        /// </para>
        /// </summary>
        public bool HasCorruptQueueWarning { get; private set; }

        /// <summary>
        /// File name (not full path) of the quarantined corrupt queue, or null when no corruption was detected.
        /// Retained for manual recovery; never auto-imported.
        /// </summary>
        public string CorruptQueueFileName { get; private set; }

        // ====================================================================
        // Blocked-state machine (storage-quota plan §4 Step 2)
        // ====================================================================

        /// <summary>
        /// Backing field for <see cref="CurrentBlock"/>. ALWAYS replaced wholesale (never mutated
        /// in place) under <see cref="semaphore"/>, then published via <see cref="Volatile.Write"/> so a
        /// reader without the lock (e.g. UploadManager's counter/UI reads) sees either the fully-formed
        /// old value or the fully-formed new value, never a torn one -- mirrors the existing
        /// <see cref="completedTotal"/> volatile-read pattern in this file.
        /// </summary>
        private UploadBlockState currentBlock = UploadBlockState.None;

        /// <inheritdoc />
        public UploadBlockState CurrentBlock => System.Threading.Volatile.Read(ref currentBlock);

        /// <summary>
        /// Job PeekAsync must return first (ahead of the normal drain order), set by
        /// <see cref="ClearBlockAsync"/>. Only ever read/written under <see cref="semaphore"/> (both
        /// call sites already hold it), so no separate volatile access is needed.
        /// </summary>
        private Guid? resumePriorityJobId;

        /// <summary>
        /// Per-installation random salt for <see cref="ComputeIdentityFingerprintAsync"/>'s HMAC-SHA256,
        /// persisted inside the SAME DPAPI-encrypted envelope as the queue (never a bare digest of the
        /// key -- storage-quota plan §4 Step 2, point 4). Lazily generated on first use; only ever
        /// read/written under <see cref="semaphore"/>.
        /// </summary>
        private byte[] identitySalt;

        /// <summary>
        /// Creates a new queue repository with the specified data folder.
        /// </summary>
        /// <param name="dataFolderPath">Folder to store encrypted queue.dat.</param>
        /// <param name="getNewestFirst">
        /// Optional drain-order selector: returns true for Newest-first (LIFO), false for Oldest-first
        /// (FIFO). When null, drain order is Newest-first (LIFO) -- the historical default, so existing
        /// callers see zero behavior change. Consulted on every drain so a live settings change applies
        /// immediately.
        /// </param>
        public UploadQueueRepository(string dataFolderPath, Func<bool> getNewestFirst = null)
        {
            this.getNewestFirst = getNewestFirst;
            Directory.CreateDirectory(dataFolderPath);
            queueFilePath = Path.Combine(dataFolderPath, "queue.dat");
            MigrateUnencryptedQueue(dataFolderPath);
            LoadQueueFromDisk();
            PruneExcessFailedJobs();
            DetectStaleQueue();
        }

        /// <summary>
        /// True when the queue should drain Newest-first (LIFO). Reads the injected selector on each
        /// call; defaults to true (LIFO) when no selector was provided.
        /// </summary>
        private bool DrainNewestFirst => getNewestFirst?.Invoke() ?? true;

        /// <summary>
        /// Orders pending jobs by the active drain order: Newest-first (LIFO) descending by QueuedAt,
        /// or Oldest-first (FIFO) ascending. Isolated so it composes with later filters (e.g. the
        /// NextRetryAfter not-yet-due filter added in plan 04).
        /// </summary>
        private IEnumerable<UploadJob> OrderPendingByDrainOrder(IEnumerable<UploadJob> pending)
        {
            return DrainNewestFirst
                ? pending.OrderByDescending(j => j.QueuedAt)
                : pending.OrderBy(j => j.QueuedAt);
        }

        /// <summary>
        /// REL-07 not-yet-due filter: a Pending job is eligible only when it has no pending backoff
        /// (<see cref="UploadJob.NextRetryAfter"/> null) or that backoff has elapsed. This replaces the
        /// old blocking Task.Delay so a job in backoff does not head-of-line block the drain loop --
        /// the loop simply skips it and returns the next due job. Composes with the drain-order switch.
        /// </summary>
        private static bool IsDuePending(UploadJob j, DateTime now)
        {
            return j.Status == UploadStatus.Pending
                   && (j.NextRetryAfter == null || j.NextRetryAfter.Value <= now);
        }

        /// <summary>
        /// Migrates old unencrypted queue.json to encrypted queue.dat.
        /// </summary>
        private void MigrateUnencryptedQueue(string dataFolderPath)
        {
            var oldPath = Path.Combine(dataFolderPath, "queue.json");
            if (!File.Exists(oldPath) || File.Exists(queueFilePath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(oldPath);
                var oldJobs = JsonConvert.DeserializeObject<List<UploadJob>>(json);
                if (oldJobs != null && oldJobs.Count > 0)
                {
                    jobs = oldJobs;
                    SaveQueueToDiskSync();
                    Logger.Info($"[Queue] Migrated {oldJobs.Count} jobs from unencrypted queue.json");
                }
                File.Delete(oldPath);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Queue] Migration failed: {ex.Message}");
            }
        }

        public async Task EnqueueAsync(UploadJob job)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                job.QueuedAt = DateTime.UtcNow;
                job.Status = UploadStatus.Pending;
                jobs.Add(job);
                await SaveQueueToDiskAsync().ConfigureAwait(false);
                Logger.Info($"[Queue] Enqueued job {job.Id}: {LogSanitizer.MaskPath(job.LocalPath)}");
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task<UploadJob> PeekAsync()
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var now = DateTime.UtcNow;

                // Storage-quota plan §4 Step 2, point 3: a job pinned by ClearBlockAsync is attempted
                // FIRST, ahead of the normal drain order -- the resume probe costs exactly one request.
                // If the pinned job isn't (or is no longer) a due Pending job -- completed, dismissed,
                // still in backoff -- fall through to the normal order without clearing the pin; only
                // an explicit new SetBlockAsync/ClearBlockAsync commit changes it.
                if (resumePriorityJobId.HasValue)
                {
                    var pinned = jobs.FirstOrDefault(j => j.Id == resumePriorityJobId.Value && IsDuePending(j, now));
                    if (pinned != null)
                    {
                        return pinned;
                    }
                }

                // REL-07: skip jobs whose NextRetryAfter is still in the future (non-blocking backoff).
                return OrderPendingByDrainOrder(
                        jobs.Where(j => IsDuePending(j, now)))
                    .FirstOrDefault();
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task<IEnumerable<UploadJob>> GetJobsByStatusAsync(UploadStatus status)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                return jobs.Where(j => j.Status == status).ToList();
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task<IEnumerable<UploadJob>> GetPendingJobsAsync()
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var now = DateTime.UtcNow;
                // REL-07: only return jobs that are due (NextRetryAfter elapsed or unset).
                return OrderPendingByDrainOrder(
                        jobs.Where(j => IsDuePending(j, now)))
                    .ToList();
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task UpdateJobStatusAsync(Guid jobId, UploadStatus status, string errorMessage = null)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var job = jobs.FirstOrDefault(j => j.Id == jobId);
                if (job != null)
                {
                    var previousStatus = job.Status;
                    job.Status = status;
                    if (errorMessage != null)
                    {
                        job.ErrorMessage = errorMessage;
                    }

                    // Set timestamps on terminal status transitions
                    if (status == UploadStatus.Failed)
                    {
                        job.FailedAt = DateTime.UtcNow;
                        PruneExcessFailedJobs();
                    }
                    else if (status == UploadStatus.Completed)
                    {
                        job.CompletedAt = DateTime.UtcNow;
                        // Bump the lifetime running total ONCE per job, only on a genuine NEW transition
                        // into Completed (idempotent if this is called again for an already-Completed job).
                        // This is decoupled from PurgeExcessCompletedJobs so the total keeps climbing even
                        // as old completed RECORDS are trimmed to the retention cap.
                        if (previousStatus != UploadStatus.Completed)
                        {
                            completedTotal++;
                        }
                        PurgeExcessCompletedJobs();
                    }

                    await SaveQueueToDiskAsync().ConfigureAwait(false);
                    Logger.Debug($"[Queue] Job {jobId} status updated to {status}");
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task IncrementRetryCountAsync(Guid jobId)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var job = jobs.FirstOrDefault(j => j.Id == jobId);
                if (job != null)
                {
                    job.RetryCount++;
                    await SaveQueueToDiskAsync().ConfigureAwait(false);
                    Logger.Debug($"[Queue] Job {jobId} retry count: {job.RetryCount}");
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task RemoveJobAsync(Guid jobId)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var removed = jobs.RemoveAll(j => j.Id == jobId);
                if (removed > 0)
                {
                    await SaveQueueToDiskAsync().ConfigureAwait(false);
                    Logger.Debug($"[Queue] Removed job {jobId}");
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task<int> GetJobCountAsync(UploadStatus status)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                return jobs.Count(j => j.Status == status);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Sums the queue-time <see cref="UploadJob.FileSize"/> of all Pending jobs for the 10 GB
        /// capacity warning. IN-03: this is BEST-EFFORT — a job enqueued before its bytes landed on
        /// disk (zero-length retry exhausted) carries FileSize == 0 and under-reports here. The size is
        /// re-stat'd authoritatively at upload time (SEC-05), so the under-report self-corrects as jobs
        /// drain; the warning is intentionally advisory, not an exact accounting.
        /// </summary>
        public async Task<long> GetTotalPendingSizeAsync()
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                return jobs
                    .Where(j => j.Status == UploadStatus.Pending)
                    .Sum(j => j.FileSize);
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task ResetRetryCountsForTransientAsync()
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var affected = jobs.Where(j => j.Status == UploadStatus.Pending && j.RetryCount > 0).ToList();
                foreach (var job in affected)
                {
                    job.RetryCount = 0;
                }

                if (affected.Count > 0)
                {
                    await SaveQueueToDiskAsync().ConfigureAwait(false);
                    Logger.Info($"[Queue] Reset retry counts for {affected.Count} transient-failed jobs");
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// REL-07: Schedules a transient-failed job for a non-blocking retry. Sets Status back to Pending,
        /// records the backoff deadline (<see cref="UploadJob.NextRetryAfter"/> = nextRetryAfter) so the
        /// not-yet-due filter skips it until due, and records the diagnostic <paramref name="reason"/> as
        /// <see cref="UploadJob.LastFailureReason"/>. Replaces the old blocking Task.Delay backoff.
        /// </summary>
        public async Task ScheduleRetryAsync(Guid jobId, DateTime nextRetryAfter, string reason)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var job = jobs.FirstOrDefault(j => j.Id == jobId);
                if (job != null)
                {
                    job.Status = UploadStatus.Pending;
                    job.NextRetryAfter = nextRetryAfter;
                    if (reason != null)
                    {
                        job.ErrorMessage = reason;
                        job.LastFailureReason = reason;
                    }
                    await SaveQueueToDiskAsync().ConfigureAwait(false);
                    Logger.Debug($"[Queue] Job {jobId} scheduled for retry after {nextRetryAfter:u}");
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Marks a job terminally Failed and records its diagnostic reason (D-12) plus the
        /// transient-vs-permanent marker (REL-06). <paramref name="isPermanent"/> = true excludes the job
        /// from auto-promotion (it is never retried forever); false marks it auto-promotable on a
        /// half-open probe. <see cref="UploadJob.LastFailureReason"/> is set so the failure surfaces via
        /// the existing notification path and is preserved as history.
        /// </summary>
        public async Task MarkFailedAsync(Guid jobId, string reason, bool isPermanent)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var job = jobs.FirstOrDefault(j => j.Id == jobId);
                if (job != null)
                {
                    job.Status = UploadStatus.Failed;
                    job.FailedAt = DateTime.UtcNow;
                    job.IsPermanentFailure = isPermanent;
                    job.NextRetryAfter = null;
                    if (reason != null)
                    {
                        job.ErrorMessage = reason;
                        job.LastFailureReason = reason;
                    }
                    PruneExcessFailedJobs();
                    await SaveQueueToDiskAsync().ConfigureAwait(false);
                    Logger.Debug($"[Queue] Job {jobId} marked Failed (permanent={isPermanent})");
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// REL-06 half-open probe promotion: promotes TRANSIENT-Failed jobs back to Pending so the
        /// breaker's probe has something to send, breaking the HalfOpen dead-end where PeekAsync returns
        /// null because every job is Failed. PERMANENT-failed jobs (<see cref="UploadJob.IsPermanentFailure"/>)
        /// are excluded -- they are never auto-promoted and never retried forever. On promotion the retry
        /// budget is reset and <see cref="UploadJob.NextRetryAfter"/> is CLEARED (job immediately due),
        /// while <see cref="UploadJob.LastFailureReason"/> is PRESERVED as history. Returns the number of
        /// jobs promoted.
        /// </summary>
        public async Task<int> PromoteTransientFailedToPendingAsync()
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var promotable = jobs
                    .Where(j => j.Status == UploadStatus.Failed && !j.IsPermanentFailure)
                    .ToList();

                foreach (var job in promotable)
                {
                    job.Status = UploadStatus.Pending;
                    job.RetryCount = 0;
                    job.NextRetryAfter = null;   // immediately due
                    job.FailedAt = null;
                    // LastFailureReason intentionally preserved as history (not cleared).
                    // Clear chunk resume state for a clean restart.
                    job.UploadSessionId = null;
                    job.ChunksSent = new List<int>();
                    job.TotalChunks = 0;
                    job.SessionRestartCount = 0;
                }

                if (promotable.Count > 0)
                {
                    await SaveQueueToDiskAsync().ConfigureAwait(false);
                    Logger.Info($"[Queue] Promoted {promotable.Count} transient-failed jobs to Pending for half-open probe");
                }

                return promotable.Count;
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task UpdateJobChunkStateAsync(Guid jobId, string sessionId, List<int> chunksSent, int totalChunks)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var job = jobs.FirstOrDefault(j => j.Id == jobId);
                if (job != null)
                {
                    job.UploadSessionId = sessionId;
                    // CR-01: chunksSent is a caller-owned snapshot already taken under the job's
                    // ChunksSentLock by AstrovaultApiClient.SnapshotChunksSent. Publish it under the
                    // SAME per-job lock so the reference swap cannot race a sibling chunk task that is
                    // mid-Add() on the previous list (a swap off-lock under the repo semaphore -- a
                    // DIFFERENT lock -- could drop a recorded chunk index or hand a sibling a list to
                    // Add() into outside any shared lock).
                    lock (job.ChunksSentLock)
                    {
                        job.ChunksSent = chunksSent;
                    }
                    job.TotalChunks = totalChunks;
                    await SaveQueueToDiskAsync().ConfigureAwait(false);
                    Logger.Debug($"[Queue] Job {jobId} chunk state updated: {chunksSent.Count}/{totalChunks} chunks");
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Resets a failed job to Pending for retry. Clears RetryCount, ErrorMessage, FailedAt,
        /// and all chunked upload resume state.
        /// </summary>
        public async Task RetryJobAsync(Guid jobId)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var job = jobs.FirstOrDefault(j => j.Id == jobId && j.Status == UploadStatus.Failed);
                if (job != null)
                {
                    job.Status = UploadStatus.Pending;
                    job.RetryCount = 0;
                    job.ErrorMessage = null;
                    job.FailedAt = null;

                    // REL-07: clear any pending backoff so a manual retry is immediately due.
                    job.NextRetryAfter = null;
                    // User-forced retry resets the permanent marker so the job gets a fresh attempt.
                    job.IsPermanentFailure = false;
                    // NOTE: LastFailureReason (D-12) is intentionally PRESERVED as history -- it is
                    // overwritten only by a subsequent failure, never cleared on retry.

                    // Clear chunk resume state for clean restart
                    job.UploadSessionId = null;
                    job.ChunksSent = new List<int>();
                    job.TotalChunks = 0;
                    job.SessionRestartCount = 0;

                    await SaveQueueToDiskAsync().ConfigureAwait(false);
                    Logger.Info($"[Queue] Retrying job {jobId}");
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Removes all jobs with Failed status (user "dismiss all" action).
        /// </summary>
        public async Task RemoveFailedJobsAsync()
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var removed = jobs.RemoveAll(j => j.Status == UploadStatus.Failed);
                if (removed > 0)
                {
                    await SaveQueueToDiskAsync().ConfigureAwait(false);
                }
                Logger.Info($"[Queue] Dismissed {removed} failed jobs");
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Resets the historical counters: zeroes the lifetime completed total and removes the retained
        /// Completed AND terminal Failed records (so "Total uploaded" and "Failed" both read 0).
        /// Clearing the Completed records alongside the total is required because the on-load seed clamps
        /// completedTotal to >= the retained Completed count, so zeroing the total without clearing the
        /// records would let it spring back on the next load. Pending and InProgress jobs are PRESERVED
        /// -- they are live state, not history -- and no source files are deleted.
        /// </summary>
        public async Task ResetCountsAsync()
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var removedCompleted = jobs.RemoveAll(j => j.Status == UploadStatus.Completed);
                var removedFailed = jobs.RemoveAll(j => j.Status == UploadStatus.Failed);
                completedTotal = 0;
                await SaveQueueToDiskAsync().ConfigureAwait(false);
                Logger.Info($"[Queue] Reset counts (removed {removedCompleted} completed, {removedFailed} failed; Pending/InProgress preserved)");
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Gets completed jobs ordered by CompletedAt descending (most recent first).
        /// </summary>
        public async Task<IEnumerable<UploadJob>> GetCompletedJobsAsync()
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                return jobs
                    .Where(j => j.Status == UploadStatus.Completed)
                    .OrderByDescending(j => j.CompletedAt)
                    .ToList();
            }
            finally
            {
                semaphore.Release();
            }
        }

        // ====================================================================
        // Blocked-state machine (storage-quota plan §4 Step 2)
        // ====================================================================

        /// <inheritdoc />
        public async Task<bool> SetBlockAsync(UploadBlockKind kind, Guid failedJobId, UploadBlockSnapshot snapshot, UploadBlockIdentityInput identity)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                // Return the failing job to Pending -- never Failed, nothing discarded, QueuedAt
                // untouched. No retry is scheduled (this is NOT a transient failure) and the job's
                // ErrorMessage/LastFailureReason are left as-is (no user-facing notification path
                // exists yet for a blocked job -- Step 3).
                var job = jobs.FirstOrDefault(j => j.Id == failedJobId);
                if (job != null)
                {
                    job.Status = UploadStatus.Pending;
                    job.NextRetryAfter = null;
                }

                var fingerprint = ComputeFingerprintLocked(identity?.ApiKey);

                currentBlock = new UploadBlockState
                {
                    Kind = kind,
                    Snapshot = new UploadBlockSnapshot
                    {
                        UsedBytes = snapshot?.UsedBytes,
                        LimitBytes = snapshot?.LimitBytes,
                        Flavor = snapshot?.Flavor ?? QuotaBlockFlavor.StorageLimitExceeded,
                        TimestampUtc = DateTime.UtcNow
                    },
                    Identity = new UploadBlockIdentity
                    {
                        EndpointUrl = identity?.EndpointUrl,
                        AccountId = identity?.AccountId,
                        KeyFingerprint = fingerprint
                    },
                    // Storage-quota plan §4 Step 5: persisted so ResumeAsync can pin the SAME job even
                    // after a NINA restart (a fresh UploadManager has no in-memory record of it).
                    FailedJobId = failedJobId
                };
                // Entering a NEW block clears any stale pin left over from a previous latch/resume cycle.
                resumePriorityJobId = null;

                var committed = await SaveQueueToDiskAsync().ConfigureAwait(false);
                if (committed)
                {
                    Logger.Warning($"[Queue] Block set: {kind} (job {failedJobId} returned to Pending)");
                }
                else
                {
                    Logger.Error($"[Queue] Block set ({kind}) but FAILED to persist durably -- in-memory state only");
                }
                return committed;
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <inheritdoc />
        public async Task<bool> ClearBlockAsync(Guid? resumePriorityJobId)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                // Storage-quota plan directive C (the durability-latch P0): capture the pre-attempt
                // state BEFORE mutating so a failed commit can be rolled back. The old code mutated
                // currentBlock/resumePriorityJobId THEN attempted the commit -- on failure it returned
                // false but left the in-memory CurrentBlock already cleared, releasing the durability
                // latch (any reader of CurrentBlock, and a second ClearBlockAsync attempt's own
                // "previous state" capture, would both observe a clear that never reached disk).
                var previousBlock = currentBlock;
                var previousPin = this.resumePriorityJobId;

                currentBlock = UploadBlockState.None;
                this.resumePriorityJobId = resumePriorityJobId;

                var committed = await SaveQueueToDiskAsync().ConfigureAwait(false);
                var pinDesc = resumePriorityJobId.HasValue ? resumePriorityJobId.Value.ToString() : "none";
                if (committed)
                {
                    Logger.Info($"[Queue] Block cleared (resume priority job: {pinDesc})");
                }
                else
                {
                    // Fail-safe: restore BOTH fields to their pre-attempt values -- a failed commit must
                    // never release the durability latch. CurrentBlock keeps reflecting reality (still
                    // blocked, no new pin) until a commit actually succeeds.
                    currentBlock = previousBlock;
                    this.resumePriorityJobId = previousPin;
                    Logger.Error($"[Queue] Block clear requested (resume priority job: {pinDesc}) but FAILED to persist durably -- retaining the previous block state");
                }
                return committed;
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <inheritdoc />
        public async Task<bool> ResetJobToPendingIfInProgressAsync(Guid jobId)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var job = jobs.FirstOrDefault(j => j.Id == jobId);
                if (job == null || job.Status != UploadStatus.InProgress)
                {
                    return true; // nothing to reset -- trivially successful no-op
                }

                job.Status = UploadStatus.Pending;
                // D-verdict fix (item 1): the in-memory reset stays regardless of what happens below --
                // Pending is the safe direction, and a fresh disk load reconverts InProgress -> Pending
                // anyway -- but the SaveQueueToDiskAsync result must no longer be discarded. Log honestly
                // (a warning, never a claimed success) and propagate it so the caller/tests can observe
                // whether the reset actually reached disk.
                var committed = await SaveQueueToDiskAsync().ConfigureAwait(false);
                if (committed)
                {
                    Logger.Info($"[Queue] Job {jobId} cleanly cancelled -- reset InProgress -> Pending");
                }
                else
                {
                    Logger.Warning($"[Queue] Job {jobId} reset InProgress -> Pending in memory, but FAILED to persist durably -- a fresh disk load will still recover the correct state");
                }
                return committed;
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <inheritdoc />
        public async Task<int> ResetAllInProgressToPendingAsync()
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var affected = jobs.Where(j => j.Status == UploadStatus.InProgress).ToList();
                foreach (var job in affected)
                {
                    job.Status = UploadStatus.Pending;
                }

                if (affected.Count > 0)
                {
                    // D-verdict fix (item 1): stop discarding SaveQueueToDiskAsync's result -- the
                    // in-memory reset stays regardless (Pending is the safe direction, and reload
                    // reconverts anyway), but a failed persist must be logged as a warning, never as
                    // success.
                    var committed = await SaveQueueToDiskAsync().ConfigureAwait(false);
                    if (committed)
                    {
                        Logger.Info($"[Queue] Reset {affected.Count} InProgress job(s) to Pending (same-process restart defensive reset)");
                    }
                    else
                    {
                        Logger.Warning($"[Queue] Reset {affected.Count} InProgress job(s) to Pending in memory, but FAILED to persist durably -- a fresh disk load will still recover the correct state");
                    }
                }

                return affected.Count;
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <inheritdoc />
        public async Task<bool> UpgradeBlockAccountIdAsync(string accountId)
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (currentBlock.Kind == UploadBlockKind.None || currentBlock.Identity == null)
                {
                    return true; // nothing latched (or no identity to upgrade) -- treat as a no-op success
                }

                var previousBlock = currentBlock;
                currentBlock = new UploadBlockState
                {
                    Kind = previousBlock.Kind,
                    Snapshot = previousBlock.Snapshot,
                    Identity = new UploadBlockIdentity
                    {
                        EndpointUrl = previousBlock.Identity.EndpointUrl,
                        AccountId = accountId,
                        KeyFingerprint = previousBlock.Identity.KeyFingerprint
                    },
                    FailedJobId = previousBlock.FailedJobId
                };

                var committed = await SaveQueueToDiskAsync().ConfigureAwait(false);
                if (committed)
                {
                    Logger.Info("[Queue] Live block upgraded with a newly-known accountId");
                }
                else
                {
                    // Fail-safe: roll back -- never claim a durability that did not happen.
                    currentBlock = previousBlock;
                    Logger.Error("[Queue] Block accountId upgrade FAILED to persist durably -- retaining the previous identity");
                }
                return committed;
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <inheritdoc />
        public async Task<string> ComputeIdentityFingerprintAsync(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                return null;
            }

            await semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var isNewSalt = identitySalt == null;
                var fingerprint = ComputeFingerprintLocked(apiKey);
                if (isNewSalt)
                {
                    // Persist the freshly-generated salt immediately -- otherwise a later fingerprint
                    // computation (e.g. after a restart, before any block was ever set) would regenerate
                    // a DIFFERENT salt and silently make every future identity comparison "foreign".
                    await SaveQueueToDiskAsync().ConfigureAwait(false);
                }
                return fingerprint;
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Computes the salted HMAC-SHA256 fingerprint of <paramref name="apiKey"/>, lazily generating
        /// the per-installation salt on first use. MUST be called only while already holding
        /// <see cref="semaphore"/> (both call sites -- <see cref="SetBlockAsync"/> and
        /// <see cref="ComputeIdentityFingerprintAsync"/> -- hold it). Returns null for a null/empty key.
        /// </summary>
        private string ComputeFingerprintLocked(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                return null;
            }

            if (identitySalt == null)
            {
                identitySalt = RandomNumberGenerator.GetBytes(32);
            }

            using var hmac = new HMACSHA256(identitySalt);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(apiKey));
            return Convert.ToBase64String(hash);
        }

        /// <summary>
        /// Storage-quota plan directive I (B#10 test-hardening): test-only (InternalsVisibleTo
        /// CloudUploadPlugin.Tests) read of the current per-installation identity salt, exposed with the
        /// NARROWEST possible surface (mirrors <see cref="DpapiEntropyForTests"/>'s pattern) so a test can
        /// independently recompute the expected HMAC-SHA256 and pin the actual security contract
        /// (keyed/salted, not a bare digest) instead of only asserting non-emptiness/non-equality-to-the-
        /// raw-key. Null before the salt has ever been lazily generated (no fingerprint computed yet).
        /// </summary>
        internal byte[] IdentitySaltForTests => identitySalt;

        /// <summary>
        /// Loads and decrypts the queue from disk on startup.
        /// Cleans up any stale .tmp files left from interrupted atomic saves.
        /// </summary>
        private void LoadQueueFromDisk()
        {
            // Clean up stale .tmp file from interrupted atomic save
            var tmpPath = queueFilePath + ".tmp";
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); }
                catch (Exception ex) { Logger.Warning($"[Queue] Failed to clean stale tmp: {ex.Message}"); }
            }

            if (!File.Exists(queueFilePath))
            {
                jobs = new List<UploadJob>();
                return;
            }

            try
            {
                var encryptedBytes = File.ReadAllBytes(queueFilePath);
                var json = DecryptData(encryptedBytes, out var wasLegacyNullEntropy);
                jobs = DeserializeJobs(
                    json,
                    out var persistedCompletedTotal,
                    out var persistedBlock,
                    out var persistedResumePriorityJobId,
                    out var persistedIdentitySalt) ?? new List<UploadJob>();

                // Seed the lifetime running total. v2 files carry the persisted value; pre-v2 files
                // (sentinel -1) seed from the retained completed-job count so the counter continues
                // from current state rather than resetting to 0. Clamp to >= retained completed count
                // so a corrupted/under-counted persisted value can never display fewer than what's on disk.
                var retainedCompleted = jobs.Count(j => j.Status == UploadStatus.Completed);
                completedTotal = persistedCompletedTotal < 0
                    ? retainedCompleted
                    : Math.Max(persistedCompletedTotal, retainedCompleted);

                // v3 additive fields (storage-quota plan §4 Step 2) -- absent on v1/v2 files, in which
                // case DeserializeJobs already defaulted them to "not blocked" / no pin / no salt.
                currentBlock = persistedBlock ?? UploadBlockState.None;
                resumePriorityJobId = persistedResumePriorityJobId;
                identitySalt = persistedIdentitySalt;

                // Reset any InProgress jobs to Pending (interrupted by shutdown)
                foreach (var job in jobs.Where(j => j.Status == UploadStatus.InProgress))
                {
                    job.Status = UploadStatus.Pending;
                }

                Logger.Info($"[Queue] Loaded {jobs.Count} jobs from disk (completed total: {completedTotal})");

                // DPAPI-entropy migration-on-read: a legacy null-entropy queue.dat just decrypted via the
                // null fallback. Rewrite it once with entropy through the existing durable migration seam
                // so the next load uses entropy. Logged WITHOUT any job contents.
                if (wasLegacyNullEntropy)
                {
                    SaveQueueToDiskSync();
                    Logger.Info("[Queue] Migrated legacy null-entropy queue file to entropy-protected storage");
                }
            }
            catch (Exception ex)
            {
                // Corrupt / undecryptable queue.dat. NEVER silently wipe it: quarantine the bad
                // file to queue.dat.corrupt-<UtcNow> so the user can MANUALLY inspect/restore it,
                // surface a warning, and continue with an EMPTY live queue. We do NOT auto-restore
                // from the quarantine or the .bak -- recovery is a deliberate user action.
                Logger.Error($"[Queue] Failed to load queue (corrupt/undecryptable): {ex.Message}");
                QuarantineCorruptQueue();
                jobs = new List<UploadJob>();
            }
        }

        /// <summary>
        /// Renames a corrupt/undecryptable queue.dat to queue.dat.corrupt-&lt;UtcNow&gt; (forensic
        /// quarantine, manual-restore-only) and records the warning. The existing .bak (last-good
        /// rollback artifact) is left untouched. Never auto-restores.
        /// </summary>
        private void QuarantineCorruptQueue()
        {
            try
            {
                if (!File.Exists(queueFilePath))
                {
                    return;
                }

                var corruptName = $"queue.dat.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssZ}";
                var corruptPath = Path.Combine(Path.GetDirectoryName(queueFilePath) ?? string.Empty, corruptName);

                // Avoid clobbering an existing quarantine file from the same second.
                int suffix = 1;
                while (File.Exists(corruptPath))
                {
                    corruptName = $"queue.dat.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{suffix++}";
                    corruptPath = Path.Combine(Path.GetDirectoryName(queueFilePath) ?? string.Empty, corruptName);
                }

                File.Move(queueFilePath, corruptPath);
                HasCorruptQueueWarning = true;
                CorruptQueueFileName = corruptName;
                Logger.Warning($"[Queue] Quarantined corrupt queue to {corruptName} (manual-restore-only; live queue reset to empty)");
            }
            catch (Exception ex)
            {
                // If quarantine itself fails we still must not lose the file silently: leave it in place
                // and surface the warning so the user is alerted to investigate manually.
                Logger.Error($"[Queue] Failed to quarantine corrupt queue: {ex.Message}");
                HasCorruptQueueWarning = true;
                CorruptQueueFileName = Path.GetFileName(queueFilePath);
            }
        }

        /// <summary>
        /// Encrypts and durably persists the queue to disk. The payload is flushed to the physical
        /// disk (fsync) BEFORE the atomic rename, so a power loss between write and rename cannot
        /// leave a zeroed/partial queue.dat. A .bak of the previous good queue is preserved as a
        /// rollback artifact, and any .corrupt-&lt;ts&gt; quarantine is never touched.
        /// Returns whether the commit durably succeeded (storage-quota plan §4 Step 2: SetBlockAsync /
        /// ClearBlockAsync / ComputeIdentityFingerprintAsync observe this to report a fail-safe
        /// "not durable" outcome instead of claiming persistence that did not happen). Pre-existing call
        /// sites that predate this change simply await the call as a statement and discard the result,
        /// preserving their exact original (best-effort, log-only-on-failure) behavior.
        /// </summary>
        private async Task<bool> SaveQueueToDiskAsync()
        {
            byte[] encryptedBytes;
            try
            {
                var json = SerializeJobs();
                encryptedBytes = EncryptData(json);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Queue] Failed to serialize queue: {ex.Message}");
                return false;
            }

            // Durable write to .tmp (fsync before close).
            var tmpPath = queueFilePath + ".tmp";
            try
            {
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await fs.WriteAsync(encryptedBytes, 0, encryptedBytes.Length).ConfigureAwait(false);
                    await fs.FlushAsync().ConfigureAwait(false);
                    fs.Flush(flushToDisk: true); // fsync: force OS buffers to the physical disk before rename
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Queue] Failed to write queue tmp: {ex.Message}");
                return false;
            }

            return await Task.Run(() => CommitTmpToQueue(tmpPath)).ConfigureAwait(false);
        }

        /// <summary>
        /// Synchronous durable version for migration use. Same fsync-before-rename guarantee.
        /// </summary>
        private void SaveQueueToDiskSync()
        {
            byte[] encryptedBytes;
            try
            {
                var json = SerializeJobs();
                encryptedBytes = EncryptData(json);
            }
            catch (Exception ex)
            {
                Logger.Error($"[Queue] Failed to serialize queue: {ex.Message}");
                return;
            }

            var tmpPath = queueFilePath + ".tmp";
            try
            {
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(encryptedBytes, 0, encryptedBytes.Length);
                    fs.Flush(flushToDisk: true); // fsync before rename
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Queue] Failed to write queue tmp: {ex.Message}");
                return;
            }

            CommitTmpToQueue(tmpPath);
        }

        /// <summary>
        /// Promotes a fully-flushed queue.dat.tmp to queue.dat: snapshots the previous good queue.dat
        /// to queue.dat.bak (rollback artifact), then atomically renames .tmp -> queue.dat. Quarantine
        /// (.corrupt-&lt;ts&gt;) files have distinct names and are never affected.
        /// Returns true only when the rename onto queue.dat actually succeeded.
        /// </summary>
        private bool CommitTmpToQueue(string tmpPath)
        {
            try
            {
                // Preserve the last-good queue as a .bak rollback artifact (distinct from the
                // forensic .corrupt-<ts> quarantine).
                if (File.Exists(queueFilePath))
                {
                    var bakPath = queueFilePath + ".bak";
                    try { File.Copy(queueFilePath, bakPath, overwrite: true); }
                    catch (Exception bakEx) { Logger.Warning($"[Queue] Failed to refresh .bak: {bakEx.Message}"); }
                }

                try
                {
                    File.Move(tmpPath, queueFilePath, overwrite: true);
                    return true;
                }
                catch (IOException)
                {
                    // Retry once after brief delay (e.g., antivirus lock)
                    Thread.Sleep(150);
                    try
                    {
                        File.Move(tmpPath, queueFilePath, overwrite: true);
                        return true;
                    }
                    catch (IOException retryEx)
                    {
                        Logger.Warning($"[Queue] Atomic rename failed after retry: {retryEx.Message}");
                        // Original queue.dat remains intact
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[Queue] Failed to commit queue: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Serializes the in-memory job list as a versioned {SchemaVersion, Jobs} envelope with
        /// statuses written by name (StringEnumConverter).
        ///
        /// CR-01: a job currently uploading has its <see cref="UploadJob.ChunksSent"/> mutated by
        /// bounded-parallel chunk tasks (Add() under <see cref="UploadJob.ChunksSentLock"/>). Newtonsoft
        /// would enumerate that live list during the JSON write, which can throw
        /// "Collection was modified" or emit a torn snapshot. We therefore stabilize each job's
        /// ChunksSent by replacing it -- UNDER the job's own lock -- with a fresh copy of itself. The
        /// copy is a brand-new reference that the serializer enumerates exclusively; any concurrent
        /// RecordChunkSent re-reads job.ChunksSent under the same lock and appends to this stable copy,
        /// but the append is serialized against this swap, so the serializer never observes a mutation
        /// mid-enumeration of the reference it captured before the write began.
        /// </summary>
        private string SerializeJobs()
        {
            // CR-01: snapshot each job's ChunksSent under its own lock into a list the live upload
            // path will NEVER enumerate, then serialize from these snapshots. This guarantees the
            // serializer never enumerates a list that a concurrent RecordChunkSent is Add()-ing to.
            var serializer = JsonSerializer.Create(QueueJsonSettings);
            var jobsArray = new JArray();
            foreach (var job in jobs)
            {
                // Hold the job's own lock across BOTH the FromObject enumeration (which would otherwise
                // read the live ChunksSent list) AND the snapshot overwrite, so a concurrent
                // RecordChunkSent (also under this lock) cannot Add() mid-enumeration. The lock is
                // per-job and the projection is fast, so contention with the upload path is minimal.
                JObject jobObj;
                lock (job.ChunksSentLock)
                {
                    var chunksSnapshot = job.ChunksSent == null ? new List<int>() : new List<int>(job.ChunksSent);
                    jobObj = JObject.FromObject(job, serializer);
                    jobObj["ChunksSent"] = JArray.FromObject(chunksSnapshot, serializer);
                }
                jobsArray.Add(jobObj);
            }

            var envelopeObj = new JObject
            {
                ["SchemaVersion"] = CurrentSchemaVersion,
                ["CompletedTotal"] = completedTotal,
                ["Jobs"] = jobsArray,
                // v3 additive fields (storage-quota plan §4 Step 2). Block is omitted (null) when not
                // blocked -- keeps the common (never-blocked) on-disk shape unchanged from v2 aside from
                // these three explicit-null/absent-equivalent keys.
                ["Block"] = currentBlock.Kind == UploadBlockKind.None
                    ? null
                    : JObject.FromObject(currentBlock, serializer),
                ["ResumePriorityJobId"] = resumePriorityJobId.HasValue
                    ? (JToken)resumePriorityJobId.Value.ToString()
                    : JValue.CreateNull(),
                ["IdentitySalt"] = identitySalt != null
                    ? (JToken)Convert.ToBase64String(identitySalt)
                    : JValue.CreateNull()
            };
            return envelopeObj.ToString(Formatting.None);
        }

        /// <summary>
        /// Deserializes persisted queue JSON, accepting both the v1 {SchemaVersion, Jobs} envelope and
        /// the legacy bare-array format (auto-migrated on the next save). Unknown/removed status names
        /// fall back tolerantly to Pending so a single bad value never corrupts adjacent jobs.
        /// Returns null only when the JSON is structurally unusable (caller treats that as corruption).
        /// <paramref name="block"/>/<paramref name="resumePriorityJobId"/>/<paramref name="identitySalt"/>
        /// are v3 additive fields (storage-quota plan §4 Step 2, point 2): absent on v1/v2 files, in
        /// which case they default to "not blocked" / no pin / no salt -- a v2 file loads exactly as it
        /// did before this change.
        /// </summary>
        private static List<UploadJob> DeserializeJobs(
            string json,
            out int completedTotal,
            out UploadBlockState block,
            out Guid? resumePriorityJobId,
            out byte[] identitySalt)
        {
            // -1 sentinel = "absent" (pre-v2 file). The caller seeds the running total from the
            // retained completed-job count in that case so the counter continues from current state.
            completedTotal = -1;
            block = UploadBlockState.None;
            resumePriorityJobId = null;
            identitySalt = null;
            var root = JToken.Parse(json);

            JToken jobsToken;
            if (root.Type == JTokenType.Object)
            {
                // v1/v2/v3 envelope: { "SchemaVersion": N, "CompletedTotal"?: M, "Jobs": [...],
                // "Block"?: {...}, "ResumePriorityJobId"?: "guid", "IdentitySalt"?: "base64" }
                jobsToken = root["Jobs"] ?? new JArray();
                if (root["CompletedTotal"] is JValue ctVal && ctVal.Type == JTokenType.Integer)
                {
                    completedTotal = ctVal.Value<int>();
                }

                if (root["Block"] is JObject blockObj)
                {
                    block = ParseBlock(blockObj);
                }

                if (root["ResumePriorityJobId"] is JValue pinVal
                    && pinVal.Type == JTokenType.String
                    && Guid.TryParse(pinVal.Value<string>(), out var pinnedId))
                {
                    resumePriorityJobId = pinnedId;
                }

                if (root["IdentitySalt"] is JValue saltVal && saltVal.Type == JTokenType.String)
                {
                    try
                    {
                        identitySalt = Convert.FromBase64String(saltVal.Value<string>());
                    }
                    catch (FormatException)
                    {
                        // Corrupt salt -- treated as absent; a fresh salt is generated and persisted on
                        // next use. This orphans any EXISTING block's fingerprint comparison (it will
                        // read as "foreign" and clear), which is the safe failure mode.
                        Logger.Warning("[Queue] Persisted identity salt was malformed -- will regenerate on next use");
                        identitySalt = null;
                    }
                }
            }
            else if (root.Type == JTokenType.Array)
            {
                // Legacy bare-array format -> will be re-saved as the versioned envelope on next write.
                jobsToken = root;
                Logger.Info("[Queue] Migrating legacy bare-array queue to versioned envelope");
            }
            else
            {
                return null;
            }

            // Tolerant enum handling: sanitize unknown status names to a known fallback BEFORE binding,
            // so deserialization never throws and adjacent fields are not shifted.
            if (jobsToken is JArray jobsArray)
            {
                var validNames = new HashSet<string>(Enum.GetNames(typeof(UploadStatus)), StringComparer.Ordinal);
                foreach (var item in jobsArray)
                {
                    if (item is JObject obj && obj["Status"] is JValue statusVal)
                    {
                        // Legacy files persisted Status as an INTEGER; new files persist it by name.
                        // Integers that map to a defined enum member are valid -- leave them alone so
                        // the StringEnumConverter/int binding resolves them. Only string names that are
                        // NOT defined members are unknown/removed and fall back tolerantly.
                        if (statusVal.Type == JTokenType.Integer)
                        {
                            var intVal = statusVal.Value<long>();
                            if (!Enum.IsDefined(typeof(UploadStatus), (int)intVal))
                            {
                                Logger.Warning($"[Queue] Unknown/removed job status value '{intVal}' -> defaulting to Pending");
                                obj["Status"] = UploadStatus.Pending.ToString();
                            }
                        }
                        else
                        {
                            var name = statusVal.Value?.ToString();
                            if (string.IsNullOrEmpty(name) || !validNames.Contains(name))
                            {
                                Logger.Warning($"[Queue] Unknown/removed job status '{name}' -> defaulting to Pending");
                                obj["Status"] = UploadStatus.Pending.ToString();
                            }
                        }
                    }
                }
            }

            return jobsToken.ToObject<List<UploadJob>>(JsonSerializer.Create(QueueJsonSettings))
                   ?? new List<UploadJob>();
        }

        /// <summary>
        /// Parses the persisted "Block" sub-object into an <see cref="UploadBlockState"/>. Never throws
        /// out to the caller -- a malformed Block object (unknown/missing Kind, corrupt sub-object) falls
        /// back to <see cref="UploadBlockState.None"/> so the WHOLE queue load still succeeds; only the
        /// block restoration is lost, which is a safer failure mode than refusing to load pending jobs.
        /// </summary>
        private static UploadBlockState ParseBlock(JObject blockObj)
        {
            try
            {
                var kindToken = blockObj["Kind"];
                var kindName = kindToken?.Type == JTokenType.String ? kindToken.Value<string>() : null;

                if (kindName == null || !Enum.TryParse<UploadBlockKind>(kindName, out var kind)
                    || !Enum.IsDefined(typeof(UploadBlockKind), kind))
                {
                    if (kindName != null)
                    {
                        Logger.Warning($"[Queue] Unknown/removed block kind '{kindName}' -> treating as no block");
                    }
                    return UploadBlockState.None;
                }

                if (kind == UploadBlockKind.None)
                {
                    return UploadBlockState.None;
                }

                var snapshotObj = blockObj["Snapshot"] as JObject;
                var identityObj = blockObj["Identity"] as JObject;

                // Storage-quota plan §4 Step 5: additive field -- absent on a pre-Step-5 v3 file, in
                // which case it stays null and resume falls back to no pin (normal drain order).
                Guid? failedJobId = null;
                if (blockObj["FailedJobId"] is JValue failedJobIdVal
                    && failedJobIdVal.Type == JTokenType.String
                    && Guid.TryParse(failedJobIdVal.Value<string>(), out var parsedFailedJobId))
                {
                    failedJobId = parsedFailedJobId;
                }

                // Pass H P1: the Flavor enum must be normalized BEFORE ToObject -- an unknown string
                // (a future additive flavor, or a downgrade reading a newer file) would make ToObject
                // throw, and the catch below would convert the WHOLE block to None, releasing the
                // stop-queue latch. Unknown values must degrade to the default flavor, never unlatch.
                if (snapshotObj?["Flavor"] is JToken flavorToken)
                {
                    // Pass I: normalization must be TOTAL. Value<long>() itself throws on a JSON
                    // integer beyond Int64 (BigInteger), and an unchecked long->int cast wraps
                    // (4294967297 -> 1 -> PlanRequired). Any token this cannot decode safely --
                    // including its own decode throwing -- degrades to the default flavor; it must
                    // never reach the outer catch-all that would drop the whole block (unlatch).
                    var normalized = QuotaBlockFlavor.StorageLimitExceeded;
                    try
                    {
                        if (flavorToken.Type == JTokenType.String
                            && Enum.TryParse<QuotaBlockFlavor>(flavorToken.Value<string>(), ignoreCase: false, out var parsedFlavor)
                            && Enum.IsDefined(typeof(QuotaBlockFlavor), parsedFlavor))
                        {
                            normalized = parsedFlavor;
                        }
                        else if (flavorToken.Type == JTokenType.Integer)
                        {
                            var raw = flavorToken.Value<long>();
                            if (raw >= 0 && raw <= int.MaxValue && Enum.IsDefined(typeof(QuotaBlockFlavor), (int)raw))
                            {
                                normalized = (QuotaBlockFlavor)(int)raw;
                            }
                        }
                    }
                    catch
                    {
                        // Undecodable token (e.g. BigInteger overflow) -> keep the default flavor.
                    }
                    snapshotObj["Flavor"] = normalized.ToString();
                }

                return new UploadBlockState
                {
                    Kind = kind,
                    Snapshot = snapshotObj?.ToObject<UploadBlockSnapshot>(),
                    Identity = identityObj?.ToObject<UploadBlockIdentity>(),
                    FailedJobId = failedJobId
                };
            }
            catch (Exception ex)
            {
                Logger.Warning($"[Queue] Failed to parse persisted block state -> treating as no block: {ex.Message}");
                return UploadBlockState.None;
            }
        }

        // ====================================================================
        // Queue intelligence (retention, stale detection)
        // ====================================================================

        /// <summary>
        /// Removes completed jobs beyond the retention limit (keeps most recent 50).
        /// Called after a job transitions to Completed.
        /// </summary>
        private void PurgeExcessCompletedJobs()
        {
            var excess = jobs
                .Where(j => j.Status == UploadStatus.Completed)
                .OrderByDescending(j => j.CompletedAt)
                .Skip(MaxCompletedJobsRetained)
                .ToList();
            foreach (var old in excess)
            {
                jobs.Remove(old);
            }
        }

        /// <summary>
        /// Auto-prunes terminal Failed jobs (D-06): drops any Failed job older than 7 days, then caps
        /// the remaining Failed jobs at the newest 200 entries (whichever triggers first). Jobs with a
        /// null FailedAt (not yet timestamped) are treated as recent and only subject to the cap.
        /// Called on each Failed transition and once at load.
        /// </summary>
        private void PruneExcessFailedJobs()
        {
            var now = DateTime.UtcNow;

            // 1. Age-out: remove Failed jobs whose FailedAt is older than the max age.
            var aged = jobs
                .Where(j => j.Status == UploadStatus.Failed
                            && j.FailedAt.HasValue
                            && (now - j.FailedAt.Value) > FailedJobMaxAge)
                .ToList();
            foreach (var old in aged)
            {
                jobs.Remove(old);
            }

            // 2. Cap: keep only the newest MaxFailedJobsRetained Failed jobs.
            var excess = jobs
                .Where(j => j.Status == UploadStatus.Failed)
                .OrderByDescending(j => j.FailedAt ?? DateTime.MaxValue)
                .Skip(MaxFailedJobsRetained)
                .ToList();
            foreach (var old in excess)
            {
                jobs.Remove(old);
            }

            if (aged.Count > 0 || excess.Count > 0)
            {
                Logger.Info($"[Queue] Pruned Failed jobs: {aged.Count} aged-out (>7d), {excess.Count} over cap (200)");
            }
        }

        /// <summary>
        /// Detects if the queue has stale jobs from a previous session.
        /// A queue is stale when the oldest pending/failed job is older than 6 hours.
        /// Called once at startup after loading the queue from disk.
        /// </summary>
        private void DetectStaleQueue()
        {
            var pendingOrFailed = jobs.Where(j =>
                j.Status == UploadStatus.Pending || j.Status == UploadStatus.Failed).ToList();
            if (pendingOrFailed.Count == 0) return;
            var oldest = pendingOrFailed.Min(j => j.QueuedAt);
            var age = DateTime.UtcNow - oldest;
            if (age.TotalHours > 6)
            {
                IsStaleQueue = true;
                StaleJobCount = pendingOrFailed.Count;
                StaleQueueAge = age;
                Logger.Warning($"[Queue] Stale queue detected: {pendingOrFailed.Count} jobs, oldest from {oldest:u}");
            }
        }

        // ====================================================================
        // DPAPI Encryption (CurrentUser scope + app-specific entropy)
        // ====================================================================

        /// <summary>
        /// App-specific DPAPI optional entropy for queue.dat (SC2 / DPAPI-entropy). Combined with the
        /// CurrentUser scope, this raises the bar above a null-entropy file: the same Windows user must
        /// ALSO supply this app constant to decrypt. Honest residual: this is NOT a defense against
        /// malware or arbitrary code already running as the same Windows user (T-19-13). This constant
        /// is part of the on-disk format -- it must stay stable forever; changing it orphans existing
        /// queue.dat files (they migrate-on-read only while the old/new entropy fallback chain holds, and
        /// here we only fall back to the historical null entropy). It is deliberately decoupled from
        /// AuthManager's entropy (no cross-file coupling).
        /// </summary>
        private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("Astrovault.NINA.Queue.v1");

        /// <summary>
        /// Test-only (InternalsVisibleTo CloudUploadPlugin.Tests): lets QueueDpapiEntropyTests recover the
        /// repository's exact plaintext to build a legacy null-entropy fixture without hand-rolling the
        /// QueueEnvelope JSON. Exposes the entropy with the NARROWEST possible surface -- it is never made
        /// public, so production encapsulation is preserved.
        /// </summary>
        internal static byte[] DpapiEntropyForTests => AppEntropy;

        /// <summary>
        /// Encrypts data using Windows DPAPI (CurrentUser scope) with app-specific entropy. The transient
        /// plaintext byte[] is zeroed with Array.Clear after Protect returns (T-19-11); the immutable
        /// source string is an unavoidable residual.
        /// </summary>
        private static byte[] EncryptData(string plainText)
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            try
            {
                return ProtectedData.Protect(plainBytes, AppEntropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                Array.Clear(plainBytes, 0, plainBytes.Length);
            }
        }

        /// <summary>
        /// Decrypts data using Windows DPAPI (CurrentUser scope) with migration-on-read. New files are
        /// written with <see cref="AppEntropy"/>; a legacy null-entropy file written before entropy
        /// existed throws <see cref="CryptographicException"/> on the entropy attempt, so we retry once
        /// with null entropy and set <paramref name="wasLegacyNullEntropy"/> so the caller can rewrite it
        /// with entropy via the existing migration seam.
        /// <para>
        /// IMPORTANT (no third behavior): a TRUE DPAPI scope loss (Windows account reset/reinstall) or a
        /// genuinely corrupt/foreign blob makes BOTH the entropy AND the null-entropy Unprotect throw.
        /// That is intentional -- the exception propagates to <see cref="LoadQueueFromDisk"/>'s existing
        /// <c>catch (Exception)</c> -&gt; <see cref="QuarantineCorruptQueue"/> path. We do NOT swallow it
        /// or invent new silent behavior (T-19-12 is accepted: manual-restore-only quarantine).
        /// </para>
        /// The transient decrypted byte[] is zeroed with Array.Clear after the string is produced
        /// (T-19-11); the immutable returned string is an unavoidable residual.
        /// </summary>
        private static string DecryptData(byte[] encryptedBytes, out bool wasLegacyNullEntropy)
        {
            wasLegacyNullEntropy = false;
            byte[] plainBytes;
            try
            {
                plainBytes = ProtectedData.Unprotect(encryptedBytes, AppEntropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // Legacy file written before entropy existed -> retry once with null entropy and flag
                // for rewrite. If THIS also throws (true scope loss / corruption), it propagates to the
                // existing quarantine path -- deliberately, no third behavior.
                plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                wasLegacyNullEntropy = true;
            }

            try
            {
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                Array.Clear(plainBytes, 0, plainBytes.Length);
            }
        }
    }
}
