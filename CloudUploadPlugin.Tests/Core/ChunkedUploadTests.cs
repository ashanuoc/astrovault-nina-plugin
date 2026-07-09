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
using Astrovault.Core;
using Astrovault.Interfaces;
using Astrovault.Models;
using CloudUploadPlugin.Tests.Helpers;
using Moq;
using Newtonsoft.Json;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Tests for the chunked upload engine in AstrovaultApiClient.
    /// Covers size-based routing, chunked protocol (initiate/chunks/complete),
    /// per-chunk SHA-256 hashing, per-chunk retry, and progress reporting.
    /// </summary>
    [TestFixture]
    [Category("ChunkedUpload")]
    public class ChunkedUploadTests
    {
        private MockHttpMessageHandler mockHandler;
        private Mock<IAuthManager> mockAuthManager;
        private AstrovaultApiClient sut;
        private string tempFilePath;

        [SetUp]
        public void SetUp()
        {
            mockHandler = new MockHttpMessageHandler();
            mockAuthManager = new Mock<IAuthManager>();
            mockAuthManager.Setup(a => a.GetApiKey()).Returns("test-api-key");

            sut = new AstrovaultApiClient(mockAuthManager.Object, "https://api.test.io", mockHandler);
        }

        [TearDown]
        public void TearDown()
        {
            sut?.Dispose();
            mockHandler?.Dispose();
            if (tempFilePath != null && File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }

        private string CreateTempFile(long sizeInBytes)
        {
            tempFilePath = Path.GetTempFileName();
            var random = new Random(42); // Deterministic seed for reproducible tests
            var buffer = new byte[Math.Min(sizeInBytes, 65536)];
            using var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write);
            long written = 0;
            while (written < sizeInBytes)
            {
                var toWrite = (int)Math.Min(buffer.Length, sizeInBytes - written);
                random.NextBytes(buffer);
                fs.Write(buffer, 0, toWrite);
                written += toWrite;
            }
            return tempFilePath;
        }

        // ====================================================================
        // Size Routing Tests
        // ====================================================================

        [Test]
        public async Task SizeRouting_SmallFile_UsesSingleUpload()
        {
            // Arrange: File < 5 MB (4 MB)
            var filePath = CreateTempFile(4 * 1024 * 1024);
            var job = TestDataFactory.CreateUploadJob(localPath: filePath);
            job.FileSize = 4 * 1024 * 1024;

            // Mock single upload response
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { success = true, remoteUrl = "https://cdn.test.io/file.fits" }),
                    Encoding.UTF8, "application/json")
            });

            // Act
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert: should hit single upload path POST /v1/storage/nina/images/upload
            Assert.That(result.Success, Is.True);
            Assert.That(mockHandler.Requests.Count, Is.EqualTo(1));
            Assert.That(mockHandler.Requests[0].Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(mockHandler.Requests[0].RequestUri.PathAndQuery, Does.Contain("v1/storage/nina/images/upload"));
        }

        [Test]
        public async Task SizeRouting_LargeFile_UsesChunkedUpload()
        {
            // Arrange: File >= 5 MB (exactly 5 MB = 1 chunk)
            var filePath = CreateTempFile(5 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 5 * 1024 * 1024);

            // Mock initiate response
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId = "sess-001", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });
            // Mock chunk 0 response
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { chunkIndex = 0, received = true }),
                    Encoding.UTF8, "application/json")
            });
            // Mock complete response
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "M31/Light/001.fits", fileSize = 5242880L }),
                    Encoding.UTF8, "application/json")
            });

            // Act
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert: should hit chunked upload path POST /v1/storage/nina/uploads/initiate
            Assert.That(result.Success, Is.True);
            Assert.That(mockHandler.Requests.Count, Is.EqualTo(3));
            Assert.That(mockHandler.Requests[0].RequestUri.PathAndQuery, Does.Contain("v1/storage/nina/uploads/initiate"));
        }

        // ====================================================================
        // Chunked Protocol Tests
        // ====================================================================

        [Test]
        public async Task ChunkedUpload_SendsCorrectInitiateRequest()
        {
            // Arrange: 10 MB file = 2 chunks
            var filePath = CreateTempFile(10 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 10 * 1024 * 1024);

            EnqueueFullChunkedFlow(2, "sess-002");

            // Act
            await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert: initiate request
            var initReq = mockHandler.Requests[0];
            Assert.That(initReq.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(initReq.RequestUri.PathAndQuery, Does.Contain("v1/storage/nina/uploads/initiate"));
            Assert.That(initReq.Headers.GetValues("X-API-Key").First(), Is.EqualTo("test-api-key"));

            // Verify multipart form content
            var contentString = await mockHandler.RequestBodies[0];
            Assert.That(contentString, Does.Contain("fileName"));
            Assert.That(contentString, Does.Contain("fileSize"));
            Assert.That(contentString, Does.Contain("remotePath"));
        }

        [Test]
        public async Task ChunkedUpload_SendsCorrectChunkRequests()
        {
            // Arrange: 10 MB file = 2 chunks
            var filePath = CreateTempFile(10 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 10 * 1024 * 1024);

            EnqueueFullChunkedFlow(2, "sess-003");

            // Act
            await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert: chunk requests use PUT with correct URL paths
            var chunk0Req = mockHandler.Requests[1];
            Assert.That(chunk0Req.Method, Is.EqualTo(HttpMethod.Put));
            Assert.That(chunk0Req.RequestUri.PathAndQuery, Does.Contain("v1/storage/nina/uploads/sess-003/chunks/0"));
            Assert.That(chunk0Req.Content.Headers.ContentType.MediaType, Is.EqualTo("application/octet-stream"));

            var chunk1Req = mockHandler.Requests[2];
            Assert.That(chunk1Req.Method, Is.EqualTo(HttpMethod.Put));
            Assert.That(chunk1Req.RequestUri.PathAndQuery, Does.Contain("v1/storage/nina/uploads/sess-003/chunks/1"));
        }

        [Test]
        public async Task ChunkedUpload_ChunkHashIsCorrectSha256()
        {
            // Arrange: 5 MB file = 1 chunk (exact)
            var filePath = CreateTempFile(5 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 5 * 1024 * 1024);

            EnqueueFullChunkedFlow(1, "sess-004");

            // Act
            await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert: X-Chunk-Hash header matches SHA-256 of actual file bytes
            var chunkReq = mockHandler.Requests[1];
            var hashHeader = chunkReq.Headers.GetValues("X-Chunk-Hash").First();
            Assert.That(hashHeader, Does.StartWith("sha256="));

            // Compute expected hash from temp file
            var fileBytes = File.ReadAllBytes(filePath);
            var expectedHash = "sha256=" + Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
            Assert.That(hashHeader, Is.EqualTo(expectedHash));
        }

        [Test]
        public async Task ChunkedUpload_LastChunkSmallerThanChunkSize()
        {
            // Arrange: 7 MB file = 2 chunks (5 MB + 2 MB)
            long fileSize = 7 * 1024 * 1024;
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);

            EnqueueFullChunkedFlow(2, "sess-005");

            // Act
            await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert: last chunk body is 2 MB, not 5 MB
            var lastChunkBody = mockHandler.RequestBodyBytes[2]; // chunk index 1
            Assert.That(lastChunkBody.Length, Is.EqualTo(2 * 1024 * 1024));
        }

        [Test]
        public async Task ChunkedUpload_CompleteSendsCorrectTotalChunks()
        {
            // Arrange: 15 MB file = 3 chunks
            long fileSize = 15 * 1024 * 1024;
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);

            EnqueueFullChunkedFlow(3, "sess-006");

            // Act
            await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert: complete request sends totalChunks
            var completeReq = mockHandler.Requests[4]; // initiate + 3 chunks + complete
            Assert.That(completeReq.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(completeReq.RequestUri.PathAndQuery, Does.Contain("v1/storage/nina/uploads/sess-006/complete"));

            var completeBody = await mockHandler.RequestBodies[4];
            Assert.That(completeBody, Does.Contain("\"totalChunks\":3").Or.Contain("\"totalChunks\": 3"));
        }

        [Test]
        public async Task ChunkedUpload_SuccessReturnsOkWithRemoteUrl()
        {
            // Arrange: 5 MB file = 1 chunk
            var filePath = CreateTempFile(5 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 5 * 1024 * 1024);

            EnqueueFullChunkedFlow(1, "sess-007");

            // Act
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(result.RemoteUrl, Is.EqualTo("https://cdn.test.io/file.fits"));
        }

        // ====================================================================
        // Per-Chunk Retry Tests
        // ====================================================================

        [Test]
        public async Task ChunkRetry_TransientFailure_RetriesUpToMax()
        {
            // Arrange: 5 MB file = 1 chunk, all 3 retries fail with 500
            var filePath = CreateTempFile(5 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 5 * 1024 * 1024);

            // Initiate succeeds
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId = "sess-retry", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });
            // Chunk upload fails 3 times with 500
            for (int i = 0; i < 3; i++)
            {
                mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"server.internal\",\"message\":\"Internal error\"}}")
                });
            }

            // Act
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert: fails after 3 retries
            Assert.That(result.Success, Is.False);
            // Initiate (1) + 3 chunk retries = 4 requests
            Assert.That(mockHandler.Requests.Count, Is.EqualTo(4));
        }

        [Test]
        public async Task ChunkRetry_SessionExpired_DoesNotRetry()
        {
            // Arrange: 5 MB file = 1 chunk, chunk returns 410 Gone
            var filePath = CreateTempFile(5 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 5 * 1024 * 1024);
            job.SessionRestartCount = 10; // Already at the (D-10 raised) max restarts

            // Initiate succeeds
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId = "sess-expired", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });
            // Chunk returns 410 Gone (session expired)
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.Gone)
            {
                Content = new StringContent("{\"error\":{\"code\":\"upload.expired\",\"message\":\"Session expired\"}}")
            });

            // Act
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert: 410 is NOT retried at chunk level; and since session restart count is at max, returns permanent failure
            Assert.That(result.Success, Is.False);
            Assert.That(result.IsPermanent, Is.True);
            // Only 2 requests: initiate + 1 chunk attempt (no retry)
            Assert.That(mockHandler.Requests.Count, Is.EqualTo(2));
        }

        // ====================================================================
        // Resilience Tests (REL-05 transport retry, REL-09 stall timeout, PERF-04 Retry-After)
        // ====================================================================

        /// <summary>
        /// REL-05: a mid-chunk transport fault (HttpRequestException) on the first attempt
        /// retries the same chunk rather than aborting the whole upload, and succeeds on the
        /// next attempt.
        /// </summary>
        [Test]
        [Category("Resilience")]
        public async Task Resilience_ChunkTransportFault_HttpRequestException_RetriesAndSucceeds()
        {
            var filePath = CreateTempFile(5 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 5 * 1024 * 1024);
            sut.ChunkRetryDelayOverride = TimeSpan.FromMilliseconds(1); // keep the test fast

            // Initiate succeeds
            mockHandler.Enqueue(OkJson(new { uploadId = "sess-tf", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }));
            // Chunk attempt 1: transport fault
            mockHandler.EnqueueThrow(new HttpRequestException("connection reset"));
            // Chunk attempt 2: success
            mockHandler.Enqueue(OkJson(new { chunkIndex = 0, received = true }));
            // Complete succeeds
            mockHandler.Enqueue(OkJson(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "M31/Light/001.fits", fileSize = 5242880L }));

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.True, "transport fault on a chunk should retry, not abort the upload");
            // initiate + chunk(fault) + chunk(retry) + complete = 4 requests
            Assert.That(mockHandler.Requests.Count, Is.EqualTo(4));
        }

        /// <summary>
        /// REL-05: an IOException mid-chunk is also treated as a transient transport fault
        /// and retried.
        /// </summary>
        [Test]
        [Category("Resilience")]
        public async Task Resilience_ChunkTransportFault_IOException_RetriesAndSucceeds()
        {
            var filePath = CreateTempFile(5 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 5 * 1024 * 1024);
            sut.ChunkRetryDelayOverride = TimeSpan.FromMilliseconds(1);

            mockHandler.Enqueue(OkJson(new { uploadId = "sess-io", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }));
            mockHandler.EnqueueThrow(new IOException("socket closed"));
            mockHandler.Enqueue(OkJson(new { chunkIndex = 0, received = true }));
            mockHandler.Enqueue(OkJson(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "M31/Light/001.fits", fileSize = 5242880L }));

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.True, "IOException on a chunk should retry, not abort");
            Assert.That(mockHandler.Requests.Count, Is.EqualTo(4));
        }

        /// <summary>
        /// PERF-04: a 429 with Retry-After as a DELTA (seconds) honors that value as the
        /// retry delay. We assert the elapsed time is at least the header value.
        /// </summary>
        [Test]
        [Category("Resilience")]
        public async Task Resilience_RetryAfter_Delta_HonoredForRetryDelay()
        {
            var filePath = CreateTempFile(5 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 5 * 1024 * 1024);

            mockHandler.Enqueue(OkJson(new { uploadId = "sess-ra-d", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }));
            // Chunk attempt 1: 429 with Retry-After: 1 (one second, delta form)
            var throttled = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("{\"error\":{\"code\":\"rate.limited\"}}")
            };
            throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            mockHandler.Enqueue(throttled);
            // Chunk attempt 2: success
            mockHandler.Enqueue(OkJson(new { chunkIndex = 0, received = true }));
            mockHandler.Enqueue(OkJson(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "M31/Light/001.fits", fileSize = 5242880L }));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);
            sw.Stop();

            Assert.That(result.Success, Is.True);
            Assert.That(sw.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900)),
                "Retry-After delta (1s) should have been honored as the retry delay");
        }

        /// <summary>
        /// PERF-04: a 503 with Retry-After as an ABSOLUTE HTTP-date honors (date - now)
        /// (clamped to >= 0) as the retry delay.
        /// </summary>
        [Test]
        [Category("Resilience")]
        public async Task Resilience_RetryAfter_AbsoluteDate_HonoredForRetryDelay()
        {
            var filePath = CreateTempFile(5 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 5 * 1024 * 1024);

            mockHandler.Enqueue(OkJson(new { uploadId = "sess-ra-abs", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }));
            // Chunk attempt 1: 503 with Retry-After: <HTTP-date ~1s in the future>
            var throttled = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":{\"code\":\"service.unavailable\"}}")
            };
            throttled.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(1));
            mockHandler.Enqueue(throttled);
            mockHandler.Enqueue(OkJson(new { chunkIndex = 0, received = true }));
            mockHandler.Enqueue(OkJson(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "M31/Light/001.fits", fileSize = 5242880L }));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);
            sw.Stop();

            Assert.That(result.Success, Is.True);
            Assert.That(sw.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(700)),
                "Retry-After absolute date (~1s) should have been honored as the retry delay");
        }

        /// <summary>
        /// REL-09: a chunk that stalls past the per-attempt timeout is cancelled and retried,
        /// while the overall upload (governed by the generous outer timeout) is NOT killed.
        /// </summary>
        [Test]
        [Category("Resilience")]
        public async Task Resilience_StalledChunk_PerAttemptTimeout_CancelsAndRetries()
        {
            var filePath = CreateTempFile(5 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 5 * 1024 * 1024);
            // Small per-attempt timeout to keep the test fast, but generous enough that the
            // retried (immediate) success attempt is never itself timed out when the test host's
            // thread pool is saturated by the rest of the parallel suite. The stall is indefinite,
            // so any value far below the mock's outer timeout still exercises stall→timeout→retry.
            sut.PerAttemptChunkTimeout = TimeSpan.FromMilliseconds(1000);
            sut.ChunkRetryDelayOverride = TimeSpan.FromMilliseconds(1);

            mockHandler.Enqueue(OkJson(new { uploadId = "sess-stall", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }));
            // Chunk attempt 1: stall (hangs until per-attempt timeout fires)
            mockHandler.EnqueueStall();
            // Chunk attempt 2: success
            mockHandler.Enqueue(OkJson(new { chunkIndex = 0, received = true }));
            mockHandler.Enqueue(OkJson(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "M31/Light/001.fits", fileSize = 5242880L }));

            // The OUTER token is never cancelled; only the per-attempt timeout should fire.
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.True, "a stalled chunk should be retried, not kill the whole upload");
            Assert.That(mockHandler.Requests.Count, Is.EqualTo(4), "initiate + stalled chunk + retried chunk + complete");
        }

        /// <summary>
        /// REL-04 boundary: when the OUTER cancellation token IS requested, a chunk
        /// TaskCanceledException is NOT treated as a transient transport fault — it
        /// propagates as a clean cancellation (UploadFileAsync returns the cancel result).
        /// </summary>
        [Test]
        [Category("Resilience")]
        public async Task Resilience_OuterCancellation_NotTreatedAsTransientFault()
        {
            var filePath = CreateTempFile(5 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 5 * 1024 * 1024);

            mockHandler.Enqueue(OkJson(new { uploadId = "sess-cancel", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }));
            // Chunk attempt 1: stall — will be cancelled by the OUTER token below.
            mockHandler.EnqueueStall();
            // No retry should occur: the next chunk response is NEVER consumed.
            mockHandler.Enqueue(OkJson(new { chunkIndex = 0, received = true }));

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(150));

            var result = await sut.UploadFileAsync(job, null, cts.Token);

            Assert.That(result.Success, Is.False, "user cancellation should not be retried");
            // initiate + the single stalled chunk attempt; the retry response is never used.
            Assert.That(mockHandler.Requests.Count, Is.EqualTo(2),
                "outer cancellation must not trigger a per-chunk retry");
        }

        /// <summary>
        /// D-10: MaxSessionRestarts is raised generously (to 10) so a session expiry at a
        /// restart count that the OLD cap (2) would have rejected still triggers a fresh
        /// re-initiate instead of a permanent failure.
        /// </summary>
        [Test]
        [Category("Resilience")]
        public async Task Resilience_SessionExpired_AboveOldCap_StillRestarts()
        {
            var filePath = CreateTempFile(5 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 5 * 1024 * 1024);
            job.SessionRestartCount = 3; // above the OLD cap of 2, below the NEW cap of 10

            // Initiate (1st session) succeeds
            mockHandler.Enqueue(OkJson(new { uploadId = "sess-d10-a", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }));
            // Chunk returns 410 Gone -> should trigger a restart (NOT permanent fail, since 3 < 10)
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.Gone)
            {
                Content = new StringContent("{\"error\":{\"code\":\"upload.expired\"}}")
            });
            // Restart: fresh initiate + chunk + complete all succeed
            mockHandler.Enqueue(OkJson(new { uploadId = "sess-d10-b", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }));
            mockHandler.Enqueue(OkJson(new { chunkIndex = 0, received = true }));
            mockHandler.Enqueue(OkJson(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "M31/Light/001.fits", fileSize = 5242880L }));

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.True, "a restart count of 3 must still restart under the raised D-10 cap");
            Assert.That(result.IsPermanent, Is.False);
            Assert.That(job.SessionRestartCount, Is.EqualTo(4), "the restart should have incremented the counter");
        }

        /// <summary>
        /// REL-11: a freshly-saved file that is momentarily locked (exclusive lock released after
        /// a short delay) is opened successfully via the bounded Retry.Do wrapping the upload-site
        /// file read, rather than failing on the first attempt.
        /// </summary>
        [Test]
        [Category("Resilience")]
        public async Task Resilience_MomentarilyLockedFile_OpenedViaRetry()
        {
            var filePath = CreateTempFile(5 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 5 * 1024 * 1024);

            EnqueueFullChunkedFlow(1, "sess-lock");

            // Hold an exclusive lock on the file, then release it shortly after the upload starts.
            var exclusive = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var releaser = Task.Run(async () =>
            {
                await Task.Delay(300);
                exclusive.Dispose(); // release so a retry can open it
            });

            // Act: upload should open the file via Retry.Do after the lock clears.
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);
            await releaser;

            Assert.That(result.Success, Is.True,
                "a momentarily-locked freshly-saved file should open via bounded retry, not fail immediately");
        }

        /// <summary>Convenience: builds an OK JSON response.</summary>
        private static HttpResponseMessage OkJson(object payload)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json")
            };
        }

        // ====================================================================
        // SEC-02: ChunksSent thread-safety (prerequisite for parallel chunks)
        // ====================================================================

        /// <summary>
        /// SEC-02: concurrent writes to job.ChunksSent via the client's guarded record helper,
        /// while another thread repeatedly snapshots/serializes the list, must NOT throw
        /// "Collection was modified" and must produce a consistent final set.
        /// </summary>
        [Test]
        [Category("ProgressStatus")]
        public void ChunksSent_ConcurrentRecordAndSnapshot_NoTornReadOrThrow()
        {
            var job = TestDataFactory.CreateChunkedUploadJob(fileSize: 5L * 1024 * 1024 * 200);
            job.ChunksSent = new List<int>();
            const int chunkCount = 500;

            Exception captured = null;

            // Writer: record chunks 0..chunkCount-1 under the client's lock.
            var writer = Task.Run(() =>
            {
                for (int i = 0; i < chunkCount; i++)
                {
                    sut.RecordChunkSent(job, i);
                }
            });

            // Reader: continuously snapshot + serialize while the writer mutates.
            var reader = Task.Run(() =>
            {
                try
                {
                    for (int iter = 0; iter < 2000; iter++)
                    {
                        var snapshot = sut.SnapshotChunksSent(job);
                        // Enumerate + serialize the snapshot (must be a private copy, never the live list).
                        _ = JsonConvert.SerializeObject(snapshot);
                        _ = snapshot.Count;
                    }
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
            });

            Task.WaitAll(writer, reader);

            Assert.That(captured, Is.Null,
                $"Concurrent snapshot/serialize threw: {captured}");
            var final = sut.SnapshotChunksSent(job);
            Assert.That(final.Count, Is.EqualTo(chunkCount));
            Assert.That(final.Distinct().Count(), Is.EqualTo(chunkCount), "no duplicates / torn writes");
        }

        /// <summary>
        /// SEC-02: the guarded record helper preserves exact sequential semantics — recording
        /// chunks 0..n in order yields the same ordered list the old direct Add produced.
        /// </summary>
        [Test]
        [Category("ProgressStatus")]
        public void ChunksSent_SequentialRecord_PreservesOrderAndContent()
        {
            var job = TestDataFactory.CreateChunkedUploadJob(fileSize: 15L * 1024 * 1024);
            job.ChunksSent = new List<int>();

            for (int i = 0; i < 3; i++)
            {
                sut.RecordChunkSent(job, i);
            }

            Assert.That(job.ChunksSent, Is.EqualTo(new List<int> { 0, 1, 2 }));
            Assert.That(sut.SnapshotChunksSent(job), Is.EqualTo(new List<int> { 0, 1, 2 }));
        }

        // ====================================================================
        // SEC-04 / SEC-05 / SEC-06: correctness guards
        // ====================================================================

        /// <summary>
        /// SEC-04: a chunked complete that returns 2xx but a null/empty remote URL must NOT be
        /// reported as success — it is a transient failure (the file isn't actually retrievable).
        /// </summary>
        [Test]
        [Category("ChunkedUpload")]
        public async Task CompleteUpload_NullRemoteUrl_DoesNotReportSuccess()
        {
            var filePath = CreateTempFile(5 * 1024 * 1024);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: 5 * 1024 * 1024);

            // Initiate + chunk OK
            mockHandler.Enqueue(OkJson(new { uploadId = "sess-nullurl", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }));
            mockHandler.Enqueue(OkJson(new { chunkIndex = 0, received = true }));
            // Complete returns success status but NO url (null remoteUrl AND null remotePath).
            mockHandler.Enqueue(OkJson(new { remoteUrl = (string)null, remotePath = (string)null, fileSize = 5242880L }));

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.False, "a complete with an empty URL must not be a success");
            Assert.That(result.IsPermanent, Is.False, "empty-URL complete is transient (retryable)");
        }

        /// <summary>
        /// SEC-05: when the file's on-disk length differs from the queue-time FileSize, the chunk
        /// count is recomputed from the CURRENT length at upload start. Here the queue-time FileSize
        /// says 5 MB (1 chunk) but the file on disk is 12 MB (3 chunks) — the engine must upload 3
        /// chunks and send the current size on initiate.
        /// </summary>
        [Test]
        [Category("ChunkedUpload")]
        public async Task ChunkedUpload_FileGrewSinceQueue_RecomputesChunkCountFromCurrentLength()
        {
            long currentSize = 12L * 1024 * 1024; // 3 chunks on disk
            var filePath = CreateTempFile(currentSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: currentSize);
            job.FileSize = 5 * 1024 * 1024; // STALE queue-time size (would imply 1 chunk)

            // Server echoes the (re-stat) size by returning 3 chunks; engine must send 3 chunks.
            mockHandler.UseIndexedRouting = true;
            mockHandler.EnqueueInitiate("sess-grew", totalChunks: 3);
            for (int i = 0; i < 3; i++) mockHandler.EnqueueChunkOk(i);
            mockHandler.EnqueueComplete();

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.True);
            // Initiate was sent with the CURRENT length, not the stale 5 MB.
            var initBody = await mockHandler.RequestBodies[0];
            Assert.That(initBody, Does.Contain(currentSize.ToString()),
                "initiate must send the re-stat current file length, not the stale queue-time size");
            // All 3 chunks (the current-length chunk count) were uploaded.
            var puts = mockHandler.Requests.Count(r => r.Method == HttpMethod.Put);
            Assert.That(puts, Is.EqualTo(3));
            Assert.That(job.ChunksSent.OrderBy(i => i), Is.EqualTo(new[] { 0, 1, 2 }));
        }

        /// <summary>
        /// SEC-06: single-file (< 5 MB) progress is clamped so the reported fraction never exceeds
        /// 100%, even if the progress stream reads slightly past the reported length.
        /// </summary>
        [Test]
        [Category("ProgressStatus")]
        public async Task SingleFileUpload_Progress_ClampedAtOneHundredPercent()
        {
            var filePath = CreateTempFile(4 * 1024 * 1024); // < 5 MB -> single upload
            var job = TestDataFactory.CreateUploadJob(localPath: filePath);
            job.FileSize = 4 * 1024 * 1024;

            mockHandler.Enqueue(OkJson(new { success = true, remoteUrl = "https://cdn.test.io/file.fits" }));

            var progressValues = new List<double>();
            var progress = new Progress<double>(v => progressValues.Add(v));

            var result = await sut.UploadFileAsync(job, progress, CancellationToken.None);
            await Task.Delay(100);

            Assert.That(result.Success, Is.True);
            Assert.That(progressValues, Is.Not.Empty);
            Assert.That(progressValues.All(v => v <= 1.0 + 1e-9), Is.True,
                "single-file progress must be clamped to <= 100%");
        }

        // ====================================================================
        // PERF-03: bounded parallel chunk uploads (ParallelChunk)
        // ====================================================================

        /// <summary>
        /// PERF-03 sequential-equivalence: at concurrency = 1 the effective in-flight count never
        /// exceeds 1 and ChunksSent ends up identical to the sequential 0..n order.
        /// </summary>
        [Test]
        [Category("ParallelChunk")]
        public async Task ParallelChunk_ConcurrencyOne_IsSequentialEquivalent()
        {
            long fileSize = 25L * 1024 * 1024; // 5 chunks
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);
            sut.ChunkConcurrencyOverride = 1;

            EnqueueFullChunkedFlow(5, "sess-seq");

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.True);
            Assert.That(sut.LastObservedMaxInFlight, Is.EqualTo(1),
                "concurrency=1 must never run more than one chunk in flight");
            Assert.That(job.ChunksSent.OrderBy(i => i), Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
        }

        /// <summary>
        /// PERF-03: at concurrency = 3, all chunks complete with none dropped/duplicated, and the
        /// engine actually runs more than one chunk in flight.
        /// </summary>
        [Test]
        [Category("ParallelChunk")]
        public async Task ParallelChunk_ConcurrencyThree_AllChunksUploadedNoneDuplicated()
        {
            long fileSize = 40L * 1024 * 1024; // 8 chunks
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);
            sut.ChunkConcurrencyOverride = 3;

            // Route by chunk index; add a small per-chunk delay so tasks genuinely overlap.
            mockHandler.UseIndexedRouting = true;
            mockHandler.EnqueueInitiate("sess-par", totalChunks: 8);
            for (int i = 0; i < 8; i++) mockHandler.EnqueueChunkOk(i, delayMs: 40);
            mockHandler.EnqueueComplete();

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.True);
            Assert.That(sut.LastObservedMaxInFlight, Is.GreaterThan(1),
                "concurrency=3 should overlap chunk sends");
            Assert.That(sut.LastObservedMaxInFlight, Is.LessThanOrEqualTo(3),
                "concurrency must be bounded by the configured maximum");
            Assert.That(job.ChunksSent.OrderBy(i => i), Is.EqualTo(Enumerable.Range(0, 8)),
                "every chunk uploaded exactly once, none dropped or duplicated");
            Assert.That(job.ChunksSent.Distinct().Count(), Is.EqualTo(8));
        }

        /// <summary>
        /// PERF-03 byte-accurate out-of-order progress: when the SHORT FINAL chunk completes FIRST,
        /// progress must reflect the actual completed byte sum (small jump), not ChunksSent.Count *
        /// ChunkSize (which would over-report), and must never exceed 100%.
        /// </summary>
        [Test]
        [Category("ParallelChunk")]
        public async Task ParallelChunk_OutOfOrderCompletion_ByteAccurateProgress_NoOvershoot()
        {
            // 12 MB = 3 chunks: chunk0=5MB, chunk1=5MB, chunk2=2MB (short final).
            long fileSize = 12L * 1024 * 1024;
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);
            sut.ChunkConcurrencyOverride = 3;

            mockHandler.UseIndexedRouting = true;
            mockHandler.EnqueueInitiate("sess-ooo", totalChunks: 3);
            // Force the SHORT FINAL chunk (index 2, 2 MB) to complete FIRST; the two full chunks later.
            mockHandler.EnqueueChunkOk(0, delayMs: 120);
            mockHandler.EnqueueChunkOk(1, delayMs: 120);
            mockHandler.EnqueueChunkOk(2, delayMs: 5);
            mockHandler.EnqueueComplete();

            var progressValues = new List<double>();
            var progressLock = new object();
            var progress = new Progress<double>(v => { lock (progressLock) progressValues.Add(v); });

            var result = await sut.UploadFileAsync(job, progress, CancellationToken.None);
            await Task.Delay(100); // let Progress callbacks flush

            Assert.That(result.Success, Is.True);

            List<double> snap;
            lock (progressLock) snap = progressValues.ToList();

            Assert.That(snap, Is.Not.Empty);
            // No overshoot ever.
            Assert.That(snap.All(v => v <= 1.0 + 1e-9), Is.True, "progress must never exceed 100%");

            // The first reported value corresponds to the SHORT final chunk finishing first:
            // 2 MB / 12 MB ~= 0.1667 — NOT 5 MB / 12 MB (~0.4167) that count*ChunkSize would give.
            double firstReport = snap.First();
            Assert.That(firstReport, Is.LessThan(0.30),
                $"first progress ({firstReport:P1}) reflects the 2 MB short chunk, not a full 5 MB chunk");
            Assert.That(firstReport, Is.GreaterThan(0.10));

            // Final reported value reaches 100% (all 12 MB accounted for).
            Assert.That(snap.Last(), Is.EqualTo(1.0).Within(1e-6));
        }

        /// <summary>
        /// PERF-03 410 mid-flight sibling drain: when a chunk returns 410 Gone while siblings are
        /// in flight, the siblings are cancelled and drained, and the session restarts cleanly
        /// (the upload ultimately succeeds via a fresh session). No "Collection was modified".
        /// </summary>
        [Test]
        [Category("ParallelChunk")]
        public async Task ParallelChunk_410MidFlight_DrainsSiblingsThenRestarts()
        {
            long fileSize = 20L * 1024 * 1024; // 4 chunks
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);
            job.SessionRestartCount = 0;
            sut.ChunkConcurrencyOverride = 4;

            mockHandler.UseIndexedRouting = true;
            // First session: chunk 1 returns 410; the other chunks are slow (still in flight).
            mockHandler.EnqueueInitiate("sess-410-a", totalChunks: 4);
            mockHandler.EnqueueChunkOk(0, delayMs: 200);
            mockHandler.EnqueueChunkGone(1, delayMs: 5); // expires mid-flight, first
            mockHandler.EnqueueChunkOk(2, delayMs: 200);
            mockHandler.EnqueueChunkOk(3, delayMs: 200);
            // Restart session: fresh initiate + all 4 chunks + complete.
            mockHandler.EnqueueInitiate("sess-410-b", totalChunks: 4);
            for (int i = 0; i < 4; i++) mockHandler.EnqueueChunkOk(i, delayMs: 5);
            mockHandler.EnqueueComplete();

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.True, "a 410 mid-flight should drain siblings then restart and succeed");
            Assert.That(result.IsPermanent, Is.False);
            Assert.That(job.SessionRestartCount, Is.EqualTo(1));
            Assert.That(job.UploadSessionId, Is.EqualTo("sess-410-b"));
            Assert.That(job.ChunksSent.OrderBy(i => i), Is.EqualTo(Enumerable.Range(0, 4)));
        }

        /// <summary>
        /// CR-03 regression: a 410 on one chunk triggers a group-cancel/restart while siblings are in
        /// flight, and the OUTER token is cancelled DURING the drain of that group cancel. The outcome
        /// must be deterministic: outer cancellation wins (clean cancel, surfaced as a failed result by
        /// UploadFileAsync), no exception escapes, the job is left resumable (SessionRestartCount NOT
        /// incremented because the pending restart is intentionally abandoned for this run), and the
        /// LastObservedMaxInFlight observability seam is still assigned.
        /// </summary>
        [Test]
        [Category("ParallelChunk")]
        public async Task ParallelChunk_OuterCancelDuringGroupCancelDrain_DeterministicOuterCancel()
        {
            long fileSize = 20L * 1024 * 1024; // 4 chunks
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);
            job.SessionRestartCount = 0;
            sut.ChunkConcurrencyOverride = 4;

            using var cts = new CancellationTokenSource();

            mockHandler.UseIndexedRouting = true;
            mockHandler.EnqueueInitiate("sess-410-outer", totalChunks: 4);
            // Chunk 1 returns 410 AND cancels the OUTER token at the same instant -> deterministically
            // races the group-cancel/restart against an outer/shutdown cancel. By the time the
            // post-drain branch evaluates, cancellationToken.IsCancellationRequested is already true.
            mockHandler.EnqueueChunkCustom(1, async ct =>
            {
                await Task.Delay(5, ct).ConfigureAwait(false);
                cts.Cancel();
                return new HttpResponseMessage(HttpStatusCode.Gone)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"upload.expired\"}}")
                };
            });
            // Siblings are slow; they get cancelled into the drain.
            mockHandler.EnqueueChunkOk(0, delayMs: 400);
            mockHandler.EnqueueChunkOk(2, delayMs: 400);
            mockHandler.EnqueueChunkOk(3, delayMs: 400);
            // If the restart path were (incorrectly) taken it would need a fresh initiate; we do NOT
            // enqueue one. Outer-cancel must win deterministically instead.

            var result = await sut.UploadFileAsync(job, null, cts.Token);

            // Outer cancellation wins deterministically: surfaced as a (non-permanent) failed result.
            Assert.That(result.Success, Is.False, "outer cancel during the drain must yield a cancelled result");
            Assert.That(result.IsPermanent, Is.False, "a user/shutdown cancel is never a permanent failure");
            // The pending session-restart is abandoned for this run -> count NOT incremented.
            Assert.That(job.SessionRestartCount, Is.EqualTo(0),
                "outer cancel must abandon (not consume) the pending session restart");
            // Observability seam assigned on the outer-cancel exit path.
            Assert.That(sut.LastObservedMaxInFlight, Is.GreaterThanOrEqualTo(1),
                "LastObservedMaxInFlight must be set on the outer-cancel exit path");
        }

        /// <summary>
        /// PERF-03: the ChunkConcurrency getter-delegate is consumed — a client built with a delegate
        /// returning 3 runs parallel; one returning 1 stays sequential.
        /// </summary>
        [Test]
        [Category("ParallelChunk")]
        public async Task ParallelChunk_GetterDelegate_DrivesEffectiveConcurrency()
        {
            long fileSize = 30L * 1024 * 1024; // 6 chunks
            var filePath = CreateTempFile(fileSize);

            // Build a client with a getter-delegate returning 3 (no override set).
            int concurrencySetting = 3;
            using var delegated = new AstrovaultApiClient(
                mockAuthManager.Object, "https://api.test.io", mockHandler, () => concurrencySetting);

            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);
            mockHandler.UseIndexedRouting = true;
            mockHandler.EnqueueInitiate("sess-deleg", totalChunks: 6);
            for (int i = 0; i < 6; i++) mockHandler.EnqueueChunkOk(i, delayMs: 40);
            mockHandler.EnqueueComplete();

            var result = await delegated.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.True);
            Assert.That(delegated.LastObservedMaxInFlight, Is.GreaterThan(1),
                "a getter-delegate returning 3 must drive parallel chunk uploads");
            Assert.That(delegated.LastObservedMaxInFlight, Is.LessThanOrEqualTo(3));
        }

        // ====================================================================
        // Progress Tests
        // ====================================================================

        [Test]
        public async Task Progress_ReportsMonotonicallyIncreasing()
        {
            // Arrange: 15 MB file = 3 chunks
            long fileSize = 15 * 1024 * 1024;
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);

            EnqueueFullChunkedFlow(3, "sess-prog");

            var progressValues = new List<double>();
            var progress = new Progress<double>(v => progressValues.Add(v));

            // Act
            await sut.UploadFileAsync(job, progress, CancellationToken.None);

            // Allow progress callbacks to fire (they may be posted on SynchronizationContext)
            await Task.Delay(100);

            // Assert: progress values are monotonically increasing
            Assert.That(progressValues.Count, Is.GreaterThanOrEqualTo(3));
            for (int i = 1; i < progressValues.Count; i++)
            {
                Assert.That(progressValues[i], Is.GreaterThanOrEqualTo(progressValues[i - 1]),
                    $"Progress decreased at index {i}: {progressValues[i - 1]} -> {progressValues[i]}");
            }
        }

        // ====================================================================
        // Helper Methods
        // ====================================================================

        /// <summary>
        /// Enqueues responses for a full chunked upload flow: initiate + N chunks + complete.
        /// </summary>
        private void EnqueueFullChunkedFlow(int totalChunks, string uploadId)
        {
            // Initiate
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId, chunkSize = 5242880, totalChunks, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });

            // Chunk responses
            for (int i = 0; i < totalChunks; i++)
            {
                mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { chunkIndex = i, received = true }),
                        Encoding.UTF8, "application/json")
                });
            }

            // Complete
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "M31/Light/001.fits", fileSize = 5242880L * totalChunks }),
                    Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Mock HttpMessageHandler that queues responses and captures requests.
    /// Supports sequential response matching for multi-request test flows.
    ///
    /// In addition to plain queued <see cref="HttpResponseMessage"/> values, the handler
    /// supports queuing per-request "steps" that can throw a transport exception
    /// (HttpRequestException / IOException) or stall (honor the per-attempt timeout by
    /// blocking until the caller's CancellationToken fires). These are used by the
    /// Resilience tests to drive REL-05 (per-chunk transport retry) and REL-09
    /// (per-attempt stall timeout).
    /// </summary>
    public class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> stepQueue
            = new Queue<Func<CancellationToken, Task<HttpResponseMessage>>>();

        // PERF-03 ParallelChunk support: when enabled, chunk PUTs (/chunks/{i}) are routed to a
        // per-index queue (so parallel/out-of-order arrivals each get the right response and an
        // optional per-chunk delay), while initiate/complete/status still come from stepQueue in
        // order. This lets tests deterministically drive out-of-order completion and a 410 on a
        // specific chunk index regardless of arrival order. Thread-safe (parallel SendAsync).
        public bool UseIndexedRouting { get; set; }
        private readonly Dictionary<int, Queue<Func<CancellationToken, Task<HttpResponseMessage>>>> chunkSteps
            = new Dictionary<int, Queue<Func<CancellationToken, Task<HttpResponseMessage>>>>();
        private readonly object gate = new object();

        public List<HttpRequestMessage> Requests { get; } = new List<HttpRequestMessage>();
        public List<Task<string>> RequestBodies { get; } = new List<Task<string>>();
        public List<byte[]> RequestBodyBytes { get; } = new List<byte[]>();

        public void Enqueue(HttpResponseMessage response)
        {
            stepQueue.Enqueue(_ => Task.FromResult(response));
        }

        // ---- Indexed-routing helpers (ParallelChunk tests) ----

        public void EnqueueInitiate(string uploadId, int totalChunks)
        {
            Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId, chunkSize = 5242880, totalChunks, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });
        }

        public void EnqueueComplete(string remoteUrl = "https://cdn.test.io/file.fits")
        {
            Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { remoteUrl, remotePath = "M31/Light/001.fits", fileSize = 1L }),
                    Encoding.UTF8, "application/json")
            });
        }

        public void EnqueueChunkOk(int chunkIndex, int delayMs = 0)
        {
            EnqueueChunkStep(chunkIndex, async ct =>
            {
                if (delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { chunkIndex, received = true }),
                        Encoding.UTF8, "application/json")
                };
            });
        }

        public void EnqueueChunkGone(int chunkIndex, int delayMs = 0)
        {
            EnqueueChunkStep(chunkIndex, async ct =>
            {
                if (delayMs > 0) await Task.Delay(delayMs, ct).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.Gone)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"upload.expired\"}}")
                };
            });
        }

        /// <summary>
        /// CR-03 test seam: queues a fully custom per-index chunk step so a test can, e.g., cancel an
        /// outer token at the exact moment a chunk responds (racing a 410 group-cancel against an
        /// outer/shutdown cancel deterministically).
        /// </summary>
        public void EnqueueChunkCustom(int chunkIndex, Func<CancellationToken, Task<HttpResponseMessage>> step)
        {
            EnqueueChunkStep(chunkIndex, step);
        }

        private void EnqueueChunkStep(int chunkIndex, Func<CancellationToken, Task<HttpResponseMessage>> step)
        {
            lock (gate)
            {
                if (!chunkSteps.TryGetValue(chunkIndex, out var q))
                {
                    q = new Queue<Func<CancellationToken, Task<HttpResponseMessage>>>();
                    chunkSteps[chunkIndex] = q;
                }
                q.Enqueue(step);
            }
        }

        private static int? TryParseChunkIndex(Uri uri)
        {
            // .../uploads/{id}/chunks/{index}
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i] == "chunks" && int.TryParse(segments[i + 1], out var idx))
                {
                    return idx;
                }
            }
            return null;
        }

        /// <summary>
        /// Queues a step that throws a transport-level exception (e.g.
        /// <see cref="HttpRequestException"/> or <see cref="IOException"/>) instead of
        /// returning a response — simulating a mid-flight transport fault.
        /// </summary>
        public void EnqueueThrow(Exception ex)
        {
            stepQueue.Enqueue(_ => Task.FromException<HttpResponseMessage>(ex));
        }

        /// <summary>
        /// Queues a step that stalls until the caller's per-attempt CancellationToken
        /// fires (simulating a hung connection). When the token is cancelled the step
        /// throws <see cref="TaskCanceledException"/>, exactly as HttpClient does on a
        /// per-attempt timeout.
        /// </summary>
        public void EnqueueStall()
        {
            stepQueue.Enqueue(async ct =>
            {
                // Block until cancelled; surface as TaskCanceledException like HttpClient.
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                throw new TaskCanceledException(); // unreachable; Delay throws first
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Capture body bytes before the content is disposed.
            byte[]? bodyBytes = null;
            string? bodyString = null;
            if (request.Content != null)
            {
                bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    bodyString = Encoding.UTF8.GetString(bodyBytes);
                }
                catch
                {
                    bodyString = $"[binary: {bodyBytes.Length} bytes]";
                }
            }

            // Record request + body (lock: parallel chunk SendAsync calls mutate these lists).
            Func<CancellationToken, Task<HttpResponseMessage>> step;
            lock (gate)
            {
                Requests.Add(request);
                RequestBodyBytes.Add(bodyBytes ?? Array.Empty<byte>());
                RequestBodies.Add(Task.FromResult(bodyString ?? ""));

                int? chunkIndex = UseIndexedRouting && request.Method == HttpMethod.Put
                    ? TryParseChunkIndex(request.RequestUri!)
                    : null;

                if (chunkIndex.HasValue
                    && chunkSteps.TryGetValue(chunkIndex.Value, out var q)
                    && q.Count > 0)
                {
                    step = q.Dequeue();
                }
                else if (stepQueue.Count > 0)
                {
                    step = stepQueue.Dequeue();
                }
                else
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("{\"error\":\"No more queued responses\"}")
                    };
                }
            }

            return await step(cancellationToken).ConfigureAwait(false);
        }
    }
}
