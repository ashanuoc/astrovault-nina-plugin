using Astrovault.Core;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Unit tests for the <c>GET v1/storage/nina/quota</c> response parser (storage-quota plan §4 Step
    /// 6). Mirrors <see cref="QuotaInfoParserTests"/>'s coverage for the three shared numeric fields
    /// (validated identically via <see cref="QuotaByteValidator"/>) plus the two additional fields this
    /// endpoint carries: <c>accountId</c> and <c>planName</c>.
    /// </summary>
    [TestFixture]
    [Category("StorageQuota")]
    public class QuotaUsageParserTests
    {
        [Test]
        public void TryParse_ValidBody_ParsesAllFiveFields()
        {
            var json = "{\"accountId\":\"acct-42\",\"planName\":\"Pro\",\"usedBytes\":13316055040,"
                + "\"limitBytes\":10737418240,\"remainingBytes\":0}";

            var result = QuotaUsageParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AccountId, Is.EqualTo("acct-42"));
            Assert.That(result.PlanName, Is.EqualTo("Pro"));
            Assert.That(result.UsedBytes, Is.EqualTo(13316055040));
            Assert.That(result.LimitBytes, Is.EqualTo(10737418240));
            Assert.That(result.RemainingBytes, Is.EqualTo(0));
        }

        [Test]
        public void TryParse_NullOrEmptyOrWhitespace_ReturnsNull()
        {
            Assert.That(QuotaUsageParser.TryParse(null), Is.Null);
            Assert.That(QuotaUsageParser.TryParse(""), Is.Null);
            Assert.That(QuotaUsageParser.TryParse("   "), Is.Null);
        }

        [Test]
        public void TryParse_MalformedJson_ReturnsNull()
        {
            Assert.That(QuotaUsageParser.TryParse("{not valid json"), Is.Null);
            Assert.That(QuotaUsageParser.TryParse("<html>502 Bad Gateway</html>"), Is.Null);
        }

        [Test]
        public void TryParse_NonObjectJson_ReturnsNull()
        {
            Assert.That(QuotaUsageParser.TryParse("[1,2,3]"), Is.Null);
        }

        [Test]
        public void TryParse_MissingAccountIdAndPlanName_ParseNumbersWithNullMetadata()
        {
            var result = QuotaUsageParser.TryParse("{\"usedBytes\":5,\"limitBytes\":10}");

            Assert.That(result, Is.Not.Null);
            Assert.That(result.AccountId, Is.Null);
            Assert.That(result.PlanName, Is.Null);
            Assert.That(result.UsedBytes, Is.EqualTo(5));
            Assert.That(result.LimitBytes, Is.EqualTo(10));
        }

        [Test]
        public void TryParse_BlankAccountIdAndPlanName_NormalizedToNull()
        {
            var json = "{\"accountId\":\"   \",\"planName\":\"\",\"usedBytes\":5,\"limitBytes\":10}";

            var result = QuotaUsageParser.TryParse(json);

            Assert.That(result.AccountId, Is.Null, "Whitespace-only accountId must normalize to null");
            Assert.That(result.PlanName, Is.Null, "Empty planName must normalize to null");
        }

        [Test]
        public void TryParse_NegativeValues_TreatedAsUnavailable()
        {
            var json = "{\"usedBytes\":-1,\"limitBytes\":10737418240,\"remainingBytes\":-5}";

            var result = QuotaUsageParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.UsedBytes, Is.Null, "a negative usedBytes must be treated as unavailable");
            Assert.That(result.LimitBytes, Is.EqualTo(10737418240));
            Assert.That(result.RemainingBytes, Is.Null);
        }

        [Test]
        public void TryParse_AbsurdlyLargeValues_TreatedAsUnavailable()
        {
            long absurd = (1024L * 1024 * 1024 * 1024 * 1024) + 1; // > 1 PiB
            var json = $"{{\"usedBytes\":{absurd},\"limitBytes\":10737418240}}";

            var result = QuotaUsageParser.TryParse(json);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.UsedBytes, Is.Null, "a value > 1 PiB must be treated as unavailable");
            Assert.That(result.LimitBytes, Is.EqualTo(10737418240));
        }

        [Test]
        public void TryParse_ZeroValues_AreValid()
        {
            var json = "{\"usedBytes\":0,\"limitBytes\":0,\"remainingBytes\":0}";

            var result = QuotaUsageParser.TryParse(json);

            Assert.That(result.UsedBytes, Is.EqualTo(0));
            Assert.That(result.LimitBytes, Is.EqualTo(0));
            Assert.That(result.RemainingBytes, Is.EqualTo(0));
        }

        [Test]
        public void TryParse_OverLimitUsage_StillParses()
        {
            // Backend §8: always 200, including over-limit; remainingBytes floored at 0.
            var json = "{\"usedBytes\":17179869184,\"limitBytes\":10737418240,\"remainingBytes\":0}";

            var result = QuotaUsageParser.TryParse(json);

            Assert.That(result.UsedBytes, Is.EqualTo(17179869184));
            Assert.That(result.LimitBytes, Is.EqualTo(10737418240));
            Assert.That(result.RemainingBytes, Is.EqualTo(0));
        }
    }
}
