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
        /// Updates the API client reference used for uploads.
        /// Called when ApiEndpoint changes and services are reinitialized.
        /// Caller must stop the manager first to ensure no in-flight uploads.
        /// </summary>
        /// <param name="newClient">The new API client with updated BaseAddress.</param>
        void UpdateApiClient(ICloudApiClient newClient);
    }
}
