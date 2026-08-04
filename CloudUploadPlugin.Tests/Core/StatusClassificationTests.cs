using System.IO;
using System.Net;
using System.Net.Http;
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
    /// Storage-quota plan §4 Step 1: threads classified status codes through the single-file,
    /// initiate, and complete drop points (G4), and verifies the Step-1 IsPermanent safety rule --
    /// PermanentAuth/PermanentQuota deliberately evaluate to IsPermanent=false in this step (the
    /// queue-stop machinery arrives in Step 2), while PermanentSkipFile (400/413, and local file
    /// errors) is IsPermanent=true, matching today's behavior.
    /// </summary>
    [TestFixture]
    [Category("StorageQuota")]
    public class StatusClassificationTests
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

        private static HttpResponseMessage ErrorResponse(HttpStatusCode status, string body = "{}")
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        // ====================================================================
        // Single-file upload path (< 5 MB) -- images/upload
        // ====================================================================

        [TestCase(401, FailureKind.PermanentAuth, false)]
        [TestCase(403, FailureKind.PermanentAuth, false)]
        [TestCase(507, FailureKind.PermanentQuota, false)]
        [TestCase(400, FailureKind.PermanentSkipFile, true)]
        [TestCase(413, FailureKind.PermanentSkipFile, true)]
        [TestCase(500, FailureKind.Transient, false)]
        public async Task SingleFileUpload_ClassifiesStatusCode(int statusCode, FailureKind expectedKind, bool expectedIsPermanent)
        {
            var filePath = CreateTempFile(1024);
            var job = TestDataFactory.CreateUploadJob(localPath: filePath);
            job.FileSize = 1024;

            mockHandler.Enqueue(ErrorResponse((HttpStatusCode)statusCode));

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.StatusCode, Is.EqualTo(statusCode));
            Assert.That(result.FailureKind, Is.EqualTo(expectedKind));
            Assert.That(result.IsPermanent, Is.EqualTo(expectedIsPermanent),
                "Step 1 safety rule: PermanentAuth/PermanentQuota must be IsPermanent=false until Step 2's block machine lands");
        }

        [Test]
        public async Task SingleFileUpload_507WithQuotaBody_AttachesValidatedQuotaInfo()
        {
            var filePath = CreateTempFile(1024);
            var job = TestDataFactory.CreateUploadJob(localPath: filePath);
            job.FileSize = 1024;

            mockHandler.Enqueue(ErrorResponse(HttpStatusCode.InsufficientStorage,
                "{\"error\":\"quota_exceeded\",\"usedBytes\":17179869184,\"limitBytes\":10737418240,\"remainingBytes\":0}"));

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.FailureKind, Is.EqualTo(FailureKind.PermanentQuota));
            Assert.That(result.QuotaInfo, Is.Not.Null);
            Assert.That(result.QuotaInfo.UsedBytes, Is.EqualTo(17179869184));
            Assert.That(result.QuotaInfo.LimitBytes, Is.EqualTo(10737418240));
            Assert.That(result.QuotaInfo.RemainingBytes, Is.EqualTo(0));
        }

        [Test]
        public async Task SingleFileUpload_NonQuotaFailure_NeverAttachesQuotaInfo()
        {
            var filePath = CreateTempFile(1024);
            var job = TestDataFactory.CreateUploadJob(localPath: filePath);
            job.FileSize = 1024;

            // A body that WOULD parse as quota numbers, on a non-507 status -- must not be attached.
            mockHandler.Enqueue(ErrorResponse(HttpStatusCode.Unauthorized, "{\"usedBytes\":5,\"limitBytes\":10}"));

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.FailureKind, Is.EqualTo(FailureKind.PermanentAuth));
            Assert.That(result.QuotaInfo, Is.Null);
        }

        [Test]
        public async Task SingleFileUpload_503WithFullQuotaEnvelopeBody_StaysTransient_NeverAttachesQuotaInfo()
        {
            // Regression guard for the backend's warning about single-path error parsers: a 503 that
            // happens to carry the EXACT SAME flat envelope shape as a real 507 body (including a
            // recognized `error` machine constant) must still classify as Transient -- classification
            // is purely status-code-driven and must never be swayed by body shape/content.
            var filePath = CreateTempFile(1024);
            var job = TestDataFactory.CreateUploadJob(localPath: filePath);
            job.FileSize = 1024;

            mockHandler.Enqueue(ErrorResponse(HttpStatusCode.ServiceUnavailable,
                "{\"error\":\"STORAGE_LIMIT_EXCEEDED\",\"usedBytes\":17179869184,\"limitBytes\":10737418240,"
                + "\"remainingBytes\":0,\"message\":\"Storage quota exceeded\"}"));

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.StatusCode, Is.EqualTo(503));
            Assert.That(result.FailureKind, Is.EqualTo(FailureKind.Transient),
                "A 503 must stay Transient regardless of what its body looks like");
            Assert.That(result.IsPermanent, Is.False);
            Assert.That(result.QuotaInfo, Is.Null,
                "QuotaInfo is only ever parsed when FailureKind is PermanentQuota (507) -- a 503 must never attach it");
        }

        // ====================================================================
        // Chunked path: initiate (uploads/initiate) drop point
        // ====================================================================

        [TestCase(401, FailureKind.PermanentAuth, false)]
        [TestCase(403, FailureKind.PermanentAuth, false)]
        [TestCase(507, FailureKind.PermanentQuota, false)]
        [TestCase(400, FailureKind.PermanentSkipFile, true)]
        public async Task Initiate_ClassifiesStatusCode_InsteadOfReturningNull(int statusCode, FailureKind expectedKind, bool expectedIsPermanent)
        {
            long fileSize = 10 * 1024 * 1024; // 2 chunks
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);

            mockHandler.Enqueue(ErrorResponse((HttpStatusCode)statusCode));

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.StatusCode, Is.EqualTo(statusCode),
                "G4 fix: initiate must no longer collapse the failure into a status-less null/generic result");
            Assert.That(result.FailureKind, Is.EqualTo(expectedKind));
            Assert.That(result.IsPermanent, Is.EqualTo(expectedIsPermanent));
            Assert.That(mockHandler.Requests.Count, Is.EqualTo(1), "no chunk/complete requests after a failed initiate");
        }

        // ====================================================================
        // Chunked path: complete (uploads/{id}/complete) drop point
        // ====================================================================

        [TestCase(401, FailureKind.PermanentAuth, false)]
        [TestCase(403, FailureKind.PermanentAuth, false)]
        [TestCase(507, FailureKind.PermanentQuota, false)]
        [TestCase(400, FailureKind.PermanentSkipFile, true)]
        public async Task Complete_ClassifiesNonSuccessStatusCode(int statusCode, FailureKind expectedKind, bool expectedIsPermanent)
        {
            long fileSize = 5 * 1024 * 1024; // 1 chunk
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);

            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { uploadId = "sess-complete-cls", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }), Encoding.UTF8, "application/json")
            });
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { chunkIndex = 0, received = true }), Encoding.UTF8, "application/json")
            });
            mockHandler.Enqueue(ErrorResponse((HttpStatusCode)statusCode));

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.StatusCode, Is.EqualTo(statusCode),
                "G4 fix: complete must no longer lose every non-410 status");
            Assert.That(result.FailureKind, Is.EqualTo(expectedKind));
            Assert.That(result.IsPermanent, Is.EqualTo(expectedIsPermanent));
        }

        [Test]
        public async Task Complete_410StillCarriesSessionExpirySemantics_Unaffected()
        {
            // Regression: the pre-existing 410-at-complete special case (untouched by the classifier
            // work) still returns the same transient, statusCode-410 result it always did -- this path
            // is not routed through FailureClassifier at all.
            long fileSize = 5 * 1024 * 1024;
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);

            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { uploadId = "sess-complete-410", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }), Encoding.UTF8, "application/json")
            });
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(new { chunkIndex = 0, received = true }), Encoding.UTF8, "application/json")
            });
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.Gone)
            {
                Content = new StringContent("{\"error\":{\"code\":\"upload.expired\"}}")
            });

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.False);
            Assert.That(result.StatusCode, Is.EqualTo(410));
            Assert.That(result.FailureKind, Is.EqualTo(FailureKind.Transient));
            Assert.That(result.IsPermanent, Is.False);
            Assert.That(result.ErrorMessage, Is.EqualTo("Session expired during complete"));
        }
    }
}
