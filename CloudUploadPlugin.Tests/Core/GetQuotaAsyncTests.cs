using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Core;
using Astrovault.Interfaces;
using Moq;
using Newtonsoft.Json;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Tests for <see cref="AstrovaultApiClient.GetQuotaAsync"/> (storage-quota plan §4 Step 6): the
    /// happy path, the 404-as-unavailable contract (older server), malformed/absurd bodies, the
    /// per-call timeout, the HTTPS send-boundary gate, and the request shape (path, headers, no API
    /// key -> no request at all).
    /// </summary>
    [TestFixture]
    [Category("StorageQuota")]
    public class GetQuotaAsyncTests
    {
        private MockHttpMessageHandler mockHandler;
        private Mock<IAuthManager> mockAuthManager;
        private AstrovaultApiClient sut;

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
        }

        private static HttpResponseMessage OkJson(object body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json")
            };
        }

        [Test]
        public async Task GetQuotaAsync_HappyPath_ReturnsParsedUsage()
        {
            mockHandler.Enqueue(OkJson(new { accountId = "acct-1", planName = "Pro", usedBytes = 13316055040L, limitBytes = 10737418240L, remainingBytes = 0L }));

            var result = await sut.GetQuotaAsync(CancellationToken.None);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AccountId, Is.EqualTo("acct-1"));
            Assert.That(result.PlanName, Is.EqualTo("Pro"));
            Assert.That(result.UsedBytes, Is.EqualTo(13316055040L));
            Assert.That(result.LimitBytes, Is.EqualTo(10737418240L));
            Assert.That(result.RemainingBytes, Is.EqualTo(0L));
        }

        [Test]
        public async Task GetQuotaAsync_HappyPath_SendsCorrectRequestShape()
        {
            mockHandler.Enqueue(OkJson(new { accountId = "acct-1", usedBytes = 1L, limitBytes = 2L }));

            await sut.GetQuotaAsync(CancellationToken.None);

            Assert.That(mockHandler.Requests.Count, Is.EqualTo(1));
            var request = mockHandler.Requests[0];
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(request.RequestUri!.PathAndQuery, Does.Contain("v1/storage/nina/quota"));
            Assert.That(request.Headers.Contains("X-API-Key"), Is.True);
            Assert.That(request.Headers.GetValues("X-API-Key"), Does.Contain("test-api-key"));
            Assert.That(request.Headers.Contains(PluginVersionHeader.HeaderName), Is.True);
        }

        [Test]
        public async Task GetQuotaAsync_404_ReturnsNull_NoWarningLogNeeded()
        {
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));

            var result = await sut.GetQuotaAsync(CancellationToken.None);

            Assert.That(result, Is.Null, "An older server without this endpoint must be treated as unavailable, not an error");
        }

        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.Forbidden)]
        [TestCase(HttpStatusCode.InternalServerError)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public async Task GetQuotaAsync_OtherNon2xx_ReturnsNull(HttpStatusCode statusCode)
        {
            mockHandler.Enqueue(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });

            var result = await sut.GetQuotaAsync(CancellationToken.None);

            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task GetQuotaAsync_MalformedBody_ReturnsNull()
        {
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{not valid json", Encoding.UTF8, "application/json")
            });

            var result = await sut.GetQuotaAsync(CancellationToken.None);

            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task GetQuotaAsync_AbsurdValues_FieldsUnavailable_ButResultNotNull()
        {
            long absurd = (1024L * 1024 * 1024 * 1024 * 1024) + 1; // > 1 PiB
            mockHandler.Enqueue(OkJson(new { accountId = "acct-1", usedBytes = absurd, limitBytes = 10737418240L }));

            var result = await sut.GetQuotaAsync(CancellationToken.None);

            Assert.That(result, Is.Not.Null, "A parseable 200 body always yields a QuotaUsage, even with unavailable fields");
            Assert.That(result.UsedBytes, Is.Null, "An absurd value must be treated as unavailable, not displayed");
            Assert.That(result.LimitBytes, Is.EqualTo(10737418240L));
        }

        [Test]
        public async Task GetQuotaAsync_NoApiKey_ReturnsNull_NoRequestSent()
        {
            mockAuthManager.Setup(a => a.GetApiKey()).Returns((string)null);

            var result = await sut.GetQuotaAsync(CancellationToken.None);

            Assert.That(result, Is.Null);
            Assert.That(mockHandler.Requests, Is.Empty, "No API key -- the request must never be sent");
        }

        [Test]
        public async Task GetQuotaAsync_Timeout_ReturnsNull()
        {
            sut.QuotaFetchTimeout = TimeSpan.FromMilliseconds(100);
            mockHandler.EnqueueStall();

            var result = await sut.GetQuotaAsync(CancellationToken.None);

            Assert.That(result, Is.Null, "A stalled quota fetch must time out and return null, never hang or throw");
        }

        [Test]
        public void GetQuotaAsync_CallerCancellation_DoesNotThrow_ReturnsNull()
        {
            mockHandler.EnqueueStall();
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            QuotaUsageResultOrNull result = null;
            Assert.DoesNotThrowAsync(async () => result = new QuotaUsageResultOrNull(await sut.GetQuotaAsync(cts.Token)),
                "A caller-cancelled quota fetch must be swallowed, never propagate as an unhandled exception");
            Assert.That(result?.Value, Is.Null);
        }

        // Small wrapper so the nullable QuotaUsage assigned inside the Assert.DoesNotThrowAsync lambda
        // is observable from the outer scope without a compiler warning about an unused async result.
        private sealed class QuotaUsageResultOrNull
        {
            public Astrovault.Models.QuotaUsage Value { get; }
            public QuotaUsageResultOrNull(Astrovault.Models.QuotaUsage value) => Value = value;
        }
    }
}
