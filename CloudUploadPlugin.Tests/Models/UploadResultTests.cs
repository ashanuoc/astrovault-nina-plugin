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

        // ====================================================================
        // Storage-quota plan §4 Step 1: FailureKind default/derivation, and the Step-1 safety rule
        // that IsPermanent is derived and evaluates true ONLY for PermanentSkipFile.
        // ====================================================================

        [Test]
        public void Fail_DefaultsFailureKindTransient()
        {
            var result = UploadResult.Fail("Network error");

            Assert.That(result.FailureKind, Is.EqualTo(FailureKind.Transient));
            Assert.That(result.IsPermanent, Is.False);
        }

        [Test]
        public void FailPermanent_SetsFailureKindPermanentSkipFile()
        {
            var result = UploadResult.FailPermanent("File not found");

            Assert.That(result.FailureKind, Is.EqualTo(FailureKind.PermanentSkipFile));
            Assert.That(result.IsPermanent, Is.True);
        }

        [TestCase(FailureKind.PermanentAuth)]
        [TestCase(FailureKind.PermanentQuota)]
        public void Fail_WithAuthOrQuotaKind_IsPermanentIsFalse_Step1SafetyRule(FailureKind kind)
        {
            // CRITICAL Step 1 behavior-safety rule: the queue-stop machinery for 507/401/403 arrives
            // in Step 2. Until then, PermanentAuth/PermanentQuota must NOT terminally fail jobs --
            // IsPermanent stays false so today's transient (retry + eventual exhaustion) behavior is
            // unchanged for these two kinds.
            var result = UploadResult.Fail("classified failure", statusCode: 999, failureKind: kind);

            Assert.That(result.FailureKind, Is.EqualTo(kind));
            Assert.That(result.IsPermanent, Is.False,
                $"{kind} must evaluate IsPermanent=false in Step 1 (queue-stop machinery arrives in Step 2)");
        }

        [Test]
        public void Fail_WithPermanentSkipFileKind_IsPermanentIsTrue()
        {
            // 400/413 skip-the-file-and-continue is the confirmed NEW behavior and is already fully
            // permanent in this step (no Step 2 gate needed).
            var result = UploadResult.Fail("bad request", statusCode: 400, failureKind: FailureKind.PermanentSkipFile);

            Assert.That(result.IsPermanent, Is.True);
        }

        [Test]
        public void Fail_CarriesStatusCodeAndQuotaInfo()
        {
            var quota = new QuotaInfo { UsedBytes = 5, LimitBytes = 10, RemainingBytes = 5 };
            var result = UploadResult.Fail("quota exceeded", 507, FailureKind.PermanentQuota, quota);

            Assert.That(result.StatusCode, Is.EqualTo(507));
            Assert.That(result.QuotaInfo, Is.SameAs(quota));
        }

        [Test]
        public void Ok_DefaultsFailureKindTransient_ButIsPermanentIsFalse()
        {
            // FailureKind is meaningless on a success result; IsPermanent must still read false.
            var result = UploadResult.Ok("https://cdn.astrovault.io/images/001.fits");

            Assert.That(result.Success, Is.True);
            Assert.That(result.IsPermanent, Is.False);
        }
    }
}
