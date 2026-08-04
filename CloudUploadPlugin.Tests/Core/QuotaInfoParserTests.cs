using Astrovault.Core;
using Astrovault.Models;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Unit tests for the 507 quota-body parser (storage-quota plan §4 Step 1). Covers: a valid
    /// body, missing fields, malformed JSON, negative values, and absurd (&gt; 1 PiB) values.
    /// Classification never depends on this parser -- these tests only cover the DISPLAY numbers.
    /// </summary>
    [TestFixture]
    [Category("StorageQuota")]
    public class QuotaInfoParserTests
    {
        [Test]
        public void TryParse_ValidBody_ParsesAllThreeFields()
        {
            var json = "{\"error\":\"quota_exceeded\",\"usedBytes\":17179869184,\"limitBytes\":10737418240,"
                + "\"remainingBytes\":0,\"message\":\"Storage quota exceeded\"}";

            var result = QuotaInfoParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.UsedBytes, Is.EqualTo(17179869184));
            Assert.That(result.LimitBytes, Is.EqualTo(10737418240));
            Assert.That(result.RemainingBytes, Is.EqualTo(0));
        }

        [Test]
        public void TryParse_NullOrEmptyOrWhitespace_ReturnsNull()
        {
            Assert.That(QuotaInfoParser.TryParse(null), Is.Null);
            Assert.That(QuotaInfoParser.TryParse(""), Is.Null);
            Assert.That(QuotaInfoParser.TryParse("   "), Is.Null);
        }

        [Test]
        public void TryParse_MalformedJson_ReturnsNull()
        {
            Assert.That(QuotaInfoParser.TryParse("{not valid json"), Is.Null);
            Assert.That(QuotaInfoParser.TryParse("<html>502 Bad Gateway</html>"), Is.Null);
        }

        [Test]
        public void TryParse_NonObjectJson_ReturnsNull()
        {
            // A bare JSON array/number is valid JSON but not the expected object shape.
            Assert.That(QuotaInfoParser.TryParse("[1,2,3]"), Is.Null);
        }

        [Test]
        public void TryParse_MissingFields_ReturnsQuotaInfoWithNullFields()
        {
            // Valid JSON, but none of the three numeric fields are present -- the body still parses
            // (it's not malformed), each individual field is simply unavailable.
            var result = QuotaInfoParser.TryParse("{\"error\":\"quota_exceeded\"}");

            Assert.That(result, Is.Not.Null);
            Assert.That(result.UsedBytes, Is.Null);
            Assert.That(result.LimitBytes, Is.Null);
            Assert.That(result.RemainingBytes, Is.Null);
        }

        [Test]
        public void TryParse_NegativeValues_TreatedAsUnavailable()
        {
            var json = "{\"usedBytes\":-1,\"limitBytes\":10737418240,\"remainingBytes\":-5}";

            var result = QuotaInfoParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.UsedBytes, Is.Null, "a negative usedBytes must be treated as unavailable");
            Assert.That(result.LimitBytes, Is.EqualTo(10737418240), "a valid sibling field must still parse");
            Assert.That(result.RemainingBytes, Is.Null, "a negative remainingBytes must be treated as unavailable");
        }

        [Test]
        public void TryParse_AbsurdlyLargeValues_TreatedAsUnavailable()
        {
            // > 1 PiB (1024^5) is treated as unavailable rather than displayed.
            long absurd = (1024L * 1024 * 1024 * 1024 * 1024) + 1;
            var json = $"{{\"usedBytes\":{absurd},\"limitBytes\":10737418240}}";

            var result = QuotaInfoParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.UsedBytes, Is.Null, "a value > 1 PiB must be treated as unavailable");
            Assert.That(result.LimitBytes, Is.EqualTo(10737418240));
        }

        [Test]
        public void TryParse_ExactlyOnePiB_IsValid()
        {
            long onePiB = 1024L * 1024 * 1024 * 1024 * 1024;
            var json = $"{{\"usedBytes\":{onePiB}}}";

            var result = QuotaInfoParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.UsedBytes, Is.EqualTo(onePiB), "exactly 1 PiB is the inclusive boundary, not absurd");
        }

        [Test]
        public void TryParse_ZeroValues_AreValid()
        {
            var json = "{\"usedBytes\":0,\"limitBytes\":0,\"remainingBytes\":0}";

            var result = QuotaInfoParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.UsedBytes, Is.EqualTo(0));
            Assert.That(result.LimitBytes, Is.EqualTo(0));
            Assert.That(result.RemainingBytes, Is.EqualTo(0));
        }

        [Test]
        public void TryParse_ExplicitNullFields_AreUnavailable()
        {
            var json = "{\"usedBytes\":null,\"limitBytes\":10737418240,\"remainingBytes\":null}";

            var result = QuotaInfoParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.UsedBytes, Is.Null);
            Assert.That(result.LimitBytes, Is.EqualTo(10737418240));
            Assert.That(result.RemainingBytes, Is.Null);
        }

        [Test]
        public void TryParse_ServerErrorAndMessageStrings_AreNeverExtracted()
        {
            // SC6 stance: `message` is never modeled at all, and `error` is only ever consumed as an
            // exact-match machine constant (see the Flavor tests below) -- never as free text. This
            // test proves the parse succeeds even with a distinctive error/message payload present.
            var json = "{\"error\":\"quota_exceeded\",\"message\":\"SECRET-INTERNAL-DETAIL\","
                + "\"usedBytes\":5,\"limitBytes\":10}";

            var result = QuotaInfoParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.UsedBytes, Is.EqualTo(5));
            Assert.That(result.LimitBytes, Is.EqualTo(10));
        }

        // ====================================================================
        // Contract-delta round: Flavor parsed from the flat body's `error` field as a MACHINE
        // CONSTANT (exact match only). Backend's two confirmed codes: STORAGE_LIMIT_EXCEEDED and
        // PLAN_REQUIRED. Classification (FailureKind) never depends on this -- these tests only cover
        // the Flavor field QuotaInfo now also carries.
        // ====================================================================

        [Test]
        public void TryParse_ErrorStorageLimitExceeded_FlavorIsStorageLimitExceeded()
        {
            var json = "{\"error\":\"STORAGE_LIMIT_EXCEEDED\",\"usedBytes\":5,\"limitBytes\":10,\"remainingBytes\":0}";

            var result = QuotaInfoParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Flavor, Is.EqualTo(QuotaBlockFlavor.StorageLimitExceeded));
        }

        [Test]
        public void TryParse_ErrorPlanRequired_FlavorIsPlanRequired()
        {
            var json = "{\"error\":\"PLAN_REQUIRED\",\"usedBytes\":0,\"limitBytes\":0,\"remainingBytes\":0}";

            var result = QuotaInfoParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Flavor, Is.EqualTo(QuotaBlockFlavor.PlanRequired));
        }

        [Test]
        public void TryParse_UnknownErrorCode_FlavorFallsBackToStorageLimitExceeded()
        {
            // Backend directive: anything other than an EXACT match for PLAN_REQUIRED must fall back
            // to the safe default -- including a plausible-looking but unrecognized future code.
            var json = "{\"error\":\"SOME_FUTURE_CODE_NOT_YET_KNOWN\",\"usedBytes\":5,\"limitBytes\":10}";

            var result = QuotaInfoParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Flavor, Is.EqualTo(QuotaBlockFlavor.StorageLimitExceeded));
        }

        [Test]
        public void TryParse_MissingErrorField_FlavorFallsBackToStorageLimitExceeded()
        {
            var json = "{\"usedBytes\":5,\"limitBytes\":10}";

            var result = QuotaInfoParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Flavor, Is.EqualTo(QuotaBlockFlavor.StorageLimitExceeded));
        }

        [Test]
        public void TryParse_ErrorFieldWrongCase_TreatedAsUnknown_FlavorFallsBackToStorageLimitExceeded()
        {
            // Exact-match only (StringComparison.Ordinal) -- a case variant is NOT the same machine
            // constant and must not be treated as PLAN_REQUIRED.
            var json = "{\"error\":\"plan_required\",\"usedBytes\":0,\"limitBytes\":0}";

            var result = QuotaInfoParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Flavor, Is.EqualTo(QuotaBlockFlavor.StorageLimitExceeded));
        }

        [Test]
        public void TryParse_MalformedJson_ReturnsNull_NoFlavorToInspect()
        {
            // A fully malformed body returns null altogether (existing contract) -- callers default the
            // Flavor to StorageLimitExceeded themselves via `result.QuotaInfo?.Flavor ?? ...` (see
            // UploadManagerBlockTests / UploadQueueRepositoryBlockTests for the latch-still-correct proof).
            Assert.That(QuotaInfoParser.TryParse("{not valid json"), Is.Null);
        }
    }
}
