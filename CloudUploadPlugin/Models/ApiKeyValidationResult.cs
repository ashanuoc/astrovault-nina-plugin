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
        /// Error description when validation fails.
        /// Null on success.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Creates a successful validation result.
        /// </summary>
        /// <param name="accountName">The account name returned by the server.</param>
        public static ApiKeyValidationResult Success(string accountName) =>
            new() { Valid = true, AccountName = accountName };

        /// <summary>
        /// Creates a failed validation result.
        /// </summary>
        /// <param name="errorMessage">Description of why validation failed.</param>
        public static ApiKeyValidationResult Fail(string errorMessage) =>
            new() { Valid = false, ErrorMessage = errorMessage };
    }
}
