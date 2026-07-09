using System;
using System.Threading.Tasks;
using Astrovault.Interfaces;
using Astrovault.Models;

namespace CloudUploadPlugin.Tests.E2E.Fixtures
{
    /// <summary>
    /// Lightweight IAuthManager stub for E2E tests.
    /// Returns a pre-configured test API key without any real authentication.
    /// Default key "test-key-1" matches mock-api-server/config.py.
    /// </summary>
    public class TestAuthStub : IAuthManager
    {
        /// <summary>
        /// The API key returned by GetApiKey(). Default: "test-key-1".
        /// </summary>
        public string ApiKey { get; set; } = "test-key-1";

        /// <summary>
        /// The account name associated with the API key.
        /// </summary>
        public string AccountName { get; set; } = "Test Observatory";

        /// <summary>
        /// Whether a valid API key is configured.
        /// </summary>
        public bool IsConfigured => !string.IsNullOrEmpty(ApiKey);

        /// <summary>
        /// Returns the current API key.
        /// </summary>
        public string GetApiKey() => ApiKey;

        /// <summary>
        /// Always returns a successful validation result.
        /// </summary>
        public Task<ApiKeyValidationResult> ValidateApiKeyAsync(string apiKey)
        {
            return Task.FromResult(new ApiKeyValidationResult
            {
                Valid = true,
                AccountName = AccountName
            });
        }

        /// <summary>
        /// Stores the given API key and account name.
        /// </summary>
        public void StoreApiKey(string apiKey, string accountName)
        {
            ApiKey = apiKey;
            AccountName = accountName;
        }

        /// <summary>
        /// Clears the stored API key and account name.
        /// </summary>
        public void ClearApiKey()
        {
            ApiKey = null;
            AccountName = null;
        }

        /// <summary>
        /// No-op disposal.
        /// </summary>
        public void Dispose()
        {
            // No resources to release
        }
    }
}
