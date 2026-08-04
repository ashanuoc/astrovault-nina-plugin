using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Core;
using Astrovault.Interfaces;
using CloudUploadPlugin.Tests.Helpers;
using Moq;
using Newtonsoft.Json;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Storage-quota plan §4 Step 1: every HTTP request AstrovaultApiClient (and AuthManager's
    /// validate call) sends must carry the X-Plugin-Version header, read once from the assembly
    /// version. Covers all seven request sites: connection test, single-file upload, chunked
    /// initiate, chunk PUT, complete, resume-status GET, and AuthManager.ValidateApiKeyAsync.
    /// </summary>
    [TestFixture]
    [Category("StorageQuota")]
    public class PluginVersionHeaderTests
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
            File.WriteAllBytes(tempFilePath, new byte[sizeInBytes]);
            return tempFilePath;
        }

        private static void AssertCarriesPluginVersionHeader(System.Net.Http.HttpRequestMessage request)
        {
            Assert.That(request.Headers.Contains(PluginVersionHeader.HeaderName), Is.True,
                $"Request to {request.RequestUri} must carry the {PluginVersionHeader.HeaderName} header");
            var value = request.Headers.GetValues(PluginVersionHeader.HeaderName).First();
            Assert.That(value, Is.EqualTo(PluginVersionHeader.Value));
        }

        [Test]
        public void HeaderValue_IsNotEmptyOrUnknown()
        {
            // The production assembly always carries an AssemblyVersion, so this should never fall
            // back to "unknown" in a real build.
            Assert.That(PluginVersionHeader.Value, Is.Not.Null.And.Not.Empty);
            Assert.That(PluginVersionHeader.Value, Is.Not.EqualTo("unknown"));
        }

        [Test]
        public async Task TestConnectionAsync_SendsPluginVersionHeader()
        {
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { valid = true }), Encoding.UTF8, "application/json")
            });

            await sut.TestConnectionAsync();

            Assert.That(mockHandler.Requests.Count, Is.EqualTo(1));
            AssertCarriesPluginVersionHeader(mockHandler.Requests[0]);
        }

        [Test]
        public async Task SingleFileUpload_SendsPluginVersionHeader()
        {
            var filePath = CreateTempFile(1024); // < 5 MB -> single upload
            var job = TestDataFactory.CreateUploadJob(localPath: filePath);
            job.FileSize = 1024;

            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { success = true, remoteUrl = "https://cdn.test.io/file.fits" }), Encoding.UTF8, "application/json")
            });

            await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(mockHandler.Requests.Count, Is.EqualTo(1));
            AssertCarriesPluginVersionHeader(mockHandler.Requests[0]);
        }

        [Test]
        public async Task ChunkedUpload_InitiateChunkAndComplete_AllSendPluginVersionHeader()
        {
            long fileSize = 5 * 1024 * 1024; // 1 chunk
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);

            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { uploadId = "sess-hdr", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }), Encoding.UTF8, "application/json")
            });
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { chunkIndex = 0, received = true }), Encoding.UTF8, "application/json")
            });
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "M31/Light/001.fits", fileSize }), Encoding.UTF8, "application/json")
            });

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.True);
            Assert.That(mockHandler.Requests.Count, Is.EqualTo(3), "initiate + 1 chunk + complete");
            foreach (var request in mockHandler.Requests)
            {
                AssertCarriesPluginVersionHeader(request);
            }
        }

        [Test]
        public async Task ResumeStatusCheck_SendsPluginVersionHeader()
        {
            long fileSize = 10 * 1024 * 1024; // 2 chunks
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateResumeUploadJob(
                uploadSessionId: "sess-hdr-resume",
                chunksSent: new List<int> { 0 },
                totalChunks: 2,
                fileSize: fileSize);
            job.LocalPath = filePath;

            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { uploadId = "sess-hdr-resume", status = "in_progress", receivedChunks = new[] { 0 }, totalChunks = 2, expiresAt = "2026-12-31T23:59:59Z" }), Encoding.UTF8, "application/json")
            });
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { chunkIndex = 1, received = true }), Encoding.UTF8, "application/json")
            });
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "test", fileSize }), Encoding.UTF8, "application/json")
            });

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.True);
            // First request is the resume-status GET.
            AssertCarriesPluginVersionHeader(mockHandler.Requests[0]);
        }

        [Test]
        public async Task AuthManagerValidateApiKeyAsync_SendsPluginVersionHeader()
        {
            var authHandler = new MockHttpMessageHandler();
            try
            {
                authHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonConvert.SerializeObject(new { valid = true, accountName = "acct" }), Encoding.UTF8, "application/json")
                });

                var tempDir = Path.Combine(Path.GetTempPath(), "PluginVersionHdr_" + Guid.NewGuid().ToString("N"));
                using var authManager = new AuthManager("https://api.test.io", tempDir, authHandler);

                await authManager.ValidateApiKeyAsync("test-key");

                Assert.That(authHandler.Requests.Count, Is.EqualTo(1));
                AssertCarriesPluginVersionHeader(authHandler.Requests[0]);

                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            }
            finally
            {
                authHandler.Dispose();
            }
        }
    }
}
