using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Astrovault.Core;

namespace Astrovault.Tests.Core
{
    /// <summary>
    /// Security tests for AuthManager's at-rest DPAPI hardening and the HTTPS send-boundary gate
    /// (Phase 19-03). Covers:
    ///   DPAPI-1  entropy write→read round-trip across two instances over the same temp folder
    ///   DPAPI-2  legacy null-entropy auth.dat (built with the EXACT current JSON contract) migrates
    ///            on read and is rewritten so it is no longer null-entropy
    ///   DPAPI-3  a garbage/foreign blob yields a null key and a deleted auth.dat (existing
    ///            DeleteAuthFile path) -- no throw, no silent retention (no third behavior)
    ///   P19-01d  a non-loopback http base makes ValidateApiKeyAsync return
    ///            Fail("HTTPS required outside localhost") WITHOUT the injected handler's SendAsync
    ///            ever being invoked (no socket); a https base passes the gate (handler reached)
    ///
    /// The send-boundary proof is BEHAVIORAL (throw-if-invoked HttpMessageHandler via the internal
    /// test ctor), not a network call or a source-text assertion (REVIEWS MED items 4, 5). The
    /// legacy fixture is built with JsonConvert.SerializeObject of an { ApiKey, AccountName } object
    /// (Newtonsoft default PascalCase) to mirror AuthManager.StoreApiKey exactly (REVIEWS MED item 6);
    /// it does NOT depend on the private AppEntropy constant.
    /// </summary>
    [TestFixture]
    [Category("Auth")]
    [Category("Security")]
    public class AuthManagerSecurityTests
    {
        private string tempDir = null!;

        // Loopback https base so the production ctor's send-boundary gate would PASS in the at-rest
        // tests (those tests never call ValidateApiKeyAsync; the base only needs to be a well-formed URL).
        private const string LoopbackHttpsBase = "https://localhost/";

        [SetUp]
        public void SetUp()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "authmgr_security_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { /* best-effort cleanup */ }
        }

        private string AuthFilePath => Path.Combine(tempDir, "auth.dat");

        // ===== DPAPI-1: entropy write→read round-trip =========================================

        [Test]
        public void Entropy_WriteThenRead_RoundTripsKeyAcrossInstances()
        {
            // Arrange + Act: first instance writes with entropy.
            var writer = new AuthManager(LoopbackHttpsBase, tempDir);
            writer.StoreApiKey("test-key-123", "acct");

            // A SECOND instance over the SAME temp folder must read it back (write-with-entropy ->
            // read-with-entropy round-trip; proves the entropy passed to Protect matches Unprotect).
            var reader = new AuthManager(LoopbackHttpsBase, tempDir);

            // Assert
            Assert.That(reader.GetApiKey(), Is.EqualTo("test-key-123"),
                "Entropy-protected auth.dat should round-trip the key across a fresh AuthManager instance");
            Assert.That(reader.AccountName, Is.EqualTo("acct"));
            Assert.That(reader.IsConfigured, Is.True);
        }

        // ===== DPAPI-2: legacy null-entropy migration (exact-serializer fixture) ===============

        [Test]
        public void Entropy_LegacyNullEntropyFile_MigratesOnReadAndIsRewrittenWithEntropy()
        {
            // Arrange: build a legacy null-entropy auth.dat with the EXACT current JSON contract.
            // AuthManager.StoreApiKey does JsonConvert.SerializeObject(new StoredApiKey { ApiKey, AccountName })
            // with default Newtonsoft PascalCase -> {"ApiKey":"...","AccountName":"..."}. Mirror it here.
            var json = JsonConvert.SerializeObject(new { ApiKey = "legacy-key-789", AccountName = "legacy-acct" });
            var legacyBlob = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(AuthFilePath, legacyBlob);

            // Act: constructing an AuthManager reads (migration-on-read) and rewrites the file with entropy.
            var migrating = new AuthManager(LoopbackHttpsBase, tempDir);

            // Assert (1): migration-on-read succeeded.
            Assert.That(migrating.GetApiKey(), Is.EqualTo("legacy-key-789"),
                "A legacy null-entropy auth.dat should still load via the null-entropy fallback");

            // Assert (2): the on-disk file was rewritten and is NO LONGER null-entropy. Proven WITHOUT
            // the private AppEntropy constant: a null-entropy Unprotect of the rewritten bytes must now
            // throw (it is entropy-protected), while a fresh AuthManager still recovers the key.
            var rewrittenBytes = File.ReadAllBytes(AuthFilePath);
            Assert.That(
                () => ProtectedData.Unprotect(rewrittenBytes, null, DataProtectionScope.CurrentUser),
                Throws.InstanceOf<CryptographicException>(),
                "After migration the rewritten auth.dat must no longer be decryptable with null entropy");

            var afterMigration = new AuthManager(LoopbackHttpsBase, tempDir);
            Assert.That(afterMigration.GetApiKey(), Is.EqualTo("legacy-key-789"),
                "The rewritten entropy-protected auth.dat must still round-trip the original key");
        }

        // ===== DPAPI-3: scope-loss / garbage fallthrough to existing DeleteAuthFile path =======

        [Test]
        public void Entropy_GarbageBlob_YieldsNullKeyAndDeletesAuthFile()
        {
            // Arrange: write a garbage/foreign blob that is NOT valid DPAPI ciphertext for either the
            // app entropy OR null entropy. Both Unprotect calls inside DecryptData will throw
            // CryptographicException, which must propagate to LoadStoredKey's existing
            // catch (CryptographicException) -> DeleteAuthFile path (no third behavior).
            File.WriteAllBytes(AuthFilePath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            // Act + Assert: constructing must NOT throw.
            AuthManager mgr = null!;
            Assert.That(() => mgr = new AuthManager(LoopbackHttpsBase, tempDir), Throws.Nothing,
                "A garbage auth.dat must not throw out of the ctor (it lands in the existing failure path)");

            // The key is null (not configured) and the file was deleted -- no silent retention.
            Assert.That(mgr.GetApiKey(), Is.Null, "A garbage blob must yield a null key");
            Assert.That(mgr.IsConfigured, Is.False);
            Assert.That(File.Exists(AuthFilePath), Is.False,
                "A garbage/undecryptable auth.dat must be deleted via the existing DeleteAuthFile path");
        }

        // ===== P19-01d: no-network send-boundary gate ==========================================

        [Test]
        public async Task SendBoundary_NonLoopbackHttp_RefusesWithoutInvokingHandler()
        {
            // Arrange: a throw-if-invoked handler proves the gate returns BEFORE any request is created.
            var handler = new ThrowingHandler();
            using var mgr = new AuthManager("http://example.com/", tempDir, handler);

            // Act
            var result = await mgr.ValidateApiKeyAsync("any-key");

            // Assert: refused with the exact message, and the handler's SendAsync was NEVER reached
            // (no exception escaped from the handler -> no socket opened).
            Assert.That(result.Valid, Is.False, "Non-loopback http must be refused");
            Assert.That(result.ErrorMessage, Is.EqualTo("HTTPS required outside localhost"),
                "The send-boundary refusal must use the exact user-facing message");
            Assert.That(handler.SendInvoked, Is.False,
                "The gate must return before any request is created -- the handler's SendAsync must never run");
        }

        [Test]
        public async Task SendBoundary_HttpsBase_PassesGateAndReachesHandler()
        {
            // Arrange: a https base must PASS the gate. The throw-if-invoked handler then fires when the
            // request is sent, proving the gate was passed (the gate would have returned first otherwise).
            var handler = new ThrowingHandler();
            using var mgr = new AuthManager("https://example.com/", tempDir, handler);

            // Act
            var result = await mgr.ValidateApiKeyAsync("any-key");

            // Assert: the handler WAS reached (gate passed), and the result is NOT the HTTPS refusal
            // (it is the generic "Unexpected error" produced when the handler's exception is caught).
            Assert.That(handler.SendInvoked, Is.True,
                "A https base must pass the gate so the request reaches the handler");
            Assert.That(result.Valid, Is.False);
            Assert.That(result.ErrorMessage, Is.Not.EqualTo("HTTPS required outside localhost"),
                "Passing the gate must NOT return the HTTPS-required message");
        }

        /// <summary>
        /// Throw-if-invoked HttpMessageHandler: records whether SendAsync was reached and throws if it is.
        /// Used to prove the send-boundary gate returns before any request is created (SendInvoked stays
        /// false) for non-loopback http, and that a https base passes the gate (SendInvoked becomes true).
        /// </summary>
        private sealed class ThrowingHandler : HttpMessageHandler
        {
            public bool SendInvoked { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                SendInvoked = true;
                throw new InvalidOperationException("network must not be reached");
            }
        }
    }
}
