using Astrovault.Core;
using Astrovault.Models;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Pure unit tests for the identity-scoping rules (storage-quota plan §4 Step 2, point 4):
    /// canonicalizing an endpoint URL and deciding whether a persisted block belongs to the CURRENT
    /// identity. No I/O (DPAPI/HTTP/disk) is involved -- see <see cref="CloudUploadPlugin.Tests.Core.UploadManagerBlockTests"/>
    /// and <see cref="CloudUploadPlugin.Tests.Data.UploadQueueRepositoryBlockTests"/> for the end-to-end
    /// (repository + manager) reconcile-on-load scenarios.
    /// </summary>
    [TestFixture]
    [Category("StorageQuota")]
    public class UploadBlockIdentityMatcherTests
    {
        // ====================================================================
        // CanonicalizeEndpoint
        // ====================================================================

        [TestCase("https://api.astrovault.io", "https://api.astrovault.io")]
        [TestCase("https://API.Astrovault.IO", "https://api.astrovault.io")]
        [TestCase("https://api.astrovault.io/", "https://api.astrovault.io")]
        [TestCase("https://api.astrovault.io:443", "https://api.astrovault.io")] // default HTTPS port dropped
        [TestCase("http://localhost:80", "http://localhost")] // default HTTP port dropped
        [TestCase("https://api.astrovault.io:8443", "https://api.astrovault.io:8443")] // non-default port kept
        // storage-quota plan directive A#7: the base path is now PRESERVED (only query/fragment/
        // trailing-slash noise is stripped) -- two backends behind the same host but a different base
        // path are DISTINCT identities.
        [TestCase("https://api.astrovault.io/v1/storage/nina", "https://api.astrovault.io/v1/storage/nina")]
        [TestCase("https://api.astrovault.io/v1/storage/nina/", "https://api.astrovault.io/v1/storage/nina")] // trailing slash on a path stripped
        [TestCase("https://gateway.example.com/prod?x=1#frag", "https://gateway.example.com/prod")] // query/fragment stripped
        public void CanonicalizeEndpoint_NormalizesEquivalentForms(string input, string expected)
        {
            Assert.That(UploadBlockIdentityMatcher.CanonicalizeEndpoint(input), Is.EqualTo(expected));
        }

        [Test]
        public void CanonicalizeEndpoint_SameHost_DifferentBasePath_AreDistinct()
        {
            // storage-quota plan directive A#7 (the actual finding): a gateway routing /prod vs /staging
            // to different backends must NOT canonicalize to the same identity.
            var prod = UploadBlockIdentityMatcher.CanonicalizeEndpoint("https://gateway.example.com/prod");
            var staging = UploadBlockIdentityMatcher.CanonicalizeEndpoint("https://gateway.example.com/staging");

            Assert.That(prod, Is.Not.EqualTo(staging));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void CanonicalizeEndpoint_NullOrWhitespace_ReturnsEmpty(string? input)
        {
            Assert.That(UploadBlockIdentityMatcher.CanonicalizeEndpoint(input), Is.EqualTo(string.Empty));
        }

        [Test]
        public void CanonicalizeEndpoint_UnparsableValue_NeverThrows_FallsBackToTrimmedLowercase()
        {
            Assert.That(() => UploadBlockIdentityMatcher.CanonicalizeEndpoint("not a url at all"), Throws.Nothing);
            Assert.That(UploadBlockIdentityMatcher.CanonicalizeEndpoint("NOT-A-URL/"), Is.EqualTo("not-a-url"));
        }

        // ====================================================================
        // MatchesCurrent
        // ====================================================================

        [Test]
        public void MatchesCurrent_NoPersistedBlock_ReturnsTrue()
        {
            Assert.That(UploadBlockIdentityMatcher.MatchesCurrent(null, "https://api.astrovault.io", null, "fp"), Is.True);
        }

        [Test]
        public void MatchesCurrent_CurrentFingerprintMissing_RetainsAsMatch()
        {
            // Storage-quota plan point 4: a MISSING current identity (e.g. auth.dat lost while
            // queue.dat survived) must be RETAINED, never treated as foreign.
            var persisted = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", KeyFingerprint = "abc123" };

            Assert.That(UploadBlockIdentityMatcher.MatchesCurrent(persisted, "https://api.astrovault.io", null, null), Is.True);
            Assert.That(UploadBlockIdentityMatcher.MatchesCurrent(persisted, "https://api.astrovault.io", null, ""), Is.True);
        }

        [Test]
        public void MatchesCurrent_SameFingerprintAndEndpoint_Matches()
        {
            var persisted = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", KeyFingerprint = "abc123" };
            Assert.That(UploadBlockIdentityMatcher.MatchesCurrent(persisted, "https://api.astrovault.io", null, "abc123"), Is.True);
        }

        [Test]
        public void MatchesCurrent_DifferentFingerprint_SameEndpoint_IsForeign()
        {
            var persisted = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", KeyFingerprint = "abc123" };
            Assert.That(UploadBlockIdentityMatcher.MatchesCurrent(persisted, "https://api.astrovault.io", null, "different"), Is.False);
        }

        [Test]
        public void MatchesCurrent_SameFingerprint_DifferentEndpoint_IsForeign()
        {
            // A rotated key that happens to hash the same is astronomically unlikely, but a genuine
            // endpoint change (e.g. staging -> prod) with the SAME stored key must still clear the block.
            var persisted = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", KeyFingerprint = "abc123" };
            Assert.That(UploadBlockIdentityMatcher.MatchesCurrent(persisted, "https://staging.astrovault.io", null, "abc123"), Is.False);
        }

        [Test]
        public void MatchesCurrent_BothSidesHaveMatchingAccountId_MatchesRegardlessOfFingerprint()
        {
            // AccountId takes precedence: key rotation on the SAME account keeps the block.
            var persisted = new UploadBlockIdentity
            {
                EndpointUrl = "https://api.astrovault.io",
                AccountId = "acct-1",
                KeyFingerprint = "old-fingerprint"
            };
            Assert.That(UploadBlockIdentityMatcher.MatchesCurrent(persisted, "https://api.astrovault.io", "acct-1", "brand-new-fingerprint"), Is.True);
        }

        [Test]
        public void MatchesCurrent_BothSidesHaveDifferentAccountId_IsForeign_EvenIfFingerprintMatches()
        {
            var persisted = new UploadBlockIdentity
            {
                EndpointUrl = "https://api.astrovault.io",
                AccountId = "acct-1",
                KeyFingerprint = "same-fingerprint"
            };
            Assert.That(UploadBlockIdentityMatcher.MatchesCurrent(persisted, "https://api.astrovault.io", "acct-2", "same-fingerprint"), Is.False);
        }

        [Test]
        public void MatchesCurrent_SameAccountId_DifferentEndpoint_IsForeign()
        {
            // storage-quota plan directive E: account comparison must include the endpoint -- a
            // matching accountId on a DIFFERENT server is NOT a match (the same account could plausibly
            // exist against a different custom backend).
            var persisted = new UploadBlockIdentity
            {
                EndpointUrl = "https://api.astrovault.io",
                AccountId = "acct-1",
                KeyFingerprint = "fp"
            };
            Assert.That(UploadBlockIdentityMatcher.MatchesCurrent(persisted, "https://staging.astrovault.io", "acct-1", "fp"), Is.False);
        }

        [Test]
        public void MatchesCurrent_OnlyPersistedHasAccountId_CurrentUnknown_RetainsAsIncompleteComparison()
        {
            // storage-quota plan directive E (B#5, the matcher rule change): a persisted block scoped to
            // a known account, compared against an UNKNOWN current account (e.g. the quota endpoint
            // hasn't returned yet this session), is an INCOMPLETE comparison -- retain rather than fall
            // back to a fingerprint/endpoint guess that says nothing about the account. This is TRUE
            // regardless of whether the fingerprint happens to match or not (unlike the pre-directive-E
            // fallback behavior).
            var persisted = new UploadBlockIdentity
            {
                EndpointUrl = "https://api.astrovault.io",
                AccountId = "acct-1",
                KeyFingerprint = "abc123"
            };
            Assert.That(UploadBlockIdentityMatcher.MatchesCurrent(persisted, "https://api.astrovault.io", null, "abc123"), Is.True);
            Assert.That(UploadBlockIdentityMatcher.MatchesCurrent(persisted, "https://api.astrovault.io", null, "totally-different-fingerprint"), Is.True,
                "Incomplete comparison retains even when the fingerprint would have said 'foreign'");
        }

        [Test]
        public void MatchesCurrent_OnlyCurrentHasAccountId_FallsBackToFingerprintAndEndpoint()
        {
            var persisted = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", KeyFingerprint = "abc123" };
            Assert.That(UploadBlockIdentityMatcher.MatchesCurrent(persisted, "https://api.astrovault.io", "acct-2", "abc123"), Is.True);
        }
    }
}
