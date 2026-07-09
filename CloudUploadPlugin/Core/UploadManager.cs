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
        private readonly IAuthManager authManager;
        private readonly CircuitBreaker circuitBreaker = new CircuitBreaker();
        private readonly SemaphoreSlim lifecycleLock = new SemaphoreSlim(1, 1);

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
        public void UpdateApiClient(ICloudApiClient newClient)
        {
            apiClient = newClient ?? throw new ArgumentNullException(nameof(newClient));
            Logger.Info("[UploadManager] API client updated for new endpoint");
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
                await InitializeCountsAsync().ConfigureAwait(false);
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
            await lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsRunning)
                {
                    return;
                }

                cts?.Cancel();
            }
            finally
            {
                lifecycleLock.Release();
            }

            try
            {
                await processingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
            }

            Logger.Info("[UploadManager] Stopped");
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
                        await Task.Delay(1000, cancellationToken).ConfigureAwait(false); // poll interval when idle
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

            try
            {
                var result = await apiClient.UploadFileAsync(
                    job,
                    progress,
                    cancellationToken).ConfigureAwait(false);

                if (result.Success)
                {
                    await HandleUploadSuccess(job).ConfigureAwait(false);
                }
                else
                {
                    await HandleUploadFailure(job, result, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // REL-04: Clean cancellation/shutdown is NOT a transient failure.
                // Do not increment the retry count or feed the circuit breaker --
                // leave the job Pending (reset on next load) and just unwind the
                // in-flight counter so it doesn't drift.
                Interlocked.Decrement(ref uploadingCount);
                Logger.Info($"[UploadManager] Upload cancelled (clean shutdown): {LogSanitizer.MaskPath(job.LocalPath)}");
                throw;
            }
            catch (Exception ex)
            {
                await HandleUploadFailure(job, UploadResult.Fail(ex.Message), cancellationToken).ConfigureAwait(false);
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
            // Permanent failures skip retries entirely (e.g., file not found, access denied).
            // REL-06/D-12: mark the terminal failure as PERMANENT (excluded from auto-promotion --
            // never retried forever) and persist LastFailureReason for the existing notification path.
            if (result.IsPermanent)
            {
                await queueRepository.MarkFailedAsync(job.Id, result.ErrorMessage, isPermanent: true).ConfigureAwait(false);
                Interlocked.Decrement(ref uploadingCount);
                Interlocked.Decrement(ref pendingCount);
                Interlocked.Increment(ref failedCount);
                await UpdateCapacityWarning().ConfigureAwait(false);
                Logger.Error($"[UploadManager] Permanent failure (no retry): {LogSanitizer.MaskPath(job.LocalPath)} - {result.ErrorMessage}");
                // D-12: surface the persisted last-failure reason via the EXISTING notification path
                // (no new UI surface). LastFailureReason == result.ErrorMessage here; prefer the
                // persisted field so the notification reflects exactly what was stored as history.
                OnUploadCompleted(job, false, job.LastFailureReason ?? result.ErrorMessage);
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
                OnUploadCompleted(job, false, job.LastFailureReason ?? result.ErrorMessage);
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

        private void OnUploadCompleted(UploadJob job, bool success, string errorMessage)
        {
            var args = new UploadCompletedEventArgs
            {
                JobId = job.Id,
                Success = success,
                FileName = System.IO.Path.GetFileName(job.LocalPath),
                ErrorMessage = errorMessage
            };

            // SEC-07: isolate each subscriber so a throwing handler cannot stall the queue.
            RaiseEvent(UploadCompleted, args);
        }
    }
}
