using Astrovault.Models;
using Newtonsoft.Json;

namespace Astrovault.Core
{
    /// <summary>
    /// Parses the <c>GET v1/storage/nina/quota</c> JSON body -- <c>{accountId, planName, usedBytes,
    /// limitBytes, remainingBytes}</c> (storage-quota plan §4 Step 6; endpoint contract confirmed by
    /// backend §8) -- into an optional, VALIDATED <see cref="QuotaUsage"/>. The three numeric fields
    /// are validated with the exact same rules as <see cref="QuotaInfoParser"/> (via the shared
    /// <see cref="QuotaByteValidator"/>) -- a missing, negative, or absurdly large field becomes null on
    /// that field alone. This never throws; the caller (<c>AstrovaultApiClient.GetQuotaAsync</c>) treats
    /// a null return the same as any other "usage unavailable" outcome (hide the usage line silently).
    /// </summary>
    internal static class QuotaUsageParser
    {
        /// <summary>
        /// Parses <paramref name="json"/> into a <see cref="QuotaUsage"/>. Returns null when the body
        /// is empty or is not parseable JSON at all.
        /// </summary>
        public static QuotaUsage TryParse(string json)
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
                // Malformed JSON (or a JSON value that isn't an object) -- usage is simply unavailable;
                // this must never fault the caller.
                return null;
            }

            if (body == null)
            {
                return null;
            }

            return new QuotaUsage
            {
                AccountId = string.IsNullOrWhiteSpace(body.AccountId) ? null : body.AccountId,
                PlanName = string.IsNullOrWhiteSpace(body.PlanName) ? null : body.PlanName,
                UsedBytes = QuotaByteValidator.Validate(body.UsedBytes),
                LimitBytes = QuotaByteValidator.Validate(body.LimitBytes),
                RemainingBytes = QuotaByteValidator.Validate(body.RemainingBytes)
            };
        }

        /// <summary>Wire shape of the quota response.</summary>
        private class QuotaBody
        {
            [JsonProperty("accountId")]
            public string AccountId { get; set; }

            [JsonProperty("planName")]
            public string PlanName { get; set; }

            [JsonProperty("usedBytes")]
            public long? UsedBytes { get; set; }

            [JsonProperty("limitBytes")]
            public long? LimitBytes { get; set; }

            [JsonProperty("remainingBytes")]
            public long? RemainingBytes { get; set; }
        }
    }
}
