namespace Astrovault.Models
{
    /// <summary>
    /// Distinguishes the two permanent 507 stop-queue conditions the backend's flat error body can
    /// carry (contract-delta round, backend's confirmed <c>error</c> codes): a real full-storage
    /// condition vs. an account with NO storage plan at all (where "free up space" is a dead end --
    /// the only fix is choosing a plan on the website). Parsed as a MACHINE CONSTANT from the body's
    /// `error` field (exact match only) -- never the server's freeform `message` text. Both flavors
    /// share the SAME <see cref="Astrovault.Models.UploadBlockKind.QuotaExceeded"/> latch/classification;
    /// this enum only selects which fixed local banner/toast wording is shown.
    /// </summary>
    public enum QuotaBlockFlavor
    {
        /// <summary>
        /// The default. Used for the `STORAGE_LIMIT_EXCEEDED` code, and as the safe fallback for any
        /// unknown/missing/malformed `error` value -- also what an older pre-Step-additive persisted
        /// envelope (predating this field) deserializes to.
        /// </summary>
        StorageLimitExceeded = 0,

        /// <summary>
        /// The `PLAN_REQUIRED` code -- the account has no storage plan at all. `limitBytes` is 0;
        /// the banner must never show byte numbers for this flavor (see
        /// <see cref="Astrovault.AstrovaultPlugin.QuotaBlockedBannerText"/>).
        /// </summary>
        PlanRequired
    }

    /// <summary>
    /// Validated snapshot of the server's storage-quota numbers, parsed from a 507 response body
    /// (storage-quota plan §4 Step 1; the quota endpoint reuses the same shape in a later step).
    /// Each field is independently nullable -- null means "unavailable" (missing from the body,
    /// negative, or absurdly large), never zero. Deliberately carries no `error`/`message` TEXT:
    /// server-provided strings are never displayed, logged, or persisted -- <see cref="Flavor"/> is
    /// the one exception, and it is not text -- it is a machine constant matched exact-only against
    /// the two known codes (see <see cref="QuotaBlockFlavor"/>), never the raw server string.
    /// </summary>
    public class QuotaInfo
    {
        /// <summary>Bytes currently used by the account, or null if unavailable.</summary>
        public long? UsedBytes { get; set; }

        /// <summary>The account's storage limit in bytes, or null if unavailable.</summary>
        public long? LimitBytes { get; set; }

        /// <summary>Bytes remaining (server-floored at 0), or null if unavailable.</summary>
        public long? RemainingBytes { get; set; }

        /// <summary>
        /// Which permanent quota-block condition this is. Defaults to
        /// <see cref="QuotaBlockFlavor.StorageLimitExceeded"/> -- the safe fallback for a missing,
        /// unknown, or malformed `error` code (never the newer <see cref="QuotaBlockFlavor.PlanRequired"/>
        /// unless the body's `error` field is an EXACT match for that code).
        /// </summary>
        public QuotaBlockFlavor Flavor { get; set; } = QuotaBlockFlavor.StorageLimitExceeded;
    }
}
