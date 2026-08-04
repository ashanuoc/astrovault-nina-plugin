using Astrovault.Core;
using Astrovault.Models;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Unit tests for the single status-code classifier (storage-quota plan §4 Step 1), covering
    /// every row of the backend's confirmed per-code table (plan §8):
    ///   507 -> PermanentQuota, 401/403 -> PermanentAuth, 400/413 -> PermanentSkipFile,
    ///   429/503 -> Transient (Retry-After honored elsewhere, not here),
    ///   404/410 -> Transient (route-specific session-expiry semantics are the CALLER's job, not
    ///   this classifier's -- see ResumeUploadTests for the route-level 404/410 behavior),
    ///   everything else (5xx, unrecognized non-2xx, network/timeout represented as 0) -> Transient.
    /// </summary>
    [TestFixture]
    [Category("StorageQuota")]
    public class FailureClassifierTests
    {
        [TestCase(507, FailureKind.PermanentQuota)]
        [TestCase(401, FailureKind.PermanentAuth)]
        [TestCase(403, FailureKind.PermanentAuth)]
        [TestCase(400, FailureKind.PermanentSkipFile)]
        [TestCase(413, FailureKind.PermanentSkipFile)]
        public void Classify_ConfirmedTerminalCodes_MapToExpectedKind(int statusCode, FailureKind expected)
        {
            Assert.That(FailureClassifier.Classify(statusCode), Is.EqualTo(expected));
        }

        [TestCase(429)]
        [TestCase(503)]
        public void Classify_RateLimitAndServiceUnavailable_StayTransient(int statusCode)
        {
            // Retry-After is honored where it already is today (the chunk retry loop) -- this
            // classifier does not add new delay plumbing; 429/503 simply stay Transient.
            Assert.That(FailureClassifier.Classify(statusCode), Is.EqualTo(FailureKind.Transient));
        }

        [TestCase(404)]
        [TestCase(410)]
        public void Classify_SessionRouteCodes_StayTransient_NotGlobalized(int statusCode)
        {
            // 404/410 carry route-specific session-expiry meaning that callers must intercept BEFORE
            // reaching this classifier. The classifier itself must never globalize them into any
            // permanent kind -- it treats them as the harmless Transient default.
            Assert.That(FailureClassifier.Classify(statusCode), Is.EqualTo(FailureKind.Transient));
        }

        [TestCase(500)]
        [TestCase(502)]
        [TestCase(0)] // network fault / timeout: no real HTTP status, represented as 0
        [TestCase(418)]
        [TestCase(422)]
        public void Classify_EverythingElse_StaysTransient(int statusCode)
        {
            Assert.That(FailureClassifier.Classify(statusCode), Is.EqualTo(FailureKind.Transient));
        }

        [Test]
        public void Classify_EachConfirmedCode_UsesItsOwnConstant()
        {
            // Each status code lives in exactly one constant (plan §4 Step 1) -- verify the constants
            // themselves match the confirmed backend table, independent of the switch statement.
            Assert.That(FailureClassifier.StatusInsufficientStorage, Is.EqualTo(507));
            Assert.That(FailureClassifier.StatusUnauthorized, Is.EqualTo(401));
            Assert.That(FailureClassifier.StatusForbidden, Is.EqualTo(403));
            Assert.That(FailureClassifier.StatusBadRequest, Is.EqualTo(400));
            Assert.That(FailureClassifier.StatusPayloadTooLarge, Is.EqualTo(413));
        }
    }
}
