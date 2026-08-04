namespace Astrovault.Models
{
    /// <summary>
    /// Validated response from <c>GET v1/storage/nina/quota</c> (storage-quota plan §4 Step 6; endpoint
    /// contract confirmed by backend §8). Always requested on demand -- never cached beyond the last
    /// fetch and never polled. <see cref="UsedBytes"/>/<see cref="LimitBytes"/>/<see cref="RemainingBytes"/>
    /// are validated with the exact same rules as <see cref="QuotaInfo"/> (nullable non-negative
    /// <c>long</c>s; a missing/negative/absurdly-large value becomes null, never displayed). Unlike the
    /// 507 error body, this response also carries <see cref="AccountId"/> (used for block identity
    /// scoping -- storage-quota plan §4 Step 2 point 4) and an optional <see cref="PlanName"/> for
    /// display; both are structured account metadata the backend contract explicitly names, not the
    /// free-form `error`/`message` text <see cref="QuotaInfo"/> deliberately omits.
    /// </summary>
    public class QuotaUsage
    {
        /// <summary>Stable account identifier, or null if the field was absent/blank.</summary>
        public string AccountId { get; set; }

        /// <summary>Display name of the account's plan, or null if the field was absent/blank.</summary>
        public string PlanName { get; set; }

        /// <summary>Bytes currently used by the account, or null if unavailable.</summary>
        public long? UsedBytes { get; set; }

        /// <summary>The account's storage limit in bytes, or null if unavailable.</summary>
        public long? LimitBytes { get; set; }

        /// <summary>Bytes remaining (server-floored at 0), or null if unavailable.</summary>
        public long? RemainingBytes { get; set; }
    }
}
