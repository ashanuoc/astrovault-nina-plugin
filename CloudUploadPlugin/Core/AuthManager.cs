using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Interfaces;
using Astrovault.Models;
using Newtonsoft.Json;
using NINA.Core.Utility;

// SECURITY NOTE: API key is encrypted using Windows DPAPI (CurrentUser)
// before storage. The key is never logged in plaintext -- only masked
// via LogSanitizer.MaskApiKey(). The plugin sends the key as X-API-Key
// header on every request; no tokens or secrets are hardcoded.

namespace Astrovault.Core
{
    /// <summary>
    /// API key authentication manager with DPAPI-encrypted storage.
    /// Handles key validation via HTTP, secure persistence with DPAPI,
    /// and credential lifecycle (store, retrieve, clear).
    /// Uses a dedicated HttpClient with 10-second timeout for validation,
    /// separate from the upload HttpClient which needs longer timeouts.
    /// </summary>
    public class AuthManager : IAuthManager
    {
        private readonly HttpClient httpClient;
        private readonly string authFilePath;

        private string apiKey;
        private string accountName;

        public bool IsConfigured => !string.IsNullOrEmpty(apiKey);
        public string AccountName => accountName;

        /// <summary>
        /// Creates a new AuthManager instance.
        /// </summary>
        /// <param name="apiBaseUrl">API base URL (e.g., https://api.astrovault.io).</param>
        /// <param name="dataFolderPath">Folder to store encrypted API key.</param>
        public AuthManager(string apiBaseUrl, string dataFolderPath)
        {
            // REL-08: build the HttpClient over a SocketsHttpHandler so pooled connections are
            // recycled (no stale DNS / dead sockets after long idle periods between validations).
            // This is the single production ctor (no handler-injection test seam exists today),
            // so applying it here is safe — BaseAddress and the 10s validation timeout are preserved.
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
            };
            httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(10)
            };

            Directory.CreateDirectory(dataFolderPath);
            authFilePath = Path.Combine(dataFolderPath, "auth.dat");

            LoadStoredKey();
        }

        /// <summary>
        /// Test-only ctor (InternalsVisibleTo CloudUploadPlugin.Tests): mirrors the production ctor but
        /// builds the HttpClient over a caller-supplied <see cref="HttpMessageHandler"/> so the
        /// send-boundary gate can be proven behaviorally -- a throw-if-invoked handler whose SendAsync is
        /// never reached proves <see cref="ValidateApiKeyAsync"/> returns BEFORE any request is created
        /// over non-loopback http (no socket opened), instead of relying on network/timing. BaseAddress
        /// uses the same logic as the production ctor.
        /// </summary>
        internal AuthManager(string apiBaseUrl, string dataFolderPath, HttpMessageHandler handler)
        {
            httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(10)
            };

            Directory.CreateDirectory(dataFolderPath);
            authFilePath = Path.Combine(dataFolderPath, "auth.dat");

            LoadStoredKey();
        }

        /// <summary>
        /// Validates an API key against the Astrovault server.
        /// Sends GET request to v1/storage/nina/auth/validate with X-API-Key header.
        /// Uses CancellationTokenSource for 10-second timeout.
        /// </summary>
        /// <param name="apiKey">The API key to validate.</param>
        /// <returns>Validation result with account name on success.</returns>
        public async Task<ApiKeyValidationResult> ValidateApiKeyAsync(string apiKey)
        {
            Logger.Info($"[AuthManager] Validating API key {LogSanitizer.MaskApiKey(apiKey)}");

            // SECURITY (P19-01, send-boundary gate): never transmit the X-API-Key header over a
            // non-loopback http:// endpoint, regardless of how the endpoint got persisted (a stored
            // staging IP would otherwise bypass the settings-setter check). BaseAddress is the configured
            // endpoint. Returning HERE -- before new HttpRequestMessage(...) -- means no request is ever
            // created and no socket is opened over cleartext http. Mirrors the existing guard-clause style.
            if (!Astrovault.AstrovaultPlugin.IsSecureEndpoint(httpClient.BaseAddress?.ToString()))
            {
                Logger.Warning("[AuthManager] Refusing to send API key: HTTPS required outside localhost");
                return ApiKeyValidationResult.Fail("HTTPS required outside localhost");
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/storage/nina/auth/validate");
                request.Headers.Add("X-API-Key", apiKey);

                var response = await httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Logger.Warning("[AuthManager] Validation failed: Invalid API key");
                    return ApiKeyValidationResult.Fail("Invalid API key");
                }

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warning($"[AuthManager] Validation failed: HTTP {(int)response.StatusCode}");
                    return ApiKeyValidationResult.Fail("Unexpected error");
                }

                var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var result = JsonConvert.DeserializeObject<ValidationResponse>(body);

                if (result?.Valid == true)
                {
                    // SC6: never log the raw account/observatory name (NINA logs are shared in public
                    // support channels). MaskAccountName keeps support-correlatable shape only.
                    Logger.Info($"[AuthManager] Validation successful for account: {LogSanitizer.MaskAccountName(result.AccountName)}");
                    return ApiKeyValidationResult.Success(result.AccountName);
                }

                Logger.Warning("[AuthManager] Validation response: key not valid");
                return ApiKeyValidationResult.Fail("Invalid API key");
            }
            catch (TaskCanceledException)
            {
                Logger.Warning("[AuthManager] Validation timed out");
                return ApiKeyValidationResult.Fail("Server unreachable \u2014 check your connection");
            }
            catch (HttpRequestException ex)
            {
                Logger.Warning($"[AuthManager] Validation network error: {ex.Message}");
                return ApiKeyValidationResult.Fail("Server unreachable \u2014 check your connection");
            }
            catch (Exception ex)
            {
                Logger.Error($"[AuthManager] Validation unexpected error: {ex.Message}");
                return ApiKeyValidationResult.Fail("Unexpected error");
            }
        }

        /// <summary>
        /// Stores a validated API key and account name using DPAPI encryption.
        /// Serializes to JSON, encrypts with DPAPI CurrentUser scope, writes to auth.dat.
        /// Only call after successful validation.
        /// </summary>
        /// <param name="apiKey">The validated API key to store.</param>
        /// <param name="accountName">The account name from validation.</param>
        public void StoreApiKey(string apiKey, string accountName)
        {
            this.apiKey = apiKey;
            this.accountName = accountName;

            try
            {
                var stored = new StoredApiKey
                {
                    ApiKey = apiKey,
                    AccountName = accountName
                };

                var json = JsonConvert.SerializeObject(stored);
                var encryptedData = EncryptData(json);
                File.WriteAllBytes(authFilePath, encryptedData);

                Logger.Info($"[AuthManager] API key stored for {LogSanitizer.MaskApiKey(apiKey)}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[AuthManager] Failed to save API key: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the stored API key, or null if not configured.
        /// </summary>
        /// <returns>The decrypted API key, or null.</returns>
        public string GetApiKey()
        {
            return apiKey;
        }

        /// <summary>
        /// Clears the stored API key and resets to unconfigured state.
        /// Deletes the encrypted auth file from disk.
        /// </summary>
        public void ClearApiKey()
        {
            apiKey = null;
            accountName = null;

            try
            {
                if (File.Exists(authFilePath))
                {
                    File.Delete(authFilePath);
                }
                Logger.Info("[AuthManager] API key cleared");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[AuthManager] Failed to delete auth file: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads stored API key from encrypted file on disk.
        /// Detects and deletes old JWT-format auth.dat files (JsonException).
        /// Handles DPAPI scope loss (CryptographicException).
        /// </summary>
        private void LoadStoredKey()
        {
            if (!File.Exists(authFilePath))
            {
                return;
            }

            try
            {
                var encryptedData = File.ReadAllBytes(authFilePath);
                var decryptedJson = DecryptData(encryptedData, out var wasLegacyNullEntropy);
                var stored = JsonConvert.DeserializeObject<StoredApiKey>(decryptedJson);

                if (stored != null && !string.IsNullOrEmpty(stored.ApiKey))
                {
                    apiKey = stored.ApiKey;
                    accountName = stored.AccountName;
                    Logger.Info($"[AuthManager] Loaded stored API key for {LogSanitizer.MaskApiKey(apiKey)}");

                    if (wasLegacyNullEntropy)
                    {
                        // Migration-on-read: a legacy null-entropy auth.dat decrypted via the null fallback.
                        // Rewrite it once with app entropy via the existing StoreApiKey path so the next load
                        // uses entropy. No key material is logged. A TRUE scope loss never reaches here -- it
                        // throws in DecryptData and lands in catch (CryptographicException) -> DeleteAuthFile.
                        StoreApiKey(stored.ApiKey, stored.AccountName);
                        Logger.Info("[AuthManager] Migrated legacy auth file to entropy-protected storage");
                    }
                }
            }
            catch (CryptographicException ex)
            {
                // DPAPI scope loss (Windows account reset/reinstall) or corruption
                Logger.Warning($"[AuthManager] Failed to decrypt auth file (DPAPI scope loss?): {ex.Message}");
                DeleteAuthFile();
            }
            catch (JsonException ex)
            {
                // Old JWT format or corrupted JSON data
                Logger.Warning($"[AuthManager] Old or corrupt auth file detected: {ex.Message}");
                DeleteAuthFile();
            }
            catch (Exception ex)
            {
                Logger.Warning($"[AuthManager] Failed to load stored key: {ex.Message}");
                DeleteAuthFile();
            }
        }

        /// <summary>
        /// Deletes the auth file and resets state to not configured.
        /// </summary>
        private void DeleteAuthFile()
        {
            try
            {
                if (File.Exists(authFilePath))
                {
                    File.Delete(authFilePath);
                    Logger.Info("[AuthManager] Deleted auth file, reset to not configured");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[AuthManager] Failed to delete auth file: {ex.Message}");
            }

            apiKey = null;
            accountName = null;
        }

        /// <summary>
        /// App-specific DPAPI optional entropy for auth.dat (SC2 / DPAPI-entropy). Combined with the
        /// CurrentUser scope, this raises the bar above a null-entropy file: the same Windows user must
        /// ALSO supply this app constant to decrypt. Honest residual (T-19-07): this is NOT a defense
        /// against malware or arbitrary code already running as the same Windows user. This constant is
        /// part of the on-disk format -- it must stay stable forever; changing it later orphans existing
        /// auth.dat files. It is deliberately decoupled from UploadQueueRepository's entropy (each file
        /// owns its own constant -- no cross-file coupling required).
        /// </summary>
        private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("Astrovault.NINA.v1");

        /// <summary>
        /// Encrypts string data using Windows DPAPI (current user scope) with app-specific entropy.
        /// The transient plaintext byte[] is zeroed with Array.Clear after Protect returns (T-19-11);
        /// the immutable source string is an unavoidable residual (P19-06 -- buffers are zeroed, strings
        /// are not).
        /// </summary>
        private static byte[] EncryptData(string plainText)
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            try
            {
                return ProtectedData.Protect(plainBytes, AppEntropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                Array.Clear(plainBytes, 0, plainBytes.Length);
            }
        }

        /// <summary>
        /// Decrypts DPAPI-encrypted data with migration-on-read. New files are written with
        /// <see cref="AppEntropy"/>; a legacy null-entropy auth.dat written before entropy existed throws
        /// <see cref="CryptographicException"/> on the entropy attempt, so we retry once with null entropy
        /// and set <paramref name="wasLegacyNullEntropy"/> so the caller can rewrite it with entropy.
        /// <para>
        /// IMPORTANT (no third behavior): a TRUE DPAPI scope loss (Windows account reset/reinstall) or a
        /// genuinely corrupt/foreign blob makes BOTH the entropy AND the null-entropy Unprotect throw.
        /// That is intentional -- the exception propagates to <see cref="LoadStoredKey"/>'s existing
        /// <c>catch (CryptographicException)</c> -&gt; <see cref="DeleteAuthFile"/> path (T-19-12 accepted:
        /// reconnect required). We do NOT swallow it or invent new silent behavior.
        /// </para>
        /// The transient decrypted byte[] is zeroed with Array.Clear after the string is produced (T-19-11);
        /// the immutable returned string is an unavoidable residual (P19-06).
        /// </summary>
        private static string DecryptData(byte[] encryptedData, out bool wasLegacyNullEntropy)
        {
            wasLegacyNullEntropy = false;
            byte[] plainBytes;
            try
            {
                plainBytes = ProtectedData.Unprotect(encryptedData, AppEntropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // Legacy file written before entropy existed -> retry once with null entropy and flag for
                // rewrite. If THIS also throws (true scope loss / corruption), it propagates to the existing
                // catch (CryptographicException) -> DeleteAuthFile path -- deliberately, no third behavior.
                plainBytes = ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser);
                wasLegacyNullEntropy = true;
            }

            try
            {
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                Array.Clear(plainBytes, 0, plainBytes.Length);
            }
        }

        /// <summary>
        /// Response model for the validation endpoint.
        /// Expected: { valid: true/false, accountName: "..." }
        /// </summary>
        private class ValidationResponse
        {
            public bool Valid { get; set; }
            public string AccountName { get; set; }
        }

        public void Dispose()
        {
            httpClient?.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Internal class for JSON serialization of stored API key data.
        /// </summary>
        private class StoredApiKey
        {
            public string ApiKey { get; set; }
            public string AccountName { get; set; }
        }
    }
}
