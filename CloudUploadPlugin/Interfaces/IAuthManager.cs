using System;
using System.Threading.Tasks;
using Astrovault.Models;

namespace Astrovault.Interfaces
{
    /// <summary>
    /// Contract for API key authentication.
    /// Handles validation, DPAPI-encrypted storage, and credential lifecycle.
    /// </summary>
    public interface IAuthManager : IDisposable
    {
        /// <summary>
        /// Validates an API key against the Astrovault server.
        /// Uses X-API-Key header with a 10-second timeout.
        /// </summary>
        /// <param name="apiKey">The API key to validate.</param>
        /// <returns>Validation result with account name on success, error message on failure.</returns>
        Task<ApiKeyValidationResult> ValidateApiKeyAsync(string apiKey);

        /// <summary>
        /// Stores a validated API key and account name using DPAPI encryption.
        /// Only call after successful validation.
        /// </summary>
        /// <param name="apiKey">The validated API key to store.</param>
        /// <param name="accountName">The account name from validation.</param>
        void StoreApiKey(string apiKey, string accountName);

        /// <summary>
        /// Gets the stored API key, or null if not configured.
        /// </summary>
        /// <returns>The decrypted API key, or null.</returns>
        string GetApiKey();

        /// <summary>
        /// Clears the stored API key and resets to unconfigured state.
        /// Deletes the encrypted auth file from disk.
        /// </summary>
        void ClearApiKey();

        /// <summary>
        /// Whether a validated API key is currently stored.
        /// </summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Account name from the last successful validation.
        /// Null if not configured.
        /// </summary>
        string AccountName { get; }
    }
}
