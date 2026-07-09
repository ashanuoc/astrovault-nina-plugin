using System;
using System.Collections.Generic;
using Astrovault.Models;

namespace CloudUploadPlugin.Tests.Helpers
{
    /// <summary>
    /// Shared test helpers for creating mock NINA objects and test data.
    /// Provides factory methods for constructing UploadJob instances,
    /// sample file paths, and supported extension lists.
    /// </summary>
    public static class TestDataFactory
    {
        /// <summary>
        /// All file extensions supported by the capture pipeline.
        /// Covers FITS, XISF, TIFF (both extensions), PNG, and JPEG (both extensions).
        /// </summary>
        public static readonly string[] SupportedExtensions = new[]
        {
            ".fits", ".xisf", ".tiff", ".tif", ".png", ".jpg", ".jpeg"
        };

        /// <summary>
        /// Creates an UploadJob with sensible defaults for testing.
        /// All fields are populated with realistic values.
        /// </summary>
        /// <param name="localPath">Full local path to the image file.</param>
        /// <param name="status">Upload status (default: Pending).</param>
        /// <param name="retryCount">Number of retry attempts (default: 0).</param>
        /// <returns>A fully populated UploadJob instance.</returns>
        public static UploadJob CreateUploadJob(
            string localPath = @"D:\Astro\M31\Light\001.fits",
            UploadStatus status = UploadStatus.Pending,
            int retryCount = 0)
        {
            var extension = System.IO.Path.GetExtension(localPath)?.TrimStart('.').ToUpperInvariant() ?? "FITS";

            return new UploadJob
            {
                Id = Guid.NewGuid(),
                LocalPath = localPath,
                RelativePath = "M31/Light/" + System.IO.Path.GetFileName(localPath),
                FileSize = 10_485_760, // 10 MB
                CapturedAt = new DateTime(2026, 3, 11, 21, 0, 0, DateTimeKind.Utc),
                QueuedAt = DateTime.UtcNow,
                Status = status,
                RetryCount = retryCount,
                Filter = "L",
                Duration = 300.0,
                FileType = extension,
                MetadataJson = "{}"
            };
        }

        /// <summary>
        /// Creates a realistic sample file path for the given extension.
        /// Simulates a typical NINA image save path on Windows.
        /// </summary>
        /// <param name="extension">File extension including the dot (e.g., ".fits").</param>
        /// <returns>A realistic absolute file path.</returns>
        public static string CreateSamplePath(string extension)
        {
            return $@"D:\Astro\M31\Light\001{extension}";
        }

        /// <summary>
        /// Creates an UploadJob configured for chunked upload testing.
        /// File size is set above the 5 MB threshold.
        /// </summary>
        public static UploadJob CreateChunkedUploadJob(
            string localPath = @"D:\Astro\M31\Light\001.fits",
            long fileSize = 26_214_400, // 25 MB = 5 chunks
            UploadStatus status = UploadStatus.Pending)
        {
            var job = CreateUploadJob(localPath, status);
            job.FileSize = fileSize;
            return job;
        }

        /// <summary>
        /// Creates an UploadJob with pre-populated resume state for resume testing.
        /// </summary>
        public static UploadJob CreateResumeUploadJob(
            string uploadSessionId,
            List<int> chunksSent,
            int totalChunks,
            long fileSize = 26_214_400)
        {
            var job = CreateChunkedUploadJob(fileSize: fileSize);
            job.UploadSessionId = uploadSessionId;
            job.ChunksSent = chunksSent;
            job.TotalChunks = totalChunks;
            return job;
        }

        /// <summary>
        /// Creates an UploadJob with Failed status and error details for testing.
        /// </summary>
        public static UploadJob CreateFailedJob(
            string errorMessage = "Network timeout",
            int retryCount = 5)
        {
            var job = CreateUploadJob(status: UploadStatus.Failed, retryCount: retryCount);
            job.ErrorMessage = errorMessage;
            job.FailedAt = DateTime.UtcNow;
            return job;
        }

        /// <summary>
        /// Creates an UploadJob with Completed status for testing.
        /// </summary>
        public static UploadJob CreateCompletedJob()
        {
            var job = CreateUploadJob(status: UploadStatus.Completed);
            job.CompletedAt = DateTime.UtcNow;
            return job;
        }
    }
}
