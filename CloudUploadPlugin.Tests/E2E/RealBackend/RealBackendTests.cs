using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Core;
using Astrovault.Data;
using Astrovault.Models;
using CloudUploadPlugin.Tests.E2E.Fixtures;
using CloudUploadPlugin.Tests.E2E.Helpers;
using NUnit.Framework;

namespace CloudUploadPlugin.Tests.E2E.RealBackend
{
    [TestFixture]
    [Category("RealBackend")]
    public class RealBackendTests
    {
        private RealBackendFixture Fixture => RealBackendFixture.Instance;
        private string TempDir;

        [SetUp]
        public void SetUp()
        {
            Assert.That(Fixture, Is.Not.Null,
                "RealBackendFixture.Instance is null -- ASTROVAULT_API_KEY likely not set");
            TempDir = Path.Combine(Path.GetTempPath(), $"astrovault-real-e2e-{Guid.NewGuid():N}");
            Directory.CreateDirectory(TempDir);
        }

        [TearDown]
        public void TearDown()
        {
            // [Addresses review concern: cleanup test artifacts]
            try
            {
                if (Directory.Exists(TempDir))
                    Directory.Delete(TempDir, recursive: true);
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"TearDown cleanup warning: {ex.Message}");
            }
        }

        // ================================================================
        // Test 1: API key validation against real backend
        // ================================================================

        [Test]
        public async Task AuthValidation_ValidKey_ReturnsTrue()
        {
            var authStub = new TestAuthStub { ApiKey = Fixture.ApiKey };
            var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl);
            try
            {
                var result = await client.TestConnectionAsync();

                Assert.That(result, Is.True,
                    "TestConnectionAsync should return true with valid API key against real backend. " +
                    "This means auth/validate returned {\"valid\":true} and the plugin correctly parsed it.");
            }
            finally
            {
                client.Dispose();
            }
        }

        [Test]
        public async Task AuthValidation_InvalidKey_ReturnsFalse()
        {
            var authStub = new TestAuthStub { ApiKey = "invalid-key-does-not-exist-" + Guid.NewGuid().ToString("N") };
            var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl);
            try
            {
                var result = await client.TestConnectionAsync();

                Assert.That(result, Is.False,
                    "TestConnectionAsync should return false with invalid API key against real backend. " +
                    "Real backend returns HTTP 200 with {\"valid\":false} -- plugin must check body, not status.");
            }
            finally
            {
                client.Dispose();
            }
        }

        // ================================================================
        // Test 2: Single file upload (< 5 MB) -- verifies response body
        // ================================================================

        [Test]
        public async Task SingleUpload_SmallFile_Succeeds()
        {
            // Create a 1 KB test file
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, 1024, "test-single-upload.fits");

            var authStub = new TestAuthStub { ApiKey = Fixture.ApiKey };
            var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl);
            try
            {
                var job = CreateTestJob(filePath, 1024, "test-single-upload.fits");

                var progress = new Progress<double>();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                var result = await client.UploadFileAsync(job, progress, cts.Token);

                // [Addresses review concern: verify response content, not just HTTP success]
                Assert.That(result.Success, Is.True,
                    $"Single file upload should succeed against real backend. Error: {result.ErrorMessage}");
                Assert.That(result.ErrorMessage, Is.Null.Or.Empty,
                    $"Single upload succeeded but had error message: {result.ErrorMessage}");

                TestContext.WriteLine($"Single upload completed. Job ID: {job.Id}, RelativePath: {job.RelativePath}");
            }
            finally
            {
                client.Dispose();
            }
        }

        // ================================================================
        // Test 3: Chunked upload (> 5 MB) -- verifies response body
        // ================================================================

        [Test]
        public async Task ChunkedUpload_LargeFile_Succeeds()
        {
            // Create a 6 MB file to trigger chunked upload (threshold is 5 MB)
            long fileSize = 6 * 1024 * 1024; // 6 MB
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, fileSize, "test-chunked-upload.fits");

            var authStub = new TestAuthStub { ApiKey = Fixture.ApiKey };

            // Chunked upload needs queue repository for chunk state persistence
            var queueDir = Path.Combine(TempDir, "queue");
            Directory.CreateDirectory(queueDir);
            var queueRepo = new UploadQueueRepository(queueDir);

            var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl, queueRepo);
            try
            {
                var job = CreateTestJob(filePath, fileSize, "test-chunked-upload.fits");

                var progress = new Progress<double>();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

                var result = await client.UploadFileAsync(job, progress, cts.Token);

                // [Addresses review concern: verify response content, not just HTTP success]
                Assert.That(result.Success, Is.True,
                    $"Chunked file upload should succeed against real backend. Error: {result.ErrorMessage}");
                Assert.That(result.ErrorMessage, Is.Null.Or.Empty,
                    $"Chunked upload succeeded but had error message: {result.ErrorMessage}");

                TestContext.WriteLine($"Chunked upload completed. Job ID: {job.Id}, " +
                    $"RelativePath: {job.RelativePath}, Size: {fileSize} bytes");
            }
            finally
            {
                client.Dispose();
            }
        }

        // ================================================================
        // Test 4: Threshold-adjacent upload (exactly 5 MB) -- boundary test
        // [Addresses MEDIUM review concern: 5 MB boundary where chunking breaks]
        // ================================================================

        [Test]
        public async Task ThresholdUpload_Exactly5MB_Succeeds()
        {
            // Exactly 5 MB = 5 * 1024 * 1024 bytes.
            // AstrovaultApiClient.ChunkedThreshold = 5 * 1024 * 1024.
            // Files >= threshold use chunked path, so exactly 5 MB triggers chunked.
            long fileSize = 5 * 1024 * 1024; // exactly 5 MB
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, fileSize, "test-threshold-upload.fits");

            var authStub = new TestAuthStub { ApiKey = Fixture.ApiKey };

            var queueDir = Path.Combine(TempDir, "queue-threshold");
            Directory.CreateDirectory(queueDir);
            var queueRepo = new UploadQueueRepository(queueDir);

            var client = new AstrovaultApiClient(authStub, Fixture.BaseUrl, queueRepo);
            try
            {
                var job = CreateTestJob(filePath, fileSize, "test-threshold-upload.fits");

                var progress = new Progress<double>();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

                var result = await client.UploadFileAsync(job, progress, cts.Token);

                Assert.That(result.Success, Is.True,
                    $"Threshold-adjacent (exactly 5 MB) upload should succeed. Error: {result.ErrorMessage}");
                Assert.That(result.ErrorMessage, Is.Null.Or.Empty,
                    $"Threshold upload succeeded but had error message: {result.ErrorMessage}");

                TestContext.WriteLine($"Threshold upload completed. Job ID: {job.Id}, " +
                    $"Size: {fileSize} bytes (exactly 5 MB = chunked threshold boundary)");
            }
            finally
            {
                client.Dispose();
            }
        }

        // ================================================================
        // Helpers
        // ================================================================

        private UploadJob CreateTestJob(string filePath, long fileSize, string fileName)
        {
            return new UploadJob
            {
                Id = Guid.NewGuid(),
                LocalPath = filePath,
                RelativePath = $"e2e-test/{Guid.NewGuid():N}/{fileName}",
                FileSize = fileSize,
                CapturedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow,
                Status = UploadStatus.Pending,
                RetryCount = 0,
                Filter = "L",
                Duration = 60.0,
                FileType = "FITS",
                MetadataJson = "{}"
            };
        }
    }
}
