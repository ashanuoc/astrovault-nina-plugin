using Astrovault.Core;

namespace CloudUploadPlugin.Tests.Core
{
    [TestFixture]
    [Category("Auth")]
    public class LogSanitizerTests
    {
        [Test]
        public void MaskApiKey_Null_ReturnsEmpty()
        {
            Assert.That(LogSanitizer.MaskApiKey(null), Is.EqualTo("[empty]"));
        }

        [Test]
        public void MaskApiKey_EmptyString_ReturnsEmpty()
        {
            Assert.That(LogSanitizer.MaskApiKey(""), Is.EqualTo("[empty]"));
        }

        [Test]
        public void MaskApiKey_FourOrFewerChars_ReturnsStars()
        {
            Assert.That(LogSanitizer.MaskApiKey("abc"), Is.EqualTo("****"));
        }

        [Test]
        public void MaskApiKey_FourCharsExactly_ReturnsStars()
        {
            Assert.That(LogSanitizer.MaskApiKey("abcd"), Is.EqualTo("****"));
        }

        [Test]
        public void MaskApiKey_LongerThanFourChars_ReturnsLastFour()
        {
            Assert.That(LogSanitizer.MaskApiKey("abcdef1234x7f2"), Is.EqualTo("...x7f2"));
        }

        [Test]
        public void MaskApiKey_FiveChars_ReturnsLastFour()
        {
            Assert.That(LogSanitizer.MaskApiKey("abcde"), Is.EqualTo("...bcde"));
        }

        [Test]
        public void MaskEmail_ValidEmail_MasksLocalPart()
        {
            Assert.That(LogSanitizer.MaskEmail("test@example.com"), Is.EqualTo("te***@example.com"));
        }

        [Test]
        public void MaskEmail_Null_ReturnsEmpty()
        {
            Assert.That(LogSanitizer.MaskEmail(null), Is.EqualTo("[empty]"));
        }

        [Test]
        public void MaskEmail_EmptyString_ReturnsEmpty()
        {
            Assert.That(LogSanitizer.MaskEmail(""), Is.EqualTo("[empty]"));
        }

        [Test]
        public void MaskEmail_NoAtSign_ReturnsStars()
        {
            Assert.That(LogSanitizer.MaskEmail("noemail"), Is.EqualTo("***"));
        }

        // ===== MaskPath (REDACT-1): filename only, never the directory structure =====

        [Test]
        public void MaskPath_Null_ReturnsEmpty()
        {
            Assert.That(LogSanitizer.MaskPath(null), Is.EqualTo("[empty]"));
        }

        [Test]
        public void MaskPath_EmptyString_ReturnsEmpty()
        {
            Assert.That(LogSanitizer.MaskPath(""), Is.EqualTo("[empty]"));
        }

        [Test]
        public void MaskPath_FullPath_ReturnsFilenameOnly()
        {
            Assert.That(LogSanitizer.MaskPath(@"C:\Users\bob\Astro\Target\img_001.fits"), Is.EqualTo("img_001.fits"));
        }

        [Test]
        public void MaskPath_BareFilename_ReturnsItUnchanged()
        {
            Assert.That(LogSanitizer.MaskPath("img_001.fits"), Is.EqualTo("img_001.fits"));
        }

        // ===== MaskAccountName (REDACT-1): truncates, never the full name =====

        [Test]
        public void MaskAccountName_Null_ReturnsEmpty()
        {
            Assert.That(LogSanitizer.MaskAccountName(null), Is.EqualTo("[empty]"));
        }

        [Test]
        public void MaskAccountName_EmptyString_ReturnsEmpty()
        {
            Assert.That(LogSanitizer.MaskAccountName(""), Is.EqualTo("[empty]"));
        }

        [Test]
        public void MaskAccountName_SingleChar_ReturnsStar()
        {
            Assert.That(LogSanitizer.MaskAccountName("A"), Is.EqualTo("*"));
        }

        [Test]
        public void MaskAccountName_LongName_FirstCharPlusLengthNotFullName()
        {
            var masked = LogSanitizer.MaskAccountName("Observatory Alpha");
            Assert.That(masked, Does.StartWith("O***(").And.Contains("17").And.Not.Contains("Observatory Alpha"));
        }
    }
}
