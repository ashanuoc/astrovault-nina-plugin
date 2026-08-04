using System;
using Astrovault.Models;
using Newtonsoft.Json;

namespace Astrovault.Core
{
    /// <summary>
    /// Parses the 507 (insufficient storage) JSON error body -- <c>{error, usedBytes, limitBytes,
    /// remainingBytes, message}</c> -- into an optional, VALIDATED <see cref="QuotaInfo"/>.
    /// Storage-quota plan §4 Step 1: classification never depends on this body -- a parse failure or
    /// absurd value only affects the DISPLAY numbers, never the <see cref="FailureKind"/>. The
    /// server's `message` string (and `error` beyond the two known machine constants below) is
    /// intentionally never modeled by <see cref="QuotaBody"/>, so it can never reach a log line or
    /// the UI (matches the codebase's existing stance on server-controlled text -- see LogSanitizer /
    /// ApiClientRedactionTests). `error` itself IS modeled, but only ever consumed as an exact-match
    /// machine constant (see <see cref="QuotaBlockFlavor"/>) -- the raw string never escapes this class.
    /// </summary>
    internal static class QuotaInfoParser
    {
        /// <summary>Backend's confirmed machine constant for the no-storage-plan condition.</summary>
        internal const string ErrorCodePlanRequired = "PLAN_REQUIRED";

        /// <summary>Backend's confirmed machine constant for a real full-storage condition.</summary>
        internal const string ErrorCodeStorageLimitExceeded = "STORAGE_LIMIT_EXCEEDED";

        /// <summary>
        /// Parses <paramref name="json"/> into a <see cref="QuotaInfo"/>. Returns null when the body
        /// is empty or is not parseable JSON at all. Otherwise returns a <see cref="QuotaInfo"/>
        /// whose numeric fields are each independently validated -- a missing, negative, or absurdly
        /// large field becomes null on that field alone -- and whose <see cref="QuotaInfo.Flavor"/> is
        /// an EXACT match against <see cref="ErrorCodePlanRequired"/>; anything else (the other known
        /// code, an unrecognized code, or a missing/malformed `error` field) resolves to
        /// <see cref="QuotaBlockFlavor.StorageLimitExceeded"/>. This never throws.
        /// </summary>
        public static QuotaInfo TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            QuotaBody body;
            try
            {
                body = JsonConvert.DeserializeObject<QuotaBody>(json);
            }
            catch
            {
                // Malformed JSON (or a JSON value that isn't an object, e.g. a bare array/number) --
                // the quota numbers are simply unavailable; this must never fault the caller's
                // request/failure handling.
                return null;
            }

            if (body == null)
            {
                return null;
            }

            return new QuotaInfo
            {
                UsedBytes = QuotaByteValidator.Validate(body.UsedBytes),
                LimitBytes = QuotaByteValidator.Validate(body.LimitBytes),
                RemainingBytes = QuotaByteValidator.Validate(body.RemainingBytes),
                Flavor = string.Equals(body.Error, ErrorCodePlanRequired, StringComparison.Ordinal)
                    ? QuotaBlockFlavor.PlanRequired
                    : QuotaBlockFlavor.StorageLimitExceeded
            };
        }

        /// <summary>
        /// Wire shape of the 507 error body. `error` is modeled (as an exact-match machine constant
        /// only -- see <see cref="TryParse"/>); `message` is deliberately omitted so it can never be
        /// captured, logged, or displayed.
        /// </summary>
        private class QuotaBody
        {
            [JsonProperty("error")]
            public string Error { get; set; }

            [JsonProperty("usedBytes")]
            public long? UsedBytes { get; set; }

            [JsonProperty("limitBytes")]
            public long? LimitBytes { get; set; }

            [JsonProperty("remainingBytes")]
            public long? RemainingBytes { get; set; }
        }
    }
}
