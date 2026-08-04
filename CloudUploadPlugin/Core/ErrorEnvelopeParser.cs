using Newtonsoft.Json;

namespace Astrovault.Core
{
    /// <summary>
    /// Parses the nested BACKEND-API-SPEC §4 error envelope -- <c>{ "error": { "code":
    /// "namespace.error_name", "message": "...", "details": {} } }</c> -- extracting ONLY the
    /// machine-readable <c>error.code</c> string.
    /// </summary>
    /// <remarks>
    /// Retry-after-complete addendum: this is the FIRST route in the plugin that parses the nested
    /// §4 envelope -- every other route classifies purely off the HTTP status code (see
    /// <see cref="FailureClassifier"/>). Do NOT wire this parser into any other route's handling;
    /// it exists solely so <c>AstrovaultApiClient.CompleteUploadAsync</c> can distinguish a genuine
    /// "upload already finished" 409 from every other 409 shape.
    /// <para/>
    /// Tolerant like <see cref="QuotaInfoParser"/>: malformed JSON, a non-object JSON value (bare
    /// array/number), a missing or non-object <c>error</c> field, or a missing/non-string
    /// <c>code</c> field all resolve to null instead of throwing -- a parse failure must never
    /// fault the caller's retry/success decision. The server's `message` text is deliberately
    /// never modeled here, matching the codebase's existing stance on server-controlled text (see
    /// LogSanitizer / QuotaInfoParser / ApiClientRedactionTests) -- callers must only ever log the
    /// KNOWN constant they compared the returned code against, never the raw parsed value.
    /// </remarks>
    internal static class ErrorEnvelopeParser
    {
        /// <summary>
        /// Returns the exact <c>error.code</c> string from a §4 envelope body, or null when the
        /// body is empty, not parseable JSON, not an envelope shape (missing/non-object
        /// <c>error</c>), or <c>error.code</c> is missing/not a string. Never throws.
        /// </summary>
        public static string TryParseErrorCode(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            EnvelopeBody body;
            try
            {
                body = JsonConvert.DeserializeObject<EnvelopeBody>(json);
            }
            catch
            {
                // Malformed JSON, a non-object top-level value, or a shape mismatch (e.g. `error`
                // present as a flat string instead of an object) all land here via Newtonsoft's
                // deserialization failure -- treated identically to "no envelope available".
                return null;
            }

            return body?.Error?.Code;
        }

        /// <summary>Wire shape of the §4 error envelope's outer object.</summary>
        private class EnvelopeBody
        {
            [JsonProperty("error")]
            public ErrorObject Error { get; set; }
        }

        /// <summary>
        /// Wire shape of the nested `error` object. Only `code` is modeled -- `message` and
        /// `details` are deliberately omitted so server text can never be captured, logged, or
        /// displayed through this parser.
        /// </summary>
        private class ErrorObject
        {
            [JsonProperty("code")]
            public string Code { get; set; }
        }
    }
}
