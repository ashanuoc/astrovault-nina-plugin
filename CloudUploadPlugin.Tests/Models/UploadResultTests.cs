using Astrovault.Models;

namespace CloudUploadPlugin.Tests.Models
{
    [TestFixture]
    [Category("FilePreparation")]
    public class UploadResultTests
    {
        [Test]
        public void FailPermanent_SetsIsPermanentTrue()
        {
            var result = UploadResult.FailPermanent("File not found");

            Assert.That(result.Success, Is.False);
            Assert.That(result.IsPermanent, Is.True);
        }

        [Test]
        public void Fail_DefaultsIsPermanentFalse()
        {
            var result = UploadResult.Fail("Network error");

            Assert.That(result.Success, Is.False);
            Assert.That(result.IsPermanent, Is.False);
        }

        [Test]
        public void Ok_DefaultsIsPermanentFalse()
        {
            var result = UploadResult.Ok("https://cdn.astrovault.io/images/001.fits");

            Assert.That(result.Success, Is.True);
            Assert.That(result.IsPermanent, Is.False);
        }

        [Test]
        public void FailPermanent_SetsErrorMessage()
        {
            var result = UploadResult.FailPermanent("Access denied: D:\\Astro\\file.fits");

            Assert.That(result.ErrorMessage, Is.EqualTo("Access denied: D:\\Astro\\file.fits"));
            Assert.That(result.IsPermanent, Is.True);
        }
    }
}
