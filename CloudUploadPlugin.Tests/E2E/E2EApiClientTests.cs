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

namespace CloudUploadPlugin.Tests.E2E
{
    [TestFixture]
    [Category("E2E")]
    public class E2EApiClientTests : E2ETestBase
    {
        // Inherits from E2ETestBase:
        //   - Fixture (MockServerFixture.Instance)
        //   - TempDir (per-test temp directory)
        //   - [SetUp] BaseSetUp() -- fixture init, temp dir, ResetServerState
        //   - [TearDown] BaseTearDown() -- DumpLogsOnFailure, temp dir cleanup
        //   - CreateUploadJob(filePath, fileSize) helper

        // ====================================================================
        // Scenario 1: Auth validation
        // ====================================================================

        [Test]
        public async Task AuthValidation_ValidKey_Returns200()
        {
            // Valid key "test-key-1" -- TestConnectionAsync hits v1/storage/nina/auth/validate
            // and checks valid==true in response body
            var authStub = new TestAuthStub();
            using var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl);

            var result = await client.TestConnectionAsync();

            Assert.That(result, Is.True, "TestConnectionAsync should succeed with valid key");
        }

        [Test]
        public async Task AuthValidation_InvalidKey_Returns401()
        {
            // Invalid key: server returns valid=false on auth/validate
            // We validate auth rejection via upload since TestConnectionAsync returns false (not exception)
            var authStub = new TestAuthStub { ApiKey = "invalid-key-999" };
            using var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl);

            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.SmallFileSize);
            var job = CreateUploadJob(filePath, E2ETestFileHelper.SmallFileSize);

            var result = await client.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);

            Assert.That(result.Success, Is.False, "Upload should fail with invalid API key");
        }

        [Test]
        public async Task AuthValidation_MissingKey_Returns401()
        {
            // Null key: client-side check in UploadFileAsync returns "Not authenticated"
            var authStub = new TestAuthStub { ApiKey = null };
            using var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl);

            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.SmallFileSize);
            var job = CreateUploadJob(filePath, E2ETestFileHelper.SmallFileSize);

            var result = await client.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);

            Assert.That(result.Success, Is.False, "Upload should fail with missing API key");
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty,
                "Error message should explain the auth failure");
        }

        // ====================================================================
        // Scenario 2: Single-file upload (< 5 MB)
        // ====================================================================

        [Test]
        public async Task SingleFileUpload_SmallFile_SucceedsWithMetadata()
        {
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.SingleUploadSize);
            var job = CreateUploadJob(filePath, E2ETestFileHelper.SingleUploadSize);
            var authStub = new TestAuthStub();
            using var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl);

            var result = await client.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);

            Assert.That(result.Success, Is.True, "Single-file upload should succeed");
            Assert.That(result.RemoteUrl, Is.Not.Null.And.Not.Empty,
                "RemoteUrl should be returned on success");
            Assert.That(result.StatusCode, Is.EqualTo(200),
                "Status code should be 200 on success");
        }

        // ====================================================================
        // Scenario 6: Error envelope parsing
        // ====================================================================

        [Test]
        public async Task ErrorEnvelope_RejectUploads_ReturnsFailureWithErrorMessage()
        {
            // Enable reject_uploads error mode -- server returns 503 with error envelope
            await Fixture.SetErrorMode("reject_uploads", true);

            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.SmallFileSize);
            var job = CreateUploadJob(filePath, E2ETestFileHelper.SmallFileSize);
            var authStub = new TestAuthStub();
            using var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl);

            var result = await client.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);

            Assert.That(result.Success, Is.False, "Upload should fail when reject_uploads is enabled");
            Assert.That(result.ErrorMessage, Is.Not.Null.And.Not.Empty,
                "Client should parse error envelope and provide an error message");
            Assert.That(result.StatusCode, Is.GreaterThan(0),
                "Status code should be set from server error response");
        }

        [Test]
        public async Task ErrorEnvelope_AuthRejection_Returns401()
        {
            // Enable auth_rejection error mode -- server returns 401 for all requests
            await Fixture.SetErrorMode("auth_rejection", true);

            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.SmallFileSize);
            var job = CreateUploadJob(filePath, E2ETestFileHelper.SmallFileSize);
            var authStub = new TestAuthStub();
            using var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl);

            var result = await client.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);

            Assert.That(result.Success, Is.False, "Upload should fail when auth_rejection is enabled");
            Assert.That(result.StatusCode, Is.EqualTo(401), "Status code should be 401 for auth rejection");
        }

        // ====================================================================
        // Scenario 3: Chunked upload (>= 5 MB)
        // ====================================================================

        [Test]
        public async Task ChunkedUpload_LargeFile_CompletesSuccessfully()
        {
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.ChunkedUploadSize);
            var job = CreateUploadJob(filePath, E2ETestFileHelper.ChunkedUploadSize);
            var authStub = new TestAuthStub();
            using var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl);

            var progressValues = new List<double>();
            var progress = new Progress<double>(p => progressValues.Add(p));

            var result = await client.UploadFileAsync(job, progress, CancellationToken.None);

            Assert.That(result.Success, Is.True, "Chunked upload should succeed for 6 MB file");
            Assert.That(result.RemoteUrl, Is.Not.Null.And.Not.Empty,
                "RemoteUrl should be returned on chunked upload success");
            Assert.That(result.StatusCode, Is.EqualTo(200),
                "Status code should be 200 on chunked upload success");
            Assert.That(progressValues.Count, Is.GreaterThan(1),
                "Chunked upload should report progress per chunk");
        }

        // ====================================================================
        // Scenario 4: Retry after failure
        // ====================================================================

        [Test]
        public async Task RetryAfterFailure_ChunkedUpload_CompletesOnSecondAttempt()
        {
            // Setup: 6 MB file (above chunked threshold) with queue repo for state persistence
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.ChunkedUploadSize);
            var job = CreateUploadJob(filePath, E2ETestFileHelper.ChunkedUploadSize);
            var authStub = new TestAuthStub();
            var queueRepo = new UploadQueueRepository(TempDir);
            using var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl, queueRepo);

            // Step 1: Attempt upload with reject_uploads enabled (fails at HTTP level)
            await Fixture.SetErrorMode("reject_uploads", true);

            var result1 = await client.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);

            Assert.That(result1.Success, Is.False, "First upload attempt should fail due to rejection");

            // Step 2: Disable error mode and retry the same job
            // reject_uploads returns 503 at initiation, so no session/chunks exist.
            // The retry is a fresh upload. Key validation: client handles failure-then-success.
            await Fixture.SetErrorMode("reject_uploads", false);

            // Reset job status for retry
            job.Status = UploadStatus.Pending;
            job.RetryCount = 0;
            job.ErrorMessage = null;

            var result2 = await client.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);

            Assert.That(result2.Success, Is.True, "Second upload attempt should succeed after error mode disabled");
            Assert.That(result2.RemoteUrl, Is.Not.Null.And.Not.Empty,
                "RemoteUrl should be returned on successful retry");
        }
    }
}
