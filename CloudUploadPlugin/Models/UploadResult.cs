namespace Astrovault.Models
{
    /// <summary>
    /// Typed classification of an upload failure (storage-quota plan §4 Step 1), produced by the
    /// single classifier <see cref="Astrovault.Core.FailureClassifier"/> from the confirmed backend
    /// per-status-code table (plan §8) and threaded through every drop point instead of being
    /// discarded (initiate / complete / chunk-group / resume-status).
    /// </summary>
    public enum FailureKind
    {
        /// <summary>
        /// Retryable: network fault, timeout, 5xx, 429/503, or any other non-2xx not otherwise
        /// classified. The default, so existing <see cref="UploadResult.Fail"/> call sites that
        /// don't pass a kind keep today's transient-by-default behavior.
        /// </summary>
        Transient = 0,

        /// <summary>401/403 -- invalid or revoked API key. Stops the whole queue starting in Step 2.</summary>
        PermanentAuth,

        /// <summary>507 -- storage quota exceeded. Stops the whole queue starting in Step 2.</summary>
        PermanentQuota,

        /// <summary>
        /// Local file errors (not found / access denied) or server 400 (malformed request) / 413
        /// (over per-request cap) -- this file only is skipped; the queue continues. Already fully
        /// permanent today.
        /// </summary>
        PermanentSkipFile
    }

    /// <summary>
    /// Result of an upload operation returned by the cloud API client.
    /// </summary>
    public class UploadResult
    {
        /// <summary>Whether the upload completed successfully.</summary>
        public bool Success { get; set; }

        /// <summary>URL of the uploaded file in cloud storage (if successful).</summary>
        public string RemoteUrl { get; set; }

        /// <summary>Error message if the upload failed.</summary>
        public string ErrorMessage { get; set; }

        /// <summary>HTTP status code from the API response.</summary>
        public int StatusCode { get; set; }

        /// <summary>
        /// Typed classification of the failure (see <see cref="FailureKind"/>). Defaults to
        /// Transient, matching the existing default behavior of <see cref="Fail"/>.
        /// </summary>
        public FailureKind FailureKind { get; set; } = FailureKind.Transient;

        /// <summary>
        /// Validated quota numbers parsed from a 507 response body, when present and parseable.
        /// Null whenever unavailable (no body, malformed JSON, or every field failed validation).
        /// Classification NEVER depends on this value -- it is display-only (storage-quota plan §4
        /// Step 1). Deliberately carries no server `error`/`message` text (see
        /// <see cref="Astrovault.Models.QuotaInfo"/>).
        /// </summary>
        public QuotaInfo QuotaInfo { get; set; }

        /// <summary>
        /// Whether this failure is permanent and should not be retried.
        /// True ONLY for <see cref="Astrovault.Models.FailureKind.PermanentSkipFile"/> (local file
        /// errors and server 400/413 -- skip this file, continue the queue; today's existing behavior).
        /// <see cref="Astrovault.Models.FailureKind.PermanentAuth"/> and
        /// <see cref="Astrovault.Models.FailureKind.PermanentQuota"/> deliberately evaluate to false
        /// here -- a 401/403/507 is NOT "skip this file and continue"; it stops the whole queue.
        /// <see cref="Astrovault.Core.UploadManager.HandleUploadFailure"/> checks
        /// <see cref="FailureKind"/> directly (BEFORE this derived flag) and routes those two kinds
        /// into the blocked-state latch (storage-quota plan §4 Step 2) instead.
        /// </summary>
        public bool IsPermanent => FailureKind == FailureKind.PermanentSkipFile;

        /// <summary>
        /// Creates a successful upload result.
        /// </summary>
        public static UploadResult Ok(string remoteUrl)
        {
            return new UploadResult
            {
                Success = true,
                RemoteUrl = remoteUrl,
                StatusCode = 200
            };
        }

        /// <summary>
        /// Creates a failed upload result. Defaults to <see cref="FailureKind.Transient"/> (will be
        /// retried) unless a caller that has already run the status code through
        /// <see cref="Astrovault.Core.FailureClassifier"/> supplies a more specific
        /// <paramref name="failureKind"/> (and, for a 507, the validated <paramref name="quotaInfo"/>).
        /// </summary>
        public static UploadResult Fail(
            string errorMessage,
            int statusCode = 0,
            FailureKind failureKind = FailureKind.Transient,
            QuotaInfo quotaInfo = null)
        {
            return new UploadResult
            {
                Success = false,
                ErrorMessage = errorMessage,
                StatusCode = statusCode,
                FailureKind = failureKind,
                QuotaInfo = quotaInfo
            };
        }

        /// <summary>
        /// Creates a permanent, skip-this-file failure result (will not be retried; the job is
        /// marked failed and the queue moves on). Use for errors that cannot resolve through
        /// retries: local FileNotFoundException/UnauthorizedAccessException, the HTTPS
        /// send-boundary gate, exhausted session restarts, and server 400/413.
        /// </summary>
        public static UploadResult FailPermanent(string errorMessage, int statusCode = 0)
        {
            return new UploadResult
            {
                Success = false,
                ErrorMessage = errorMessage,
                StatusCode = statusCode,
                FailureKind = FailureKind.PermanentSkipFile
            };
        }
    }
}
