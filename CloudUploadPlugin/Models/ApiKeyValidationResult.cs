namespace Astrovault.Models
{
    /// <summary>
    /// Result of an API key validation attempt.
    /// Replaces the old LoginResult model for the API key auth flow.
    /// </summary>
    public class ApiKeyValidationResult
    {
        /// <summary>
        /// Whether the API key was accepted by the server.
        /// </summary>
        public bool Valid { get; set; }

        /// <summary>
        /// Account name associated with the validated key.
        /// Null when validation fails.
        /// </summary>
        public string AccountName { get; set; }

        /// <summary>
        /// Stable account identifier from the validate response's optional <c>accountId</c> field
        /// (contract-delta round item 2). Null when validation fails, or when an older backend
        /// omits the field -- tolerated, never required.
        /// </summary>
        public string AccountId { get; set; }

        /// <summary>
        /// Error description when validation fails.
        /// Null on success.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Creates a successful validation result.
        /// </summary>
        /// <param name="accountName">The account name returned by the server.</param>
        /// <param name="accountId">The account id returned by the server, or null when absent (older backend).</param>
        public static ApiKeyValidationResult Success(string accountName, string accountId = null) =>
            new() { Valid = true, AccountName = accountName, AccountId = accountId };

        /// <summary>
        /// Creates a failed validation result.
        /// </summary>
        /// <param name="errorMessage">Description of why validation failed.</param>
        public static ApiKeyValidationResult Fail(string errorMessage) =>
            new() { Valid = false, ErrorMessage = errorMessage };
    }
}
