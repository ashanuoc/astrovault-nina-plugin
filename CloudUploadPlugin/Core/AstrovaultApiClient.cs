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

        public string BaseUrl { get; }

        // --- Test seams (InternalsVisibleTo CloudUploadPlugin.Tests) -----------------
        // Allow the resilience tests to shrink the per-attempt stall timeout and the
        // inter-attempt retry delay so they run fast. Production uses the defaults.
        internal TimeSpan PerAttemptChunkTimeout { get; set; } = DefaultPerAttemptChunkTimeout;
        internal TimeSpan ChunkRetryDelayOverride { get; set; } = TimeSpan.FromMilliseconds(ChunkRetryDelayMs);

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
            catch (OperationCanceledException)
            {
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
            request.Content = content;

            Logger.Info($"[ApiClient] Single upload: {fileName} ({fileSize} bytes)");

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning($"[ApiClient] Upload failed: {response.StatusCode}");
                // SC6: build the error from the NUMERIC status only. Server-controllable ReasonPhrase
                // must not flow into persisted/user-facing error text (the ToActionableError mapper keys
                // off the number, not the phrase).
                return UploadResult.Fail($"HTTP {(int)response.StatusCode}", (int)response.StatusCode);
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

                var resume = await GetResumeStatusAsync(job.UploadSessionId, job.TotalChunks, apiKey, cancellationToken).ConfigureAwait(false);

                if (resume == null)
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

                var initResult = await InitiateUploadAsync(job, apiKey, cancellationToken).ConfigureAwait(false);
                if (initResult == null)
                {
                    return UploadResult.Fail("Failed to initiate chunked upload");
                }

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

            // Shared outcome flags (set under groupResultLock).
            var groupResultLock = new object();
            bool sessionExpired = false;   // a chunk returned 410 Gone
            UploadResult fatalResult = null; // a chunk failed non-transiently (not 410)

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
                        job.UploadSessionId, chunkIndex, chunkBytes, chunkHash, apiKey, groupCts.Token).ConfigureAwait(false);

                    if (chunkResult.StatusCode == 410)
                    {
                        // (3) Signal a session expiry and cancel siblings; the orchestrator drains then restarts.
                        lock (groupResultLock) { sessionExpired = true; }
                        groupCts.Cancel();
                        return;
                    }

                    if (!chunkResult.Success)
                    {
                        lock (groupResultLock)
                        {
                            fatalResult ??= UploadResult.Fail($"Chunk {chunkIndex} upload failed: {chunkResult.ErrorMessage}");
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
            // deterministically -- a 410 session-expiry restart takes precedence over a fatal result
            // (a 410 is recoverable; we only surface the fatal if the group did not also expire).
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

            if (fatalResult != null)
            {
                return fatalResult;
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
        /// Initiates a chunked upload session via POST v1/storage/nina/uploads/initiate.
        /// </summary>
        private async Task<InitiateResponse> InitiateUploadAsync(
            UploadJob job, string apiKey, CancellationToken cancellationToken)
        {
            using var content = new MultipartFormDataContent();
            content.Add(new StringContent(Path.GetFileName(job.LocalPath)), "fileName");
            content.Add(new StringContent(job.FileSize.ToString()), "fileSize");
            content.Add(new StringContent(job.RelativePath ?? ""), "remotePath");
            content.Add(new StringContent(job.MetadataJson ?? "{}"), "metadata");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiPrefix}/uploads/initiate");
            request.Headers.Add("X-API-Key", apiKey);
            request.Content = content;

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // SC3/P19-03: status only — never log the raw server response body.
                Logger.Warning($"[ApiClient] Initiate failed: HTTP {(int)response.StatusCode}");
                return null;
            }

            var result = JsonConvert.DeserializeObject<InitiateResponse>(body);
            Logger.Info($"[ApiClient] Chunked upload initiated: {result?.UploadId}, {result?.TotalChunks} chunks");
            return result;
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
            string apiKey, CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < MaxChunkRetries; attempt++)
            {
                // REL-05: an HttpRequestMessage (and its content) cannot be re-sent, so the
                // request + content MUST be re-created on every attempt — otherwise a retry
                // after a transport fault would throw InvalidOperationException.
                using var content = new ByteArrayContent(chunkBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                using var request = new HttpRequestMessage(HttpMethod.Put,
                    $"{ApiPrefix}/uploads/{uploadId}/chunks/{chunkIndex}");
                request.Headers.Add("X-API-Key", apiKey);
                request.Headers.Add("X-Chunk-Hash", chunkHash);
                request.Content = content;

                // REL-09: keep the generous outer timeout but bound THIS attempt with a per-attempt
                // linked CTS so a stalled chunk (hung socket) is detected early and retried instead
                // of waiting out the whole upload's outer timeout.
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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

                    // PERF-04: honor Retry-After on 429/503 (header read only — NOT a wire change).
                    // Supports BOTH forms: Delta (seconds) and an absolute HTTP-date.
                    if ((int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                        retryAfterDelay = GetRetryAfterDelay(response);
                    }

                    Logger.Warning($"[ApiClient] Chunk {chunkIndex} attempt {attempt + 1} failed: HTTP {(int)response.StatusCode}");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // REL-04 semantics: the OUTER token was cancelled (user cancel) — this is a
                    // clean cancel, NOT a transient transport fault. Propagate.
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
                    await Task.Delay(ChunkRetryDelayOverride, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Transient HTTP-status failure -- retry after the (possibly Retry-After) delay
                if (attempt < MaxChunkRetries - 1)
                {
                    var delay = retryAfterDelay ?? ChunkRetryDelayOverride;
                    Logger.Warning($"[ApiClient] Chunk {chunkIndex} retrying in {delay.TotalMilliseconds:F0}ms...");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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
            request.Content = content;

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Gone)
                {
                    return UploadResult.Fail("Session expired during complete", 410);
                }
                return UploadResult.Fail($"Complete failed: HTTP {(int)response.StatusCode}");
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
        /// Queries the server for the status of an existing upload session.
        /// Returns the authoritative received + missing chunk sets, or null if the session is
        /// expired/not found (caller restarts).
        /// </summary>
        private async Task<ResumeStatus> GetResumeStatusAsync(
            string uploadId, int totalChunks, string apiKey, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiPrefix}/uploads/{uploadId}/status");
            request.Headers.Add("X-API-Key", apiKey);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Gone ||
                response.StatusCode == HttpStatusCode.NotFound)
            {
                return null; // Signal: need fresh session
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var status = JsonConvert.DeserializeObject<UploadStatusResponse>(body);
            var received = new HashSet<int>(status?.ReceivedChunks ?? new List<int>());

            var missing = new HashSet<int>();
            for (int i = 0; i < totalChunks; i++)
            {
                if (!received.Contains(i)) missing.Add(i);
            }

            Logger.Info($"[ApiClient] Resume: {received.Count} of {totalChunks} chunks already uploaded (server-authoritative)");
            return new ResumeStatus { Received = received, Missing = missing };
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
