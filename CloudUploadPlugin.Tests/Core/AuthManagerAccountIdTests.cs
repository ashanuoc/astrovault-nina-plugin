using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Astrovault.Core;
using Newtonsoft.Json;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Contract-delta round item 2: <c>GET /nina/auth/validate</c> now returns an optional
    /// <c>accountId</c> field alongside <c>valid</c>/<c>accountName</c>. Covers: a response that
    /// includes it (parsed onto <see cref="Astrovault.Models.ApiKeyValidationResult.AccountId"/>), an
    /// older-backend response that omits it (tolerated, resolves to null -- never throws/fails
    /// validation), and that a failed validation never carries an accountId either. Uses the same
    /// real-<see cref="AuthManager"/>-over-<see cref="MockHttpMessageHandler"/> seam as
    /// <see cref="PluginVersionHeaderTests"/>'s <c>AuthManagerValidateApiKeyAsync_SendsPluginVersionHeader</c>.
    /// </summary>
    [TestFixture]
    [Category("Auth")]
    public class AuthManagerAccountIdTests
    {
        private string tempDir = null!;
        private MockHttpMessageHandler handler = null!;

        [SetUp]
        public void SetUp()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "authmgr_accountid_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            handler = new MockHttpMessageHandler();
        }

        [TearDown]
        public void TearDown()
        {
            handler?.Dispose();
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { /* best-effort cleanup */ }
        }

        [Test]
        public async Task ValidateApiKeyAsync_ResponseIncludesAccountId_ParsesOntoResult()
        {
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { valid = true, accountName = "acct", accountId = "acct-42" }),
                    Encoding.UTF8, "application/json")
            });
            using var mgr = new AuthManager("https://api.test.io", tempDir, handler);

            var result = await mgr.ValidateApiKeyAsync("some-key");

            Assert.That(result.Valid, Is.True);
            Assert.That(result.AccountId, Is.EqualTo("acct-42"));
        }

        [Test]
        public async Task ValidateApiKeyAsync_ResponseOmitsAccountId_TolerantlyResolvesToNull()
        {
            // Older backend, predates the accountId field -- must not fail validation or throw.
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { valid = true, accountName = "acct" }),
                    Encoding.UTF8, "application/json")
            });
            using var mgr = new AuthManager("https://api.test.io", tempDir, handler);

            var result = await mgr.ValidateApiKeyAsync("some-key");

            Assert.That(result.Valid, Is.True, "An absent accountId must never fail an otherwise-valid response");
            Assert.That(result.AccountId, Is.Null);
        }

        [Test]
        public async Task ValidateApiKeyAsync_ResponseAccountIdExplicitNull_ResolvesToNull()
        {
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"valid\":true,\"accountName\":\"acct\",\"accountId\":null}",
                    Encoding.UTF8, "application/json")
            });
            using var mgr = new AuthManager("https://api.test.io", tempDir, handler);

            var result = await mgr.ValidateApiKeyAsync("some-key");

            Assert.That(result.Valid, Is.True);
            Assert.That(result.AccountId, Is.Null);
        }

        [Test]
        public async Task ValidateApiKeyAsync_InvalidKey_NeverCarriesAccountId()
        {
            handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            using var mgr = new AuthManager("https://api.test.io", tempDir, handler);

            var result = await mgr.ValidateApiKeyAsync("bad-key");

            Assert.That(result.Valid, Is.False);
            Assert.That(result.AccountId, Is.Null);
        }
    }
}
