using Astrovault.Core;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Unit tests for the shared "N.N GB" formatter (storage-quota plan §4 Steps 3 and 6). Confirms
    /// BINARY gibibyte (1024^3) math with a "GB" label, always one decimal place -- e.g. exactly 10 GiB
    /// reads "10.0 GB", not "10 GB" or "9.3 GB".
    /// </summary>
    [TestFixture]
    [Category("StorageQuota")]
    public class ByteFormatterTests
    {
        private const long OneGiB = 1024L * 1024 * 1024;

        [Test]
        public void FormatGB_ExactlyTenGiB_Formats_AsTenPointZero()
        {
            Assert.That(ByteFormatter.FormatGB(10 * OneGiB), Is.EqualTo("10.0 GB"));
        }

        [Test]
        public void FormatGB_SixteenGiB_MatchesPlanExample()
        {
            // Plan §4 Step 3's illustrative banner text: "16.0 GB of 10.0 GB used".
            Assert.That(ByteFormatter.FormatGB(16 * OneGiB), Is.EqualTo("16.0 GB"));
        }

        [Test]
        public void FormatGB_TwelvePointFourGiB_MatchesPlanExample()
        {
            // Plan §4 Step 6's illustrative usage-line text: "12.4 GB of 10 GB used".
            long bytes = (long)(12.4 * OneGiB);
            Assert.That(ByteFormatter.FormatGB(bytes), Is.EqualTo("12.4 GB"));
        }

        [Test]
        public void FormatGB_Zero_FormatsAsZeroPointZero()
        {
            Assert.That(ByteFormatter.FormatGB(0), Is.EqualTo("0.0 GB"));
        }

        [Test]
        public void FormatGB_SubGiBValue_RoundsToOneDecimal()
        {
            // 512 MiB = 0.5 GiB
            Assert.That(ByteFormatter.FormatGB(512L * 1024 * 1024), Is.EqualTo("0.5 GB"));
        }

        [Test]
        public void FormatGB_RoundsHalfUpToOneDecimal()
        {
            // 10.05 GiB should round to 10.1 GB (MidpointRounding used by "F1" is AwayFromZero/ToEven
            // depending on the exact fractional bits -- this value is comfortably past the midpoint so
            // it is unambiguous either way).
            long bytes = (long)(10.06 * OneGiB);
            Assert.That(ByteFormatter.FormatGB(bytes), Is.EqualTo("10.1 GB"));
        }

        [Test]
        public void FormatGB2_NearLimitValue_ShowsTheDifference_OneDecimalWouldHide()
        {
            // The motivating case (owner request): 10,691,051,040 bytes = 9.956 GiB. One decimal
            // renders "10.0 GB" -- indistinguishable from the 10 GiB limit; two decimals show 9.96.
            Assert.That(ByteFormatter.FormatGB2(10691051040), Is.EqualTo("9.96 GB"));
            Assert.That(ByteFormatter.FormatGB2(10 * OneGiB), Is.EqualTo("10.00 GB"));
        }

        [Test]
        public void FormatCompact_SubGiB_WholeMB()
        {
            Assert.That(ByteFormatter.FormatCompact(46367200), Is.EqualTo("44 MB"));
            Assert.That(ByteFormatter.FormatCompact(0), Is.EqualTo("0 MB"));
        }

        [Test]
        public void FormatCompact_GiBAndAbove_TwoDecimalGB()
        {
            Assert.That(ByteFormatter.FormatCompact(OneGiB), Is.EqualTo("1.00 GB"));
            Assert.That(ByteFormatter.FormatCompact((long)(2.5 * OneGiB)), Is.EqualTo("2.50 GB"));
        }
    }
}
