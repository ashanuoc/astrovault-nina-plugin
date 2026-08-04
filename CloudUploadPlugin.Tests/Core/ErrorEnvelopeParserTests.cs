using Astrovault.Core;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Unit tests for the nested BACKEND-API-SPEC §4 error-envelope parser (retry-after-complete
    /// addendum). Covers: a valid envelope, a missing/nested-missing `error`, malformed JSON, and a
    /// non-object `error` field (flat-body-shaped). This parser only ever extracts `error.code` --
    /// never `message` -- and is deliberately used from exactly one call site
    /// (AstrovaultApiClient.CompleteUploadAsync's 409 handling).
    /// </summary>
    [TestFixture]
    [Category("StorageQuota")]
    public class ErrorEnvelopeParserTests
    {
        [Test]
        public void TryParseErrorCode_ValidEnvelope_ReturnsCode()
        {
            var json = "{\"error\":{\"code\":\"upload.already_complete\",\"message\":\"Upload session has already been completed\",\"details\":{\"uploadId\":\"sess-1\"}}}";

            var code = ErrorEnvelopeParser.TryParseErrorCode(json);

            Assert.That(code, Is.EqualTo("upload.already_complete"));
        }

        [Test]
        public void TryParseErrorCode_ValidEnvelope_DifferentCode_ReturnsThatCode()
        {
            var json = "{\"error\":{\"code\":\"upload.chunk_hash_mismatch\",\"message\":\"nope\",\"details\":{}}}";

            var code = ErrorEnvelopeParser.TryParseErrorCode(json);

            Assert.That(code, Is.EqualTo("upload.chunk_hash_mismatch"));
        }

        [Test]
        public void TryParseErrorCode_NullOrEmptyOrWhitespace_ReturnsNull()
        {
            Assert.That(ErrorEnvelopeParser.TryParseErrorCode(null), Is.Null);
            Assert.That(ErrorEnvelopeParser.TryParseErrorCode(""), Is.Null);
            Assert.That(ErrorEnvelopeParser.TryParseErrorCode("   "), Is.Null);
        }

        [Test]
        public void TryParseErrorCode_MissingErrorField_ReturnsNull()
        {
            // Valid JSON object, but no `error` key at all (nested-missing).
            var code = ErrorEnvelopeParser.TryParseErrorCode("{\"uploadId\":\"sess-1\"}");

            Assert.That(code, Is.Null);
        }

        [Test]
        public void TryParseErrorCode_ErrorPresentButCodeMissing_ReturnsNull()
        {
            var code = ErrorEnvelopeParser.TryParseErrorCode("{\"error\":{\"message\":\"no code field\"}}");

            Assert.That(code, Is.Null);
        }

        [Test]
        public void TryParseErrorCode_MalformedJson_ReturnsNull()
        {
            Assert.That(ErrorEnvelopeParser.TryParseErrorCode("{not valid json"), Is.Null);
            Assert.That(ErrorEnvelopeParser.TryParseErrorCode("<html>502 Bad Gateway</html>"), Is.Null);
        }

        [Test]
        public void TryParseErrorCode_NonObjectJson_ReturnsNull()
        {
            // A bare JSON array/number is valid JSON but not the expected object shape.
            Assert.That(ErrorEnvelopeParser.TryParseErrorCode("[1,2,3]"), Is.Null);
        }

        [Test]
        public void TryParseErrorCode_FlatBodyShaped_ErrorIsAString_ReturnsNull()
        {
            // A flat (non-envelope) body: `error` present but as a STRING, not a nested object --
            // e.g. a legacy/mismatched error shape. Must never throw, and must not be mistaken for
            // an envelope.
            var code = ErrorEnvelopeParser.TryParseErrorCode("{\"error\":\"upload.already_complete\"}");

            Assert.That(code, Is.Null);
        }

        [Test]
        public void TryParseErrorCode_ErrorIsAnArray_ReturnsNull()
        {
            var code = ErrorEnvelopeParser.TryParseErrorCode("{\"error\":[1,2,3]}");

            Assert.That(code, Is.Null);
        }

        [Test]
        public void TryParseErrorCode_CodeFieldIsNumeric_NeverThrows_AndNeverExactMatchesAKnownConstant()
        {
            // Newtonsoft coerces a numeric `code` to its string form ("12345") rather than throwing --
            // this parser's only real contract is "never throw, never fault the caller's decision".
            // Whatever comes back here can never Ordinal-equal a real machine constant like
            // "upload.already_complete", so the caller's exact-match check is unaffected either way.
            string code = null;
            Assert.DoesNotThrow(() => code = ErrorEnvelopeParser.TryParseErrorCode("{\"error\":{\"code\":12345}}"));
            Assert.That(code, Is.Not.EqualTo("upload.already_complete"));
        }
    }
}
