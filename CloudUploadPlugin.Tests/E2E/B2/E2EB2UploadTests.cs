using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Core;
using Astrovault.Data;
using Astrovault.Models;
using CloudUploadPlugin.Tests.E2E.Fixtures;
using CloudUploadPlugin.Tests.E2E.Helpers;
using NUnit.Framework;

namespace CloudUploadPlugin.Tests.E2E.B2
{
    /// <summary>
    /// E2E tests that validate the upload pipeline against real Backblaze B2 cloud storage.
    /// Mirrors Phase 12 local test patterns but runs through the B2 backend.
    /// All tests skip gracefully (Assert.Ignore) when B2 credentials are unavailable.
    /// </summary>
    [TestFixture]
    [Category("E2E_B2")]
    [Timeout(60000)] // 60s timeout for network-bound B2 tests
    public class E2EB2UploadTests : E2ETestBase
    {
        /// <summary>
        /// Override GetFixture to return the B2 fixture instead of the local fixture.
        /// This is the critical line that routes all B2 tests to B2MockServerFixture.
        /// </summary>
        protected override MockServerFixture GetFixture() => B2MockServerFixture.Instance;

        // ==================================================================
        // Test 1: Auth validation -- invalid key returns 401 through B2
        // ==================================================================

        [Test]
        public async Task AuthValidation_InvalidKey_Returns401()
        {
            var authStub = new TestAuthStub { ApiKey = "invalid-key-999" };
            using var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl);

            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.SmallFileSize);
            var job = CreateUploadJob(filePath, E2ETestFileHelper.SmallFileSize);

            var result = await client.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);

            Assert.That(result.Success, Is.False, "Upload should fail with invalid API key through B2 backend");
            Assert.That(result.StatusCode, Is.EqualTo(401),
                "Invalid API key should return 401 through B2 backend");
        }

        // ==================================================================
        // Test 2: Single-file upload (4MB, below chunked threshold)
        // ==================================================================

        [Test]
        public async Task SingleFileUpload_4MB_SucceedsThroughB2()
        {
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.SingleUploadSize);
            var job = CreateUploadJob(filePath, E2ETestFileHelper.SingleUploadSize);
            var authStub = new TestAuthStub();
            using var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl);

            var result = await client.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);

            Assert.That(result.Success, Is.True, "Single-file upload should succeed through B2");
            Assert.That(result.RemoteUrl, Is.Not.Null.And.Not.Empty,
                "RemoteUrl should be returned on B2 upload success");
            Assert.That(result.StatusCode, Is.EqualTo(200),
                "Status code should be 200 on B2 upload success");
        }

        // ==================================================================
        // Test 3: Chunked upload (6MB, above chunked threshold)
        // ==================================================================

        [Test]
        public async Task ChunkedUpload_6MB_CompletesSuccessfullyThroughB2()
        {
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.ChunkedUploadSize);
            var job = CreateUploadJob(filePath, E2ETestFileHelper.ChunkedUploadSize);
            var authStub = new TestAuthStub();
            using var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl);

            var progressValues = new List<double>();
            var progress = new Progress<double>(p => progressValues.Add(p));

            var result = await client.UploadFileAsync(job, progress, CancellationToken.None);

            Assert.That(result.Success, Is.True, "Chunked upload should succeed through B2");
            Assert.That(result.RemoteUrl, Is.Not.Null.And.Not.Empty,
                "RemoteUrl should be returned on B2 chunked upload success");
            Assert.That(result.StatusCode, Is.EqualTo(200),
                "Status code should be 200 on B2 chunked upload success");
            Assert.That(progressValues.Count, Is.GreaterThan(1),
                "Chunked upload should report progress per chunk");
        }

        // ==================================================================
        // Test 4: Mid-chunk failure (fail at chunk index 1)
        // ==================================================================

        [Test]
        public async Task MidChunkFailure_ChunkRejected_ReturnsFailureWithErrorMessage()
        {
            // 6MB file = 2 chunks (chunk 0: 5MB, chunk 1: 1MB)
            // Set fail_chunk_number=1: chunk 0 succeeds, chunk 1 fails with 503
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.ChunkedUploadSize);
            var job = CreateUploadJob(filePath, E2ETestFileHelper.ChunkedUploadSize);
            var authStub = new TestAuthStub();
            using var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl);

            await Fixture.SetErrorMode("fail_chunk_number", 1);

            var result = await client.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);

            Assert.That(result.Success, Is.False, "Upload should fail when chunk 1 is rejected");
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty,
                "Error message should describe the chunk failure");
            // AstrovaultApiClient returns "Chunk N upload failed: ..." or "Chunk N failed after M attempts"
            Assert.That(result.ErrorMessage, Does.Contain("Chunk").IgnoreCase,
                "Error message should mention chunk failure");
        }

        // ==================================================================
        // Test 5: Retry after failure (reject -> disable -> succeed)
        // ==================================================================

        [Test]
        public async Task RetryAfterFailure_SucceedsOnSecondAttempt()
        {
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.ChunkedUploadSize);
            var job = CreateUploadJob(filePath, E2ETestFileHelper.ChunkedUploadSize);
            var authStub = new TestAuthStub();
            var queueRepo = new UploadQueueRepository(TempDir);
            using var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl, queueRepo);

            // Step 1: Upload fails with reject_uploads enabled (503 at initiation)
            await Fixture.SetErrorMode("reject_uploads", true);

            var result1 = await client.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);
            Assert.That(result1.Success, Is.False, "First upload should fail due to rejection");

            // Step 2: Disable error mode and retry
            await Fixture.SetErrorMode("reject_uploads", false);

            job.Status = UploadStatus.Pending;
            job.RetryCount = 0;
            job.ErrorMessage = null;

            var result2 = await client.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);

            Assert.That(result2.Success, Is.True, "Second upload should succeed through B2 after error cleared");
            Assert.That(result2.RemoteUrl, Is.Not.Null.And.Not.Empty,
                "RemoteUrl should be returned on successful retry through B2");
        }
    }
}
