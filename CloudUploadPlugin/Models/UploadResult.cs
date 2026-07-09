namespace Astrovault.Models
{
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
        /// Whether this failure is permanent (e.g., file not found, access denied).
        /// Permanent failures should not be retried.
        /// </summary>
        public bool IsPermanent { get; set; }

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
        /// Creates a failed upload result (transient by default -- will be retried).
        /// </summary>
        public static UploadResult Fail(string errorMessage, int statusCode = 0)
        {
            return new UploadResult
            {
                Success = false,
                ErrorMessage = errorMessage,
                StatusCode = statusCode
            };
        }

        /// <summary>
        /// Creates a permanent failure result (will not be retried).
        /// Use for errors that cannot resolve through retries, such as
        /// FileNotFoundException or UnauthorizedAccessException.
        /// </summary>
        public static UploadResult FailPermanent(string errorMessage, int statusCode = 0)
        {
            return new UploadResult
            {
                Success = false,
                IsPermanent = true,
                ErrorMessage = errorMessage,
                StatusCode = statusCode
            };
        }
    }
}
