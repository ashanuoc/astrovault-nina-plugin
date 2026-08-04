using System;

namespace Astrovault.Models
{
    /// <summary>
    /// Latched queue-blocking state (storage-quota plan §4 Step 2). Set on <c>UploadManager</c> and
    /// durably mirrored in the queue.dat envelope (v3) via
    /// <see cref="Astrovault.Interfaces.IUploadQueueRepository.SetBlockAsync"/> /
    /// <see cref="Astrovault.Interfaces.IUploadQueueRepository.ClearBlockAsync"/> so a block survives
    /// a NINA restart.
    /// </summary>
    public enum UploadBlockKind
    {
        /// <summary>Not blocked. The default/cleared state.</summary>
        None,

        /// <summary><see cref="FailureKind.PermanentQuota"/> (507) -- storage quota exceeded.</summary>
        QuotaExceeded,

        /// <summary><see cref="FailureKind.PermanentAuth"/> (401/403) -- invalid or revoked API key.</summary>
        AuthInvalid
    }

    /// <summary>
    /// Numbers captured when a block was entered. Independently nullable -- null means "unavailable"
    /// (e.g. the 507 body was missing/malformed, or the block is <see cref="UploadBlockKind.AuthInvalid"/>,
    /// which carries no quota numbers at all). Never carries server `error`/`message` text (matches
    /// <see cref="QuotaInfo"/>'s existing stance -- see storage-quota plan §4 Step 1).
    /// </summary>
    public class UploadBlockSnapshot
    {
        /// <summary>Bytes used at the moment the block was entered, or null if unavailable.</summary>
        public long? UsedBytes { get; set; }

        /// <summary>The account's storage limit at that moment, or null if unavailable.</summary>
        public long? LimitBytes { get; set; }

        /// <summary>
        /// UTC time the block was entered. Always set by the repository at commit time (never trusts
        /// a caller-supplied clock) so it reflects when the transition was actually persisted.
        /// </summary>
        public DateTime? TimestampUtc { get; set; }

        /// <summary>
        /// Which quota-block condition this is (contract-delta round). Additive envelope field --
        /// defaults to <see cref="QuotaBlockFlavor.StorageLimitExceeded"/>, so a pre-existing v3
        /// persisted file (written before this field existed) deserializes to that same safe default,
        /// and a block latched for <see cref="UploadBlockKind.AuthInvalid"/> (which carries no quota
        /// numbers at all) leaves this at the meaningless-but-harmless default too.
        /// </summary>
        public QuotaBlockFlavor Flavor { get; set; } = QuotaBlockFlavor.StorageLimitExceeded;
    }

    /// <summary>
    /// Caller-supplied identity input to <see cref="Astrovault.Interfaces.IUploadQueueRepository.SetBlockAsync"/>.
    /// <see cref="ApiKey"/> is the RAW key -- it is used only to derive a salted HMAC fingerprint
    /// inside the repository and is never itself persisted or exposed (storage-quota plan §4 Step 2,
    /// point 4: "never a bare digest").
    /// </summary>
    public class UploadBlockIdentityInput
    {
        /// <summary>Canonicalized API endpoint URL the block was entered against.</summary>
        public string EndpointUrl { get; set; }

        /// <summary>
        /// The account id, when known. Null today -- the quota endpoint
        /// (<c>GetQuotaAsync</c>, storage-quota plan step 6) does not exist yet, so this is accepted
        /// as an optional parameter and threaded through in a later step.
        /// </summary>
        public string AccountId { get; set; }

        /// <summary>The raw, currently-active API key. Used only to compute <see cref="UploadBlockIdentity.KeyFingerprint"/>.</summary>
        public string ApiKey { get; set; }
    }

    /// <summary>
    /// Persisted/exposed identity a block is scoped to. Carries a keyed fingerprint of the API key
    /// that was active when the block was entered -- NEVER the raw key itself.
    /// </summary>
    public class UploadBlockIdentity
    {
        /// <summary>Canonicalized API endpoint URL the block was entered against.</summary>
        public string EndpointUrl { get; set; }

        /// <summary>The account id captured at block time, or null when not yet known.</summary>
        public string AccountId { get; set; }

        /// <summary>
        /// HMAC-SHA256 of the API key active when the block was entered, keyed with a random
        /// per-installation salt (see <c>UploadQueueRepository</c>'s identity-salt handling). Never a
        /// bare digest of the key.
        /// </summary>
        public string KeyFingerprint { get; set; }
    }

    /// <summary>
    /// Full latched block state: which kind, the snapshot recorded when it was entered, and the
    /// identity it is scoped to. <see cref="Kind"/> == <see cref="UploadBlockKind.None"/> represents
    /// "not blocked" -- <see cref="Snapshot"/>/<see cref="Identity"/> are then meaningless (typically null).
    /// </summary>
    public class UploadBlockState
    {
        public UploadBlockKind Kind { get; set; } = UploadBlockKind.None;

        public UploadBlockSnapshot Snapshot { get; set; }

        public UploadBlockIdentity Identity { get; set; }

        /// <summary>
        /// The id of the job whose failure triggered this block (storage-quota plan §4 Step 5). Persisted
        /// so <c>UploadManager.ResumeAsync</c> can pin the SAME job as the resume probe even after a NINA
        /// restart (a fresh manager instance has no in-memory record of which job it was). Null when not
        /// blocked, or for a block restored from a pre-Step-5 v3 file that predates this field (additive,
        /// tolerant of absence -- resume then falls back to no pin, i.e. normal drain order).
        /// </summary>
        public Guid? FailedJobId { get; set; }

        /// <summary>Shared "not blocked" instance. Never mutate this instance's properties in place --
        /// treat it as an immutable default and always assign a brand-new object on a real transition.</summary>
        public static UploadBlockState None { get; } = new UploadBlockState { Kind = UploadBlockKind.None };
    }
}
