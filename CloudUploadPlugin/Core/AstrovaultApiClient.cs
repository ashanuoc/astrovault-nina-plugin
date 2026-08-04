using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Interfaces;
using Astrovault.Models;
using Newtonsoft.Json;
using NINA.Core.Utility;

namespace Astrovault.Core
{
    /// <summary>
    /// Real API client that calls the Astrovault API endpoints.
    /// Handles file uploads with metadata using X-API-Key authentication.
    /// Routes files below 5 MB to single upload, and files >= 5 MB to chunked upload
    /// (initiate/chunks/complete) with per-chunk SHA-256 hashing, retry, and resume support.
    /// </summary>
    public class AstrovaultApiClient : ICloudApiClient
    {
        private readonly HttpClient httpClient;
        private readonly IAuthManager authManager;
        private readonly IUploadQueueRepository queueRepository;

        // SEC-02 / CR-01: every client-side read/write of job.ChunksSent is guarded by the job's OWN
        // ChunksSentLock (UploadJob.ChunksSentLock), NOT a client-local lock. Once chunk uploads run
        // bounded-parallel (PERF-03), multiple worker tasks mutate ChunksSent concurrently while the
        // progress reporter / crash-state serializer / queue repo read it -- across COMPONENT
        // boundaries on the SAME aliased job instance. Locking the per-job monitor (rather than a lock
        // private to this client) is what makes those cross-component accesses race-free: the repo's
        // UpdateJobChunkStateAsync and the serializer lock the identical object. All access goes
        // through RecordChunkSent / SnapshotChunksSent / ResetChunksSent / SetChunksSent.

        // PERF-03 / D-08: getter-delegate carrying the user's ChunkConcurrency setting (1-4).
        // AstrovaultApiClient is constructed WITHOUT IPluginOptionsAccessor access, so AstrovaultPlugin
        // (the settings owner) injects `() => clampedChunkConcurrency` here — same seam pattern as the
        // drain-order delegate. Null (existing/test ctors) means "no setting" -> effective concurrency 1,
        // preserving today's exact sequential behavior.
        private readonly Func<int> getChunkConcurrency;

        // Bounds for ChunkConcurrency. Default 2 (D-08). concurrency=1 reproduces sequential behavior.
        internal const int MinChunkConcurrency = 1;
        internal const int MaxChunkConcurrency = 4;

        // API path prefix matching real backend
        private const string ApiPrefix = "v1/storage/nina";

        // Chunked upload constants
        private const long ChunkedThreshold = 5 * 1024 * 1024; // 5 MB
        private const int ChunkSize = 5 * 1024 * 1024; // 5 MB
        private const int MaxChunkRetries = 3;
        private const int ChunkRetryDelayMs = 1500; // 1.5s between retries
        // D-10: raised from 2 to 10 so multi-night outages don't exhaust the session-restart budget.
        private const int MaxSessionRestarts = 10;

        // Retry-after-complete addendum: backend-confirmed §4 envelope code for a 409 on the
        // COMPLETE route meaning "this upload session already finished successfully" -- our own
        // retry of `complete` raced a still-in-flight original attempt that has since landed. Route
        // -specific and CompleteUploadAsync-only; every other route's 409 stays classified Transient
        // by FailureClassifier's default, unchanged.
        private const string CompleteAlreadyCompleteErrorCode = "upload.already_complete";

        // REL-08: refresh pooled connections so overnight sessions don't reuse stale DNS / dead sockets.
        private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);

        // REL-11: bounded retry for transient file-system races (AV/sync locks, zero-length) on
        // freshly-saved images. Uses NINA's Retry.Do convention (do not hand-roll a retry loop).
        private static readonly TimeSpan FileOpenRetryDelay = TimeSpan.FromMilliseconds(200);
        private const int FileOpenRetryCount = 5;

        // REL-09: per-attempt stall timeout. The outer Timeout (10 min) stays generous so a
        // slow-but-valid upload is not killed; a per-attempt linked CTS detects a stalled
        // chunk early (a hung socket) and triggers a retry instead of waiting out the whole
        // outer timeout.
        private static readonly TimeSpan DefaultPerAttemptChunkTimeout = TimeSpan.FromSeconds(90);

        // Storage-quota plan §4 Step 6: short per-call timeout for GetQuotaAsync -- the usage line is a
        // nice-to-have display refresh, not part of the upload path, so it must never hang anywhere
        // near the 10-minute upload-client timeout.
        private static readonly TimeSpan DefaultQuotaFetchTimeout = TimeSpan.FromSeconds(15);

        public string BaseUrl { get; }

        // --- Test seams (InternalsVisibleTo CloudUploadPlugin.Tests) -----------------
        // Allow the resilience tests to shrink the per-attempt stall timeout and the
        // inter-attempt retry delay so they run fast. Production uses the defaults.
        internal TimeSpan PerAttemptChunkTimeout { get; set; } = DefaultPerAttemptChunkTimeout;
        internal TimeSpan ChunkRetryDelayOverride { get; set; } = TimeSpan.FromMilliseconds(ChunkRetryDelayMs);

        // Storage-quota plan §4 Step 6 test seam: lets the timeout test shrink the bound instead of
        // waiting out a real 15s. Production uses the default.
        internal TimeSpan QuotaFetchTimeout { get; set; } = DefaultQuotaFetchTimeout;

        // PERF-03 test seam: lets parallel-chunk tests (which use a handler ctor without the
        // production getter-delegate) drive the effective concurrency. When non-null it takes
        // precedence over the injected delegate. Null in production.
        internal int? ChunkConcurrencyOverride { get; set; }

        // PERF-03 observability seam: the maximum number of chunk SendAsync calls observed
        // in-flight simultaneously during the last UploadChunksAsync. Tests assert this is 1
        // at concurrency=1 (sequential-equivalence) and > 1 at concurrency >= 2.
        internal int LastObservedMaxInFlight { get; private set; }
        // ----------------------------------------------------------------------------

        /// <summary>
        /// PERF-03: resolves the effective chunk concurrency, clamped to [1,4]. Order of precedence:
        /// the test override, then the injected getter-delegate, then 1 (sequential default when no
        /// setting is wired). A delegate that throws or returns a bad value falls back to 1.
        /// </summary>
        private int GetEffectiveChunkConcurrency()
        {
            int raw;
            if (ChunkConcurrencyOverride.HasValue)
            {
                raw = ChunkConcurrencyOverride.Value;
            }
            else if (getChunkConcurrency != null)
            {
                try { raw = getChunkConcurrency(); }
                catch { raw = MinChunkConcurrency; }
            }
            else
            {
                raw = MinChunkConcurrency;
            }

            if (raw < MinChunkConcurrency) return MinChunkConcurrency;
            if (raw > MaxChunkConcurrency) return MaxChunkConcurrency;
            return raw;
        }

        /// <summary>
        /// Creates a real API client for production use.
        /// </summary>
        /// <param name="getChunkConcurrency">
        /// PERF-03: optional getter-delegate carrying the user's ChunkConcurrency setting (1-4).
        /// Null preserves the existing sequential behavior (effective concurrency 1).
        /// </param>
        public AstrovaultApiClient(IAuthManager authManager, string baseUrl, Func<int> getChunkConcurrency = null)
        {
            this.authManager = authManager;
            this.getChunkConcurrency = getChunkConcurrency;
            BaseUrl = baseUrl.TrimEnd('/');

            // REL-08: build the production HttpClient over a SocketsHttpHandler so pooled
            // connections are recycled (no stale DNS / dead sockets on overnight sessions).
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = PooledConnectionLifetime,
                PooledConnectionIdleTimeout = PooledConnectionIdleTimeout
            };
            httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(BaseUrl + "/"),
                Timeout = TimeSpan.FromMinutes(10)
            };
        }

        /// <summary>
        /// Creates an API client with a custom HttpMessageHandler for testing.
        /// </summary>
        public AstrovaultApiClient(IAuthManager authManager, string baseUrl, HttpMessageHandler handler)
        {
            this.authManager = authManager;
            BaseUrl = baseUrl.TrimEnd('/');

            httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(BaseUrl + "/"),
                Timeout = TimeSpan.FromMinutes(10)
            };
        }

        /// <summary>
        /// Creates an API client with optional queue repository for chunk state persistence.
        /// PERF-03: accepts the ChunkConcurrency getter-delegate (defaults to null = sequential).
        /// </summary>
        public AstrovaultApiClient(IAuthManager authManager, string baseUrl, IUploadQueueRepository queueRepository, Func<int> getChunkConcurrency = null)
            : this(authManager, baseUrl, getChunkConcurrency)
        {
            this.queueRepository = queueRepository;
        }

        /// <summary>
        /// Creates an API client with custom handler and queue repository (for testing with persistence).
        /// </summary>
        public AstrovaultApiClient(IAuthManager authManager, string baseUrl, HttpMessageHandler handler, IUploadQueueRepository queueRepository)
            : this(authManager, baseUrl, handler)
        {
            this.queueRepository = queueRepository;
        }

        /// <summary>
        /// PERF-03 test ctor: custom handler + ChunkConcurrency getter-delegate (no queue repo).
        /// Lets the ParallelChunk tests verify the delegate drives effective concurrency through the
        /// same code path production uses, without injecting a live IPluginOptionsAccessor.
        /// </summary>
        public AstrovaultApiClient(IAuthManager authManager, string baseUrl, HttpMessageHandler handler, Func<int> getChunkConcurrency)
            : this(authManager, baseUrl, handler)
        {
            this.getChunkConcurrency = getChunkConcurrency;
        }

        // ====================================================================
        // SEC-02: synchronized ChunksSent access
        // ====================================================================

        /// <summary>
        /// SEC-02: records a successfully-uploaded chunk index on the job under the lock.
        /// Idempotent (a re-record of an already-present index is a no-op) so a parallel
        /// retry that races with reconciliation cannot create duplicates. The job's
        /// <see cref="UploadJob.ChunksSent"/> list is mutated only here and in
        /// <see cref="ResetChunksSent"/> / <see cref="SetChunksSent"/>, always under
        /// <see cref="chunksSentLock"/>.
        /// </summary>
        internal void RecordChunkSent(UploadJob job, int chunkIndex)
        {
            lock (job.ChunksSentLock)
            {
                job.ChunksSent ??= new List<int>();
                if (!job.ChunksSent.Contains(chunkIndex))
                {
                    job.ChunksSent.Add(chunkIndex);
                }
            }
        }

        /// <summary>
        /// SEC-02: returns a private snapshot copy of the job's ChunksSent under the lock.
        /// Callers (progress, crash-state persistence) enumerate/serialize the copy, never the
        /// live list, so a concurrent <see cref="RecordChunkSent"/> can't throw
        /// "Collection was modified" or yield a torn read.
        /// </summary>
        internal List<int> SnapshotChunksSent(UploadJob job)
        {
            lock (job.ChunksSentLock)
            {
                return job.ChunksSent == null ? new List<int>() : new List<int>(job.ChunksSent);
            }
        }

        /// <summary>SEC-02: replaces ChunksSent with a fresh empty list under the lock (session reset).</summary>
        private void ResetChunksSent(UploadJob job)
        {
            lock (job.ChunksSentLock)
            {
                job.ChunksSent = new List<int>();
            }
        }

        /// <summary>
        /// D-11: replaces ChunksSent with the server's authoritative received-chunk set under the
        /// lock (resume reconciliation). Copies into a fresh list so the caller's collection is not aliased.
        /// </summary>
        private void SetChunksSent(UploadJob job, IEnumerable<int> chunks)
        {
            lock (job.ChunksSentLock)
            {
                job.ChunksSent = new List<int>(chunks);
            }
        }

        /// <summary>SEC-02: count of recorded chunks read under the lock.</summary>
        private int ChunksSentCount(UploadJob job)
        {
            lock (job.ChunksSentLock)
            {
                return job.ChunksSent?.Count ?? 0;
            }
        }

        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                var apiKey = authManager.GetApiKey();
                if (string.IsNullOrEmpty(apiKey))
                    return false;

                // WR-02 / P19-01: never attach the key over a non-loopback http:// boundary, mirroring the
                // ValidateApiKeyAsync + UploadFileAsync send-boundary gates. Only test-reachable today, but
                // the endpoint setter permits persisting non-loopback http, so this closes the last send path.
                if (!Astrovault.AstrovaultPlugin.IsSecureEndpoint(httpClient.BaseAddress?.ToString()))
                {
                    Logger.Warning("[ApiClient] Refusing connection test: HTTPS required outside localhost");
                    return false;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/auth/validate");
                request.Headers.Add("X-API-Key", apiKey);
                request.Headers.Add(PluginVersionHeader.HeaderName, PluginVersionHeader.Value);

                // IN-04: bound the connection test with a short timeout (mirrors AuthManager.ValidateApiKeyAsync)
                // so a dead endpoint cannot hang for the full 10-minute upload-client timeout.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var response = await httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return false;

                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var result = JsonConvert.DeserializeObject<ValidationCheckResponse>(body);
                return result?.Valid == true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[ApiClient] Connection test failed: {ex.Message}");
                return false;
            }
        }

        private class ValidationCheckResponse
        {
            [JsonProperty("valid")]
            public bool Valid { get; set; }
        }

        /// <summary>
        /// Storage-quota plan §4 Step 6: <c>GET v1/storage/nina/quota</c>. Never throws -- every failure
        /// mode (no key, HTTPS gate, 404 on an older server, any other non-2xx, malformed body, timeout,
        /// network fault) returns null so the caller can hide the usage line silently. A 404 is logged
        /// at Debug only (not a real error -- an older server predating this endpoint); every other
        /// failure is logged at Warning, matching the rest of this client's non-spammy conventions.
        /// </summary>
        public async Task<QuotaUsage> GetQuotaAsync(CancellationToken cancellationToken)
        {
            try
            {
                var apiKey = authManager.GetApiKey();
                if (string.IsNullOrEmpty(apiKey))
                {
                    return null;
                }

                // WR-02 / P19-01: same send-boundary HTTPS gate as every other route that attaches the
                // X-API-Key header.
                if (!Astrovault.AstrovaultPlugin.IsSecureEndpoint(httpClient.BaseAddress?.ToString()))
                {
                    Logger.Warning("[ApiClient] Refusing quota fetch: HTTPS required outside localhost");
                    return null;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/quota");
                request.Headers.Add("X-API-Key", apiKey);
                request.Headers.Add(PluginVersionHeader.HeaderName, PluginVersionHeader.Value);

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(QuotaFetchTimeout);

                using var response = await httpClient.SendAsync(request, attemptCts.Token).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Older server, predates this endpoint -- NOT an error. No toast, no warning log.
                    Logger.Debug("[ApiClient] Quota endpoint returned 404 (older server) -- hiding usage line");
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warning($"[ApiClient] Quota fetch failed: HTTP {(int)response.StatusCode}");
                    return null;
                }

                return QuotaUsageParser.TryParse(body);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The per-call QuotaFetchTimeout fired, not the caller's own token -- a slow/dead
                // endpoint, not an error worth surfacing to the user for a nice-to-have display line.
                Logger.Warning("[ApiClient] Quota fetch timed out");
                return null;
            }
            catch (OperationCanceledException)
            {
                // Caller's own token was cancelled (e.g. plugin teardown mid-fetch) -- not a fetch
                // failure to log as a warning.
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[ApiClient] Quota fetch failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Uploads a file to cloud storage. Routes based on file size:
        /// files below 5 MB use single POST, files >= 5 MB use chunked upload protocol.
        /// </summary>
        public async Task<UploadResult> UploadFileAsync(
            UploadJob job,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            var apiKey = authManager.GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                return UploadResult.Fail("Not authenticated");
            }

            // P19-01: send-boundary HTTPS gate. The X-API-Key header must never cross a non-loopback
            // http:// boundary, regardless of how the endpoint got persisted. FailPermanent so a job
            // does not burn transient retries against an endpoint that can never be made secure.
            if (!Astrovault.AstrovaultPlugin.IsSecureEndpoint(httpClient.BaseAddress?.ToString()))
            {
                return UploadResult.FailPermanent("HTTPS required outside localhost");
            }

            if (!File.Exists(job.LocalPath))
            {
                return UploadResult.FailPermanent($"File not found: {LogSanitizer.MaskPath(job.LocalPath)}");
            }

            try
            {
                // WR-05 / SEC-05: re-stat the file BEFORE the single-vs-chunked branch decision. The
                // branch was made on the stale queue-time job.FileSize, so a file that GREW past the
                // 5 MB threshold between enqueue and upload would wrongly take the single-file multipart
                // path (and a file that shrank below it would wrongly chunk). Refresh the size first so
                // the routing decision uses the current on-disk length; the chunked path's own re-stat
                // then composes idempotently.
                if (TryGetCurrentFileLength(job, out long currentLength) && currentLength != job.FileSize)
                {
                    Logger.Info($"[ApiClient] Pre-branch FileSize re-stat: queue-time {job.FileSize} -> current {currentLength}");
                    job.FileSize = currentLength;
                }

                if (job.FileSize < ChunkedThreshold)
                {
                    return await UploadSingleFileAsync(job, apiKey, progress, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    return await UploadChunkedAsync(job, apiKey, progress, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Storage-quota plan directive D (cancellation honesty): the CALLER's own token was
                // cancelled (shutdown/user cancel) -- a real cancellation, not a transient failure.
                // Rethrow so UploadManager's existing clean-cancel catch handles it (no breaker feed,
                // no retry increment, no state change) instead of fabricating a normal failed
                // UploadResult that gets fed back through the retry/breaker machinery.
                throw;
            }
            catch (OperationCanceledException)
            {
                // The caller's own token is still live -- this is an internal bound (e.g. the
                // HttpClient's own request timeout) firing, not a real cancel. Stays transient so it
                // retries normally.
                return UploadResult.Fail("Upload cancelled");
            }
            catch (FileNotFoundException)
            {
                return UploadResult.FailPermanent($"File not found: {LogSanitizer.MaskPath(job.LocalPath)}");
            }
            catch (UnauthorizedAccessException)
            {
                return UploadResult.FailPermanent($"Access denied: {LogSanitizer.MaskPath(job.LocalPath)}");
            }
            catch (IOException ex)
            {
                Logger.Warning($"[ApiClient] Transient IO error: {ex.Message}");
                return UploadResult.Fail(ex.Message);
            }
            catch (Exception ex)
            {
                Logger.Error($"[ApiClient] Upload failed: {ex.Message}");
                return UploadResult.Fail(ex.Message);
            }
        }

        // ====================================================================
        // Single File Upload (< 5 MB)
        // ====================================================================

        /// <summary>
        /// Uploads a single file using multipart POST to v1/storage/nina/images/upload.
        /// Used for files below the chunked threshold.
        /// </summary>
        private async Task<UploadResult> UploadSingleFileAsync(
            UploadJob job,
            string apiKey,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            // REL-11: a freshly-saved image may be momentarily AV/sync-locked or zero-length.
            // Open via NINA's bounded Retry.Do so transient locks / races are absorbed.
            using var fileStream = await OpenFileWithRetryAsync(job.LocalPath).ConfigureAwait(false);
            var fileSize = fileStream.Length;
            var fileName = Path.GetFileName(job.LocalPath);

            var progressStream = new ProgressStream(fileStream, fileSize, progress);

            using var content = new MultipartFormDataContent();

            var fileContent = new StreamContent(progressStream);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
            content.Add(fileContent, "file", fileName);

            var metadata = string.IsNullOrEmpty(job.MetadataJson) ? "{}" : job.MetadataJson;
            content.Add(new StringContent(metadata), "metadata");
            content.Add(new StringContent(job.RelativePath ?? ""), "remotePath");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/images/upload");
            request.Headers.Add("X-API-Key", apiKey);
            request.Headers.Add(PluginVersionHeader.HeaderName, PluginVersionHeader.Value);
            request.Content = content;

            Logger.Info($"[ApiClient] Single upload: {fileName} ({fileSize} bytes)");

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning($"[ApiClient] Upload failed: {response.StatusCode}");
                var statusCode = (int)response.StatusCode;
                var failureKind = FailureClassifier.Classify(statusCode);
                var quotaInfo = failureKind == FailureKind.PermanentQuota
                    ? QuotaInfoParser.TryParse(responseBody)
                    : null;
                // SC6: build the error from the NUMERIC status only. Server-controllable ReasonPhrase
                // must not flow into persisted/user-facing error text (the ToActionableError mapper keys
                // off the number, not the phrase).
                return UploadResult.Fail($"HTTP {statusCode}", statusCode, failureKind, quotaInfo);
            }

            var result = JsonConvert.DeserializeObject<UploadApiResponse>(responseBody);

            // Real backend returns {"remotePath":"...","fileSize":N} without a "success" boolean.
            // Mock returns {"success":true,"remoteUrl":"..."}. Handle both.
            if (result?.Success == true || !string.IsNullOrEmpty(result?.RemotePath))
            {
                var url = result.RemoteUrl ?? result.RemotePath;
                // SC3/P19-03: log filename + success state only. Never log the remote host/URL (even the
                // approved domain) — NINA logs are routinely shared in public support channels.
                Logger.Info($"[ApiClient] Upload complete: {Path.GetFileName(job.LocalPath)}");
                return UploadResult.Ok(url);
            }

            return UploadResult.Fail(result?.ErrorMessage ?? "Unknown error");
        }

        // ====================================================================
        // Chunked Upload (>= 5 MB)
        // ====================================================================

        /// <summary>
        /// Orchestrates the full chunked upload flow: initiate, upload chunks, complete.
        /// Supports resume from interrupted uploads and session expiry auto-restart.
        /// </summary>
        private async Task<UploadResult> UploadChunkedAsync(
            UploadJob job,
            string apiKey,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            // Resume flow: check if we have an existing session
            if (!string.IsNullOrEmpty(job.UploadSessionId))
            {
                // SEC-05 / resume TOCTOU: re-stat the file. If the current length implies a different
                // chunk count than the persisted/session state, the persisted chunk map is stale —
                // restart the session explicitly rather than uploading against a stale map.
                if (TryGetCurrentChunkCount(job, out int currentChunkCount)
                    && job.TotalChunks > 0
                    && currentChunkCount != job.TotalChunks)
                {
                    if (job.SessionRestartCount >= MaxSessionRestarts)
                    {
                        return UploadResult.FailPermanent("File changed since upload started; session restarted too many times");
                    }

                    Logger.Warning($"[ApiClient] Resume TOCTOU: file now implies {currentChunkCount} chunks but session has {job.TotalChunks}; restarting session");
                    job.SessionRestartCount++;
                    job.UploadSessionId = null;
                    ResetChunksSent(job);
                    job.TotalChunks = 0;
                    return await UploadChunkedAsync(job, apiKey, progress, cancellationToken).ConfigureAwait(false);
                }

                var resumeOutcome = await GetResumeStatusAsync(job.UploadSessionId, job.TotalChunks, apiKey, cancellationToken).ConfigureAwait(false);

                if (resumeOutcome.FailureResult != null)
                {
                    // G9/G4 fix: a classified non-2xx (401/403/507/...) on the resume-status route is
                    // surfaced directly -- it must NEVER be mistaken for "session expired, restart".
                    return resumeOutcome.FailureResult;
                }

                if (resumeOutcome.SessionExpired)
                {
                    // Session expired or not found -- try to restart
                    if (job.SessionRestartCount >= MaxSessionRestarts)
                    {
                        return UploadResult.FailPermanent("Upload session expired too many times");
                    }

                    job.SessionRestartCount++;
                    job.UploadSessionId = null;
                    ResetChunksSent(job);
                    job.TotalChunks = 0;
                    Logger.Info($"[ApiClient] Session expired, restarting ({job.SessionRestartCount}/{MaxSessionRestarts})");

                    // Fall through to fresh initiate below
                }
                else
                {
                    var resume = resumeOutcome.Status;

                    // D-11: reconcile local ChunksSent with the server's AUTHORITATIVE received set,
                    // then re-send exactly the missing chunks (client-side read; no wire change).
                    SetChunksSent(job, resume.Received);

                    if (resume.Missing.Count == 0)
                    {
                        // All chunks already received -- go straight to complete.
                        // CR-02: emit the authoritative terminal 100% first, mirroring the terminal
                        // report on the main parallel path. Otherwise a fully-received resumed job
                        // completes with the UI bar stuck at the resume-base fraction (which can be
                        // < 1.0 if the re-stat changed FileSize), never reaching 100%.
                        if (job.FileSize > 0) progress?.Report(1.0);
                        return await CompleteUploadAsync(job.UploadSessionId, job.TotalChunks, apiKey, cancellationToken).ConfigureAwait(false);
                    }

                    return await UploadChunksAsync(job, apiKey, resume.Missing, progress, cancellationToken).ConfigureAwait(false);
                }
            }

            // Fresh session: initiate upload
            {
                // SEC-05 (TOCTOU): re-stat the file and recompute against its CURRENT on-disk length
                // rather than trusting the queue-time FileSize. The file may have grown/changed between
                // enqueue and upload. Both the initiate fileSize field and the chunk byte-math then use
                // the current length, so the chunk map is never built from a stale size.
                if (TryGetCurrentFileLength(job, out long currentLength) && currentLength != job.FileSize)
                {
                    Logger.Info($"[ApiClient] FileSize re-stat: queue-time {job.FileSize} -> current {currentLength}");
                    job.FileSize = currentLength;
                }

                var initOutcome = await InitiateUploadAsync(job, apiKey, cancellationToken).ConfigureAwait(false);
                if (initOutcome.Response == null)
                {
                    // G4 drop-point fix: preserve the classified failure (status/FailureKind/QuotaInfo)
                    // instead of discarding it into a generic message.
                    return initOutcome.FailureResult ?? UploadResult.Fail("Failed to initiate chunked upload");
                }

                var initResult = initOutcome.Response;
                job.UploadSessionId = initResult.UploadId;
                job.TotalChunks = initResult.TotalChunks;
                ResetChunksSent(job);

                var allChunks = new HashSet<int>(Enumerable.Range(0, initResult.TotalChunks));
                return await UploadChunksAsync(job, apiKey, allChunks, progress, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// PERF-03 / D-08: uploads the missing chunks with bounded parallelism and completes the
        /// upload session. Shared by both fresh-upload and resume paths.
        ///
        /// Safe concurrency model:
        /// (1) INDEPENDENT READS — each chunk task opens its OWN FileStream (FileShare.Read) and
        ///     seeks to its own offset; there is no shared FileStream position to race on.
        /// (2) BYTE-ACCURATE PROGRESS — progress sums each completed chunk's ACTUAL byte length
        ///     (the final chunk is shorter, §778), so out-of-order completion (short final chunk
        ///     finishing early) never over-reports. Clamped to <= 100%.
        /// (3) SIBLING CANCEL/DRAIN ON 410/FATAL — a linked CTS bounds the chunk-task group; on a
        ///     410 Gone or fatal chunk error the group is cancelled and ALL sibling tasks are awaited
        ///     to completion BEFORE the recursive session-restart runs, so no restart races in-flight
        ///     tasks mutating ChunksSent/session state.
        /// concurrency=1 reproduces exact sequential behavior. Out-of-order PUTs are permitted by
        /// BACKEND-API-SPEC §780-781 — no wire change.
        /// </summary>
        private async Task<UploadResult> UploadChunksAsync(
            UploadJob job,
            string apiKey,
            HashSet<int> missingChunks,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int maxConcurrency = GetEffectiveChunkConcurrency();
            var ordered = missingChunks.OrderBy(i => i).ToList();

            // Byte-accurate progress denominator: actual file length (re-stat composes with SEC-05).
            long fileSize = job.FileSize;

            // Progress base = bytes already accepted by the server (resume), summed from the actual
            // sizes of the chunks already in ChunksSent. Per-chunk completions add their actual length.
            long completedBytes = SumChunkBytes(SnapshotChunksSent(job), fileSize);
            var progressLock = new object();
            void ReportChunkBytes(long actualBytes)
            {
                // Report INSIDE the lock so concurrent chunk completions deliver progress in
                // accumulation order. Reporting outside the lock lets two siblings race between
                // bumping the shared counter and calling Report, so a stale lower value can land
                // last (the UI bar jumps backwards and may never reach 100%).
                lock (progressLock)
                {
                    completedBytes += actualBytes;
                    if (fileSize > 0)
                    {
                        var fraction = (double)completedBytes / fileSize;
                        progress?.Report(Math.Min(fraction, 1.0));
                    }
                }
            }

            // Linked CTS so a 410/fatal in any chunk cancels its siblings; await-drain before restart.
            using var groupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var concurrencyGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            // Shared outcome flags (set under groupResultLock). Storage-quota plan §4 Step 1: the
            // group must preserve the CHILD's typed result under a TOTAL precedence order --
            // PermanentAuth > PermanentQuota > 410-session-expiry > everything else -- instead of
            // reconstructing a generic message (G4). Auth/quota get their own dedicated slots so
            // they can NEVER be shadowed by (or fall into) the 410 session-restart recursion below,
            // even if a sibling also raced a 410 before honoring cancellation.
            var groupResultLock = new object();
            UploadResult authResult = null;    // a chunk returned 401/403 (PermanentAuth)
            UploadResult quotaResult = null;   // a chunk returned 507 (PermanentQuota)
            bool sessionExpired = false;       // a chunk returned 410 Gone
            UploadResult otherResult = null;   // a chunk failed transiently, or PermanentSkipFile (400/413)

            // In-flight observability (PERF-03 sequential-equivalence assertion).
            int inFlight = 0;
            int maxInFlight = 0;
            var inFlightLock = new object();

            async Task UploadOneChunkAsync(int chunkIndex)
            {
                await concurrencyGate.WaitAsync(groupCts.Token).ConfigureAwait(false);
                try
                {
                    lock (inFlightLock)
                    {
                        inFlight++;
                        if (inFlight > maxInFlight) maxInFlight = inFlight;
                    }

                    groupCts.Token.ThrowIfCancellationRequested();

                    // (1) INDEPENDENT READ: each task opens its own stream and reads only its chunk.
                    byte[] chunkBytes;
                    using (var chunkStream = await OpenFileWithRetryAsync(job.LocalPath).ConfigureAwait(false))
                    {
                        chunkBytes = await ReadChunkAsync(chunkStream, chunkIndex, ChunkSize, fileSize).ConfigureAwait(false);
                    }

                    var chunkHash = ComputeChunkHash(chunkBytes);

                    var chunkResult = await UploadChunkWithRetryAsync(
                        job.UploadSessionId, chunkIndex, chunkBytes, chunkHash, apiKey, groupCts.Token, cancellationToken).ConfigureAwait(false);

                    // Total precedence (storage-quota plan §4 Step 1): PermanentAuth > PermanentQuota >
                    // 410-session-expiry > everything else. Auth/quota chunk failures cancel the shared
                    // token and drain siblings exactly like the pre-existing 410/fatal paths, but land in
                    // their OWN slot so they can never be shadowed by (or trigger) the 410 session-restart
                    // recursion below -- a revoked key or full quota must never be reported as a session
                    // expiry, no matter how the siblings race.
                    if (chunkResult.FailureKind == FailureKind.PermanentAuth)
                    {
                        lock (groupResultLock) { authResult ??= chunkResult; }
                        groupCts.Cancel();
                        return;
                    }

                    if (chunkResult.FailureKind == FailureKind.PermanentQuota)
                    {
                        lock (groupResultLock) { quotaResult ??= chunkResult; }
                        groupCts.Cancel();
                        return;
                    }

                    if (chunkResult.StatusCode == 410)
                    {
                        // (3) Signal a session expiry and cancel siblings; the orchestrator drains then restarts.
                        lock (groupResultLock) { sessionExpired = true; }
                        groupCts.Cancel();
                        return;
                    }

                    if (!chunkResult.Success)
                    {
                        // Preserve the child's OWN typed result (transient, or PermanentSkipFile for a
                        // 400/413 chunk) instead of reconstructing a generic Fail(...) that drops
                        // StatusCode/FailureKind -- the G4 fix for this drop point.
                        lock (groupResultLock)
                        {
                            otherResult ??= chunkResult;
                        }
                        groupCts.Cancel();
                        return;
                    }

                    // Success: record (SEC-02 guarded), persist a snapshot, advance byte-accurate progress.
                    RecordChunkSent(job, chunkIndex);

                    if (queueRepository != null)
                    {
                        await queueRepository.UpdateJobChunkStateAsync(
                            job.Id, job.UploadSessionId, SnapshotChunksSent(job), job.TotalChunks).ConfigureAwait(false);
                    }

                    ReportChunkBytes(ChunkActualLength(chunkIndex, fileSize));

                    if (chunkIndex % 5 == 0 || chunkIndex == job.TotalChunks - 1)
                    {
                        Logger.Info($"[ApiClient] Chunk {chunkIndex + 1}/{job.TotalChunks} uploaded");
                    }
                }
                finally
                {
                    lock (inFlightLock) { inFlight--; }
                    concurrencyGate.Release();
                }
            }

            // Launch all chunk tasks (bounded by the semaphore) and DRAIN them to completion.
            var tasks = ordered.Select(UploadOneChunkAsync).ToList();
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Either the OUTER token was cancelled (user cancel) or the group was cancelled by a
                // 410/fatal. Drain ALL siblings to completion (observe exceptions) BEFORE deciding.
                await DrainTasksAsync(tasks).ConfigureAwait(false);
            }

            // CR-03: set the in-flight observability seam EXACTLY ONCE here, on every exit path
            // (normal completion, group-cancel restart, fatal, and outer-cancel throw below). The
            // previous code set it in two places and could miss the group-cancel-only path.
            LastObservedMaxInFlight = maxInFlight;

            // CR-03: make the post-drain outcome DETERMINISTIC and independent of how the
            // group-cancel raced the outer-cancel. Evaluate OUTER cancellation FIRST: a real user/
            // shutdown cancel takes precedence and propagates as a clean OperationCanceledException.
            // The job is left in a resumable state -- ChunksSent is NOT reset on this path (only on the
            // restart path below), so a subsequent run resumes from the server-authoritative received
            // set. Any pending session-restart (sessionExpired) is intentionally abandoned for this
            // run and re-derived on resume; this is the documented REL-04 shutdown behavior.
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            // (3) No outer cancel: every sibling is drained, so act on the group outcome
            // deterministically, per the TOTAL precedence order (storage-quota plan §4 Step 1):
            // PermanentAuth > PermanentQuota > 410-session-expiry > everything else. Auth/quota are
            // evaluated BEFORE sessionExpired so they can NEVER trigger the 410 session-restart
            // recursion below, even if a sibling also raced a 410 before honoring cancellation.
            if (authResult != null)
            {
                return authResult;
            }

            if (quotaResult != null)
            {
                return quotaResult;
            }

            if (sessionExpired)
            {
                if (job.SessionRestartCount >= MaxSessionRestarts)
                {
                    return UploadResult.FailPermanent("Upload session expired too many times");
                }

                job.SessionRestartCount++;
                job.UploadSessionId = null;
                ResetChunksSent(job);
                job.TotalChunks = 0;
                Logger.Info($"[ApiClient] Session expired mid-upload, restarting ({job.SessionRestartCount}/{MaxSessionRestarts})");

                // Safe to restart: no sibling task is still touching ChunksSent/session state.
                return await UploadChunkedAsync(job, apiKey, progress, cancellationToken).ConfigureAwait(false);
            }

            if (otherResult != null)
            {
                return otherResult;
            }

            // All chunks uploaded. Emit a final authoritative 100% AFTER every sibling has drained,
            // so the terminal progress value is deterministic regardless of how Progress<T> schedules
            // the concurrent per-chunk reports above (the last in-flight report may otherwise be a
            // stale lower fraction under thread-pool reordering).
            if (fileSize > 0)
            {
                progress?.Report(1.0);
            }

            // All chunks uploaded -- complete.
            return await CompleteUploadAsync(job.UploadSessionId, job.TotalChunks, apiKey, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Drains a set of chunk tasks to completion, swallowing the cancellation/observed transport
        /// exceptions that the group-cancel produced. Ensures no sibling task is still running (and
        /// thus still mutating ChunksSent/session state) before a session restart.
        /// </summary>
        private static async Task DrainTasksAsync(IEnumerable<Task> tasks)
        {
            foreach (var t in tasks)
            {
                try { await t.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected on group/outer cancel */ }
                catch (Exception ex)
                {
                    // A sibling threw a non-cancellation fault while draining; log and continue so
                    // every sibling is observed before we proceed.
                    Logger.Warning($"[ApiClient] Sibling chunk task fault during drain: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Computes the number of chunks for a given byte length at the fixed ChunkSize.
        /// </summary>
        private static int ComputeChunkCount(long fileSize)
        {
            if (fileSize <= 0) return 0;
            return (int)((fileSize + ChunkSize - 1) / ChunkSize);
        }

        /// <summary>
        /// SEC-05: re-stats the file on disk and returns its CURRENT byte length. Returns false (and
        /// does not gate) when the file is missing/unreadable — the upload pipeline already re-checks
        /// File.Exists and surfaces a clear failure downstream.
        /// </summary>
        private static bool TryGetCurrentFileLength(UploadJob job, out long length)
        {
            length = 0;
            try
            {
                var info = new FileInfo(job.LocalPath);
                if (!info.Exists) return false;
                length = info.Length;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[ApiClient] Could not re-stat file length: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// SEC-05 / resume TOCTOU: re-stats the file on disk and computes the chunk count its CURRENT
        /// length implies. Returns false (and does not gate) when the file is missing/unreadable — the
        /// upload pipeline already re-checks File.Exists and surfaces a clear failure downstream.
        /// </summary>
        private static bool TryGetCurrentChunkCount(UploadJob job, out int chunkCount)
        {
            chunkCount = 0;
            if (!TryGetCurrentFileLength(job, out long length)) return false;
            chunkCount = ComputeChunkCount(length);
            return true;
        }

        /// <summary>
        /// Actual byte length of the chunk at <paramref name="chunkIndex"/> for a file of
        /// <paramref name="fileSize"/> bytes. The last chunk is shorter (§778).
        /// </summary>
        private static long ChunkActualLength(int chunkIndex, long fileSize)
        {
            long offset = (long)chunkIndex * ChunkSize;
            long remaining = fileSize - offset;
            if (remaining <= 0) return 0;
            return Math.Min(ChunkSize, remaining);
        }

        /// <summary>
        /// Sums the ACTUAL byte lengths of the given chunk indices for byte-accurate progress
        /// (accounts for the short final chunk). Used to seed the resume progress base.
        /// </summary>
        private static long SumChunkBytes(IEnumerable<int> chunkIndices, long fileSize)
        {
            long sum = 0;
            foreach (var idx in chunkIndices)
            {
                sum += ChunkActualLength(idx, fileSize);
            }
            return sum;
        }

        // ====================================================================
        // Chunked Upload Helper Methods
        // ====================================================================

        /// <summary>
        /// G4 drop-point fix: carries either the successful <see cref="InitiateResponse"/> or the
        /// classified typed failure (status/FailureKind/QuotaInfo) instead of collapsing every
        /// failure into a bare null.
        /// </summary>
        private sealed class InitiateOutcome
        {
            public InitiateResponse Response { get; init; }
            public UploadResult FailureResult { get; init; }
        }

        /// <summary>
        /// Initiates a chunked upload session via POST v1/storage/nina/uploads/initiate.
        /// </summary>
        private async Task<InitiateOutcome> InitiateUploadAsync(
            UploadJob job, string apiKey, CancellationToken cancellationToken)
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(Path.GetFileName(job.LocalPath)), "fileName");
            content.Add(new StringContent(job.FileSize.ToString()), "fileSize");
            content.Add(new StringContent(job.RelativePath ?? ""), "remotePath");
            content.Add(new StringContent(job.MetadataJson ?? "{}"), "metadata");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/uploads/initiate");
            request.Headers.Add("X-API-Key", apiKey);
            request.Headers.Add(PluginVersionHeader.HeaderName, PluginVersionHeader.Value);
            request.Content = content;

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // SC3/P19-03: status only — never log the raw server response body.
                var statusCode = (int)response.StatusCode;
                Logger.Warning($"[ApiClient] Initiate failed: HTTP {statusCode}");
                var failureKind = FailureClassifier.Classify(statusCode);
                var quotaInfo = failureKind == FailureKind.PermanentQuota
                    ? QuotaInfoParser.TryParse(body)
                    : null;
                return new InitiateOutcome
                {
                    FailureResult = UploadResult.Fail($"Failed to initiate chunked upload: HTTP {statusCode}", statusCode, failureKind, quotaInfo)
                };
            }

            var result = JsonConvert.DeserializeObject<InitiateResponse>(body);
            Logger.Info($"[ApiClient] Chunked upload initiated: {result?.UploadId}, {result?.TotalChunks} chunks");
            return new InitiateOutcome { Response = result };
        }

        /// <summary>
        /// REL-11: opens a freshly-saved image file for reading via NINA's bounded Retry.Do.
        /// A just-written file may be momentarily locked by AV/sync software or briefly
        /// zero-length; both are transient. We retry the open (and re-open on a zero-length
        /// race) up to FileOpenRetryCount times with FileOpenRetryDelay between attempts,
        /// rather than failing on the first attempt. Do NOT hand-roll the retry loop.
        /// </summary>
        private static async Task<FileStream> OpenFileWithRetryAsync(string path)
        {
            return await Retry.Do(() =>
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
                if (stream.Length == 0)
                {
                    // Zero-length race: dispose and force a retry so we don't upload an empty file.
                    stream.Dispose();
                    throw new IOException($"File is momentarily zero-length: {path}");
                }
                return stream;
            }, FileOpenRetryDelay, FileOpenRetryCount).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads a chunk of bytes from the file stream at the specified index.
        /// Handles partial reads by looping until the buffer is fully filled.
        /// </summary>
        private static async Task<byte[]> ReadChunkAsync(FileStream fileStream, int chunkIndex, int chunkSize, long fileSize)
        {
            long offset = (long)chunkIndex * chunkSize;
            // WR-03: the file can shrink between the SEC-05 re-stat and this read (each parallel chunk
            // task opens its own stream much later). A negative remaining would make new byte[negative]
            // throw OverflowException/ArgumentOutOfRangeException; surface a clean transient IOException
            // (retryable) instead.
            long remaining = fileSize - offset;
            if (remaining <= 0)
            {
                throw new IOException($"File shrank below expected size at chunk {chunkIndex} (offset {offset}, fileSize {fileSize})");
            }
            int bytesToRead = (int)Math.Min(chunkSize, remaining);
            var buffer = new byte[bytesToRead];

            fileStream.Seek(offset, SeekOrigin.Begin);
            int totalRead = 0;
            while (totalRead < bytesToRead)
            {
                int read = await fileStream.ReadAsync(buffer, totalRead, bytesToRead - totalRead).ConfigureAwait(false);
                if (read == 0) throw new IOException("Unexpected end of file");
                totalRead += read;
            }
            return buffer;
        }

        /// <summary>
        /// Computes SHA-256 hash of chunk bytes. Returns "sha256=" + lowercase hex string.
        /// </summary>
        private static string ComputeChunkHash(byte[] chunkBytes)
        {
            byte[] hash = SHA256.HashData(chunkBytes);
            return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Uploads a single chunk with retry logic.
        /// Retries up to MaxChunkRetries times for transient failures.
        /// Returns immediately (no retry) for 410 Gone (session expired).
        /// </summary>
        private async Task<UploadResult> UploadChunkWithRetryAsync(
            string uploadId, int chunkIndex, byte[] chunkBytes, string chunkHash,
            string apiKey, CancellationToken groupToken, CancellationToken outerToken)
        {
            for (int attempt = 0; attempt < MaxChunkRetries; attempt++)
            {
                // Storage-quota plan directive A (chunk-group precedence race, the P0): the group-stop
                // token gates DISPATCH ONLY. A sibling that has already classified a higher-precedence
                // failure (auth/quota/410/fatal) and cancelled the group must stop THIS chunk from
                // starting a NEW attempt -- but once an attempt is actually dispatched below, it must
                // run to completion (or the real outer/per-attempt bound) undisturbed, so its response
                // is always drained and classified before precedence is applied.
                groupToken.ThrowIfCancellationRequested();

                // REL-05: an HttpRequestMessage (and its content) cannot be re-sent, so the
                // request + content MUST be re-created on every attempt — otherwise a retry
                // after a transport fault would throw InvalidOperationException.
                using var content = new ByteArrayContent(chunkBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                using var request = new HttpRequestMessage(HttpMethod.Put,
                    $"{ApiPrefix}/uploads/{uploadId}/chunks/{chunkIndex}");
                request.Headers.Add("X-API-Key", apiKey);
                request.Headers.Add("X-Chunk-Hash", chunkHash);
                request.Headers.Add(PluginVersionHeader.HeaderName, PluginVersionHeader.Value);
                request.Content = content;

                // REL-09 + chunk-race fix: bound THIS dispatched attempt with the REAL outer lifecycle
                // token + the per-attempt timeout ONLY -- never the group-stop token. Once SendAsync is
                // called, a sibling's later group-cancel can no longer abort this in-flight request
                // before its response is read and classified (the actual P0 race: two chunks both
                // reach the server, one classifies first and cancels the group, and the OTHER's
                // already-dispatched SendAsync used to be torn down mid-flight by that same token,
                // losing its classification entirely).
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
                attemptCts.CancelAfter(PerAttemptChunkTimeout);

                TimeSpan? retryAfterDelay = null;
                try
                {
                    using var response = await httpClient.SendAsync(request, attemptCts.Token).ConfigureAwait(false);

                    // Session expired -- do NOT retry, return special status
                    if (response.StatusCode == HttpStatusCode.Gone)
                    {
                        return UploadResult.Fail("Session expired", 410);
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        return UploadResult.Ok(null);
                    }

                    var statusCode = (int)response.StatusCode;
                    var failureKind = FailureClassifier.Classify(statusCode);
                    if (failureKind != FailureKind.Transient)
                    {
                        // 401/403/507/400/413 -- permanent per the classifier; do NOT retry this chunk.
                        // Only read the body when it is actually needed (507 -- QuotaInfo); avoids an
                        // unnecessary await on the auth/skip-file paths. CancellationToken.None here is
                        // deliberate: the response has ALREADY been received and this chunk is already
                        // committed to a terminal result -- a SIBLING chunk's group-cancel (e.g. a 410
                        // that resolves with no further awaits) must not race this already-decided
                        // chunk's own body read and silently drop its QuotaInfo/result.
                        QuotaInfo quotaInfo = null;
                        if (failureKind == FailureKind.PermanentQuota)
                        {
                            var errorBody = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
                            quotaInfo = QuotaInfoParser.TryParse(errorBody);
                        }
                        Logger.Warning($"[ApiClient] Chunk {chunkIndex} permanent failure: HTTP {statusCode}");
                        return UploadResult.Fail($"Chunk {chunkIndex} failed: HTTP {statusCode}", statusCode, failureKind, quotaInfo);
                    }

                    // PERF-04: honor Retry-After on 429/503 (header read only — NOT a wire change).
                    // Supports BOTH forms: Delta (seconds) and an absolute HTTP-date.
                    if (statusCode == 429 || response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                        retryAfterDelay = GetRetryAfterDelay(response);
                    }

                    Logger.Warning($"[ApiClient] Chunk {chunkIndex} attempt {attempt + 1} failed: HTTP {statusCode}");
                }
                catch (OperationCanceledException) when (outerToken.IsCancellationRequested)
                {
                    // REL-04 semantics: the REAL outer token was cancelled (user cancel/shutdown) —
                    // this is a clean cancel, NOT a transient transport fault. Propagate.
                    throw;
                }
                catch (Exception ex) when (
                    ex is HttpRequestException ||
                    ex is IOException ||
                    ex is OperationCanceledException) // per-attempt timeout (outer NOT cancelled — guarded above)
                {
                    // REL-05 / REL-09: a mid-chunk transport fault or a per-attempt stall is
                    // transient — retry the chunk rather than aborting the whole upload.
                    Logger.Warning($"[ApiClient] Chunk {chunkIndex} attempt {attempt + 1} transport fault: {ex.Message}");
                    if (attempt >= MaxChunkRetries - 1)
                    {
                        return UploadResult.Fail($"Chunk {chunkIndex} transport error after {MaxChunkRetries} attempts: {ex.Message}");
                    }
                    // Inter-retry wait: gated on the GROUP-stop token (directive A) -- an already-decided
                    // sibling must be able to cut short a chunk that is merely sleeping between attempts
                    // (nothing dispatched, nothing to lose by stopping here).
                    await Task.Delay(ChunkRetryDelayOverride, groupToken).ConfigureAwait(false);
                    continue;
                }

                // Transient HTTP-status failure -- retry after the (possibly Retry-After) delay, gated
                // on the group-stop token (an inter-retry wait, never an in-flight request).
                if (attempt < MaxChunkRetries - 1)
                {
                    var delay = retryAfterDelay ?? ChunkRetryDelayOverride;
                    Logger.Warning($"[ApiClient] Chunk {chunkIndex} retrying in {delay.TotalMilliseconds:F0}ms...");
                    await Task.Delay(delay, groupToken).ConfigureAwait(false);
                }
            }

            return UploadResult.Fail($"Chunk {chunkIndex} failed after {MaxChunkRetries} attempts");
        }

        /// <summary>
        /// PERF-04: extracts the Retry-After delay from a 429/503 response. Honors BOTH the
        /// delta (seconds) form and the absolute HTTP-date form; for the date form the delay is
        /// (date - now) clamped to non-negative. Returns null when the header is absent so the
        /// caller falls back to the standard backoff. Header read only — not a wire change.
        /// </summary>
        private static TimeSpan? GetRetryAfterDelay(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter == null)
            {
                return null;
            }

            if (retryAfter.Delta.HasValue)
            {
                var delta = retryAfter.Delta.Value;
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }

            if (retryAfter.Date.HasValue)
            {
                var delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            }

            return null;
        }

        /// <summary>
        /// Completes a chunked upload via POST v1/storage/nina/uploads/{uploadId}/complete.
        /// </summary>
        private async Task<UploadResult> CompleteUploadAsync(
            string uploadId, int totalChunks, string apiKey, CancellationToken cancellationToken)
        {
            var payload = JsonConvert.SerializeObject(new { totalChunks });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{ApiPrefix}/uploads/{uploadId}/complete");
            request.Headers.Add("X-API-Key", apiKey);
            request.Headers.Add(PluginVersionHeader.HeaderName, PluginVersionHeader.Value);
            request.Content = content;

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Gone)
                {
                    return UploadResult.Fail("Session expired during complete", 410);
                }

                // Retry-after-complete addendum: the BFF's 300s downstream timeout means OUR OWN
                // retry of `complete` can arrive after the original attempt already finished
                // server-side. A 409 here is the backend's two-outcome answer to that race:
                //   - winner still working  -> 503 upload.completion_in_progress (falls through
                //     below; already classified Transient by FailureClassifier's default -- no
                //     special-casing needed, just a regression pin in the test suite).
                //   - winner finished        -> 409 upload.already_complete -- the file LANDED. This
                //     is the ONLY 409 shape ever treated as success; every other 409 (a different
                //     code, an unparseable/missing body, or a flat non-envelope body) keeps today's
                //     Transient behavior below. Wrong-success would silently drop a frame (forbidden
                //     direction); wrong-retry only costs one extra request -- the asymmetry is
                //     deliberate. Route-specific: this exact-match check exists ONLY here, never on
                //     initiate/chunk/resume-status, whose 409s remain unconditionally Transient.
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    var errorCode = ErrorEnvelopeParser.TryParseErrorCode(body);
                    if (string.Equals(errorCode, CompleteAlreadyCompleteErrorCode, StringComparison.Ordinal))
                    {
                        // The 409 body does not carry the normal completion payload (no
                        // remoteUrl/remotePath) -- mirror the parallel-chunk success path's own
                        // UploadResult.Ok(null) (this same file, chunk PUT success branch): nothing
                        // downstream dereferences RemoteUrl unconditionally (UploadManager.
                        // HandleUploadSuccess takes only the job, never the result).
                        Logger.Info($"[ApiClient] Complete 409 {CompleteAlreadyCompleteErrorCode} -- upload already finished server-side, treating as success");
                        return UploadResult.Ok(null);
                    }
                    // Any other code / unparseable / flat body -- fall through to the standard
                    // classify-and-fail path below (Transient, same as today).
                }

                // G4 drop-point fix: classify instead of collapsing every non-410 status into a
                // generic, un-typed failure.
                var statusCode = (int)response.StatusCode;
                var failureKind = FailureClassifier.Classify(statusCode);
                var quotaInfo = failureKind == FailureKind.PermanentQuota
                    ? QuotaInfoParser.TryParse(body)
                    : null;
                Logger.Warning($"[ApiClient] Complete failed: HTTP {statusCode}");
                return UploadResult.Fail($"Complete failed: HTTP {statusCode}", statusCode, failureKind, quotaInfo);
            }

            var result = JsonConvert.DeserializeObject<CompleteResponse>(body);
            var url = result?.RemoteUrl ?? result?.RemotePath;

            // SEC-04: a 2xx complete with a null/empty url is NOT a real success — the file is not
            // actually retrievable. Treat it as a transient failure (warn + retry) rather than
            // marking the job done with no remote location.
            if (string.IsNullOrEmpty(url))
            {
                Logger.Warning("[ApiClient] Complete returned success status but an empty remote URL; treating as transient failure");
                return UploadResult.Fail("Upload completed without a remote URL");
            }

            // SC3/P19-03: log success state only — never the remote host/URL (job/filename is not in
            // scope in this completion helper, so log a generic state with no remote location).
            Logger.Info("[ApiClient] Chunked upload complete");
            return UploadResult.Ok(url);
        }

        /// <summary>
        /// D-11: the authoritative resume status read from the server's status response — the set of
        /// chunks the server has actually received and the derived missing set. The client reconciles
        /// its local ChunksSent against <see cref="Received"/> and re-sends exactly <see cref="Missing"/>.
        /// </summary>
        private sealed class ResumeStatus
        {
            public HashSet<int> Received { get; init; }
            public HashSet<int> Missing { get; init; }
        }

        /// <summary>
        /// G9/G4 fix: carries one of three outcomes from a resume-status check instead of the old
        /// "null means restart" contract, which conflated 404/410 session-expiry with an
        /// unclassified non-2xx (e.g. 401/403/507 would have been deserialized as if it were a
        /// status body -- G9). <see cref="SessionExpired"/> is true only for the route-specific
        /// 404/410 case; every other non-2xx failure classifies into <see cref="FailureResult"/>.
        /// </summary>
        private sealed class ResumeOutcome
        {
            public ResumeStatus Status { get; init; }
            public bool SessionExpired { get; init; }
            public UploadResult FailureResult { get; init; }
        }

        /// <summary>
        /// Queries the server for the status of an existing upload session.
        /// Returns the authoritative received + missing chunk sets, a session-expired signal
        /// (404/410 -- caller restarts), or a classified failure for every other non-2xx status.
        /// </summary>
        private async Task<ResumeOutcome> GetResumeStatusAsync(
            string uploadId, int totalChunks, string apiKey, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/uploads/{uploadId}/status");
            request.Headers.Add("X-API-Key", apiKey);
            request.Headers.Add(PluginVersionHeader.HeaderName, PluginVersionHeader.Value);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Gone ||
                response.StatusCode == HttpStatusCode.NotFound)
            {
                // Route-specific session-expiry semantics -- NOT globalized through the classifier.
                return new ResumeOutcome { SessionExpired = true };
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // G9 fix: this branch used to fall through and deserialize the body as if it were a
                // success-shaped UploadStatusResponse, regardless of status. Every non-404/410
                // failure (401/403/507/5xx/...) is now classified instead.
                var statusCode = (int)response.StatusCode;
                var failureKind = FailureClassifier.Classify(statusCode);
                var quotaInfo = failureKind == FailureKind.PermanentQuota
                    ? QuotaInfoParser.TryParse(body)
                    : null;
                Logger.Warning($"[ApiClient] Resume status check failed: HTTP {statusCode}");
                return new ResumeOutcome
                {
                    FailureResult = UploadResult.Fail($"Resume status check failed: HTTP {statusCode}", statusCode, failureKind, quotaInfo)
                };
            }

            var status = JsonConvert.DeserializeObject<UploadStatusResponse>(body);
            var received = new HashSet<int>(status?.ReceivedChunks ?? new List<int>());

            var missing = new HashSet<int>();
            for (int i = 0; i < totalChunks; i++)
            {
                if (!received.Contains(i)) missing.Add(i);
            }

            Logger.Info($"[ApiClient] Resume: {received.Count} of {totalChunks} chunks already uploaded (server-authoritative)");
            return new ResumeOutcome { Status = new ResumeStatus { Received = received, Missing = missing } };
        }

        public void Dispose()
        {
            httpClient?.Dispose();
        }

        // ====================================================================
        // Response DTOs
        // ====================================================================

        private class UploadApiResponse
        {
            public bool? Success { get; set; }
            public string RemoteUrl { get; set; }
            public string RemotePath { get; set; }
            public long? FileSize { get; set; }
            public string ErrorMessage { get; set; }
        }

        private class InitiateResponse
        {
            public string UploadId { get; set; }
            public int ChunkSize { get; set; }
            public int TotalChunks { get; set; }
            public string ExpiresAt { get; set; }
        }

        private class CompleteResponse
        {
            public string RemoteUrl { get; set; }
            public string RemotePath { get; set; }
            public long FileSize { get; set; }
        }

        private class UploadStatusResponse
        {
            public string UploadId { get; set; }
            public string Status { get; set; }
            public List<int> ReceivedChunks { get; set; }
            public int TotalChunks { get; set; }
            public string ExpiresAt { get; set; }
        }
    }

    /// <summary>
    /// Stream wrapper that reports read progress.
    /// </summary>
    internal class ProgressStream : Stream
    {
        private readonly Stream innerStream;
        private readonly long totalLength;
        private readonly IProgress<double> progress;
        private long bytesRead;

        public ProgressStream(Stream inner, long length, IProgress<double> progress)
        {
            innerStream = inner;
            totalLength = length;
            this.progress = progress;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = innerStream.Read(buffer, offset, count);
            bytesRead += read;

            if (totalLength > 0)
            {
                // SEC-06: clamp so single-file progress can never exceed 100% (the chunked path
                // already clamps). Guards against a stream that reads slightly past the reported length.
                progress?.Report(Math.Min((double)bytesRead / totalLength, 1.0));
            }

            return read;
        }

        public override bool CanRead => innerStream.CanRead;
        public override bool CanSeek => innerStream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => innerStream.Length;

        public override long Position
        {
            get => innerStream.Position;
            set => innerStream.Position = value;
        }

        public override void Flush() => innerStream.Flush();
        public override long Seek(long offset, SeekOrigin origin) => innerStream.Seek(offset, origin);
        public override void SetLength(long value) => innerStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
