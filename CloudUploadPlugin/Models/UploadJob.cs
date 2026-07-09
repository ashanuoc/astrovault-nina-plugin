using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Astrovault.Models
{
    /// <summary>
    /// Represents the current state of an upload job in the queue.
    /// </summary>
    public enum UploadStatus
    {
        /// <summary>Job is waiting to be processed.</summary>
        Pending,

        /// <summary>Job is currently being uploaded.</summary>
        InProgress,

        /// <summary>Job completed successfully.</summary>
        Completed,

        /// <summary>Job failed after all retry attempts.</summary>
        Failed
        // NOTE: A 'Cancelled' value previously existed but was never assigned by any production
        // writer (D-07). It was removed in phase 18.1. Because the queue now persists statuses by
        // NAME (StringEnumConverter), legacy files that contain "Cancelled" deserialize tolerantly
        // to the default fallback rather than corrupting adjacent enum values.
    }

    /// <summary>
    /// Data transfer object representing a file queued for upload to Astrovault.
    /// This object is serialized to JSON for persistence in the upload queue.
    /// </summary>
    public class UploadJob
    {
        /// <summary>Unique identifier for this upload job.</summary>
        public Guid Id { get; set; }

        /// <summary>Full local path to the image file (e.g., D:\Astro\M31\Light\001.fits).</summary>
        public string LocalPath { get; set; }

        /// <summary>Relative path for cloud storage (e.g., M31/Light/001.fits).</summary>
        public string RelativePath { get; set; }

        /// <summary>File size in bytes.</summary>
        public long FileSize { get; set; }

        /// <summary>When the image was captured by N.I.N.A.</summary>
        public DateTime CapturedAt { get; set; }

        /// <summary>When this job was added to the upload queue.</summary>
        public DateTime QueuedAt { get; set; }

        /// <summary>Current status of the upload job.</summary>
        public UploadStatus Status { get; set; }

        /// <summary>Number of upload retry attempts made.</summary>
        public int RetryCount { get; set; }

        /// <summary>Error message if the upload failed.</summary>
        public string ErrorMessage { get; set; }

        // ====================================================================
        // Image metadata from N.I.N.A. (useful for organizing in cloud)
        // ====================================================================

        /// <summary>Filter used (e.g., "L", "R", "G", "B", "Ha").</summary>
        public string Filter { get; set; }

        /// <summary>Exposure duration in seconds.</summary>
        public double Duration { get; set; }

        /// <summary>File type (e.g., "FITS", "XISF", "TIFF").</summary>
        public string FileType { get; set; }

        /// <summary>
        /// Full metadata JSON extracted from NINA at queue time.
        /// Serialized ImageMetadataDto for API upload.
        /// </summary>
        public string MetadataJson { get; set; }

        // ====================================================================
        // Chunked upload resume state (Phase 5)
        // ====================================================================

        /// <summary>Server-assigned upload session ID. Null for small files or new uploads.</summary>
        public string UploadSessionId { get; set; }

        /// <summary>
        /// List of chunk indices successfully uploaded. Empty for new uploads.
        /// CR-01/SEC-02: this list is aliased across components (the API client mutates it from
        /// bounded-parallel chunk tasks, the repo persists it, the UI reads its count). EVERY read,
        /// write, count, and serialization-time enumeration of this field MUST be performed while
        /// holding <see cref="ChunksSentLock"/> -- it is the single cross-component monitor that makes
        /// the otherwise-racing accesses safe.
        /// </summary>
        public List<int> ChunksSent { get; set; } = new List<int>();

        /// <summary>
        /// CR-01/SEC-02: the cross-component monitor guarding <see cref="ChunksSent"/>. Lives on the
        /// job so the API client, the queue repository, and the progress reporter all lock the SAME
        /// object (the previous design had each component using its own lock, so a list-reference swap
        /// in the repo could race a sibling's Add()). Not serialized; re-created fresh on deserialize.
        /// </summary>
        [JsonIgnore]
        public object ChunksSentLock { get; } = new object();

        /// <summary>Total number of chunks. 0 for small files.</summary>
        public int TotalChunks { get; set; }

        /// <summary>Number of session auto-restarts due to expiry. Capped at MaxSessionRestarts (10, D-10).</summary>
        public int SessionRestartCount { get; set; }

        // ====================================================================
        // Queue hardening state (Phase 7)
        // ====================================================================

        /// <summary>When the job permanently failed (all retries exhausted or permanent error).</summary>
        public DateTime? FailedAt { get; set; }

        /// <summary>When the job completed successfully. Used for completed job retention.</summary>
        public DateTime? CompletedAt { get; set; }

        // ====================================================================
        // Non-blocking backoff + auto-recovery state (Phase 18.1, REL-06/REL-07/D-12)
        // ====================================================================

        /// <summary>
        /// Earliest UTC time this job is eligible to be retried. Replaces the old blocking
        /// <c>Task.Delay</c> backoff: <see cref="UploadStatus.Pending"/> jobs whose NextRetryAfter is in
        /// the future are skipped by the queue's not-yet-due filter (no head-of-line block, no frozen
        /// loop). Null means "immediately due". Cleared (set null) on auto-promotion and on manual retry
        /// so the promoted job is processed right away. Deserializes to null on old files (backward
        /// compatible with the schema-versioned envelope).
        /// </summary>
        public DateTime? NextRetryAfter { get; set; }

        /// <summary>
        /// Diagnostic last-failure reason (D-12). Set whenever the job reaches terminal Failed and
        /// surfaced through the EXISTING error-notification path (no new UI surface). Preserved as
        /// history across auto-promotion and manual retry -- it is overwritten only by a subsequent
        /// failure, never cleared on promotion. Deserializes to null on old files.
        /// </summary>
        public string LastFailureReason { get; set; }

        /// <summary>
        /// Transient-vs-permanent failure marker (REL-06). True when the job's terminal Failed state
        /// came from a PERMANENT fault (<see cref="UploadResult.FailPermanent"/> -- e.g. file not found,
        /// access denied, too many session restarts). Permanent-failed jobs are NEVER auto-promoted on a
        /// half-open probe and are never retried forever. False (the default) marks a TRANSIENT failure
        /// (retry budget exhausted under transient/Open-state faults) that IS auto-promotable. Deserializes
        /// to false on old files.
        /// </summary>
        public bool IsPermanentFailure { get; set; }

    }
}
