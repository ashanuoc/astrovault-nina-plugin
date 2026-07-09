using Astrovault.Models;

namespace CloudUploadPlugin.Tests.Models
{
    [TestFixture]
    [Category("Auth")]
    public class ApiKeyValidationResultTests
    {
        [Test]
        public void Success_CreatesValidResult_WithAccountName()
        {
            var result = ApiKeyValidationResult.Success("John's Observatory");

            Assert.That(result.Valid, Is.True);
            Assert.That(result.AccountName, Is.EqualTo("John's Observatory"));
            Assert.That(result.ErrorMessage, Is.Null);
        }

        [Test]
        public void Fail_CreatesInvalidResult_WithErrorMessage()
        {
            var result = ApiKeyValidationResult.Fail("Invalid API key");

            Assert.That(result.Valid, Is.False);
            Assert.That(result.ErrorMessage, Is.EqualTo("Invalid API key"));
            Assert.That(result.AccountName, Is.Null);
        }
    }
}
