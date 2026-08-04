using Astrovault.Models;

namespace Astrovault.Core
{
    /// <summary>
    /// Single source of truth for status-code -> <see cref="FailureKind"/> classification, per the
    /// backend's confirmed per-code table (storage-quota plan §8, 2026-08-02):
    /// <list type="bullet">
    /// <item>507 -&gt; stop queue, no retry (<see cref="FailureKind.PermanentQuota"/>)</item>
    /// <item>401/403 -&gt; stop queue, no retry (<see cref="FailureKind.PermanentAuth"/>)</item>
    /// <item>400 (malformed request) -&gt; skip this file, continue queue, no retry (<see cref="FailureKind.PermanentSkipFile"/>)</item>
    /// <item>413 (over per-request cap) -&gt; skip this file, continue queue, no retry (<see cref="FailureKind.PermanentSkipFile"/>)</item>
    /// <item>429/503 -&gt; stays Transient here; Retry-After is honored where it already is today
    /// (the chunk retry loop) -- no new delay plumbing lives in this classifier</item>
    /// <item>everything else (network/timeout, 5xx, unrecognized non-2xx) -&gt; Transient</item>
    /// </list>
    /// Session-route 404/410 are deliberately NOT modeled here. Those keep their existing
    /// route-specific session-expiry semantics ("restart that upload, don't retry the chunk") --
    /// callers on those routes MUST check for 404/410 first and only fall back to this classifier
    /// for every other non-2xx status, so this classifier never globalizes them.
    /// </summary>
    internal static class FailureClassifier
    {
        // Each status code lives in exactly one constant (storage-quota plan §4 Step 1).
        internal const int StatusBadRequest = 400;
        internal const int StatusUnauthorized = 401;
        internal const int StatusForbidden = 403;
        internal const int StatusPayloadTooLarge = 413;
        internal const int StatusInsufficientStorage = 507;

        /// <summary>
        /// Classifies an HTTP status code into a <see cref="FailureKind"/>. Deliberately treats
        /// 404/410 as Transient (the harmless default) -- callers on routes where those codes carry
        /// route-specific session-expiry meaning must intercept them before reaching this method.
        /// </summary>
        public static FailureKind Classify(int statusCode)
        {
            switch (statusCode)
            {
                case StatusInsufficientStorage:
                    return FailureKind.PermanentQuota;
                case StatusUnauthorized:
                case StatusForbidden:
                    return FailureKind.PermanentAuth;
                case StatusBadRequest:
                case StatusPayloadTooLarge:
                    return FailureKind.PermanentSkipFile;
                default:
                    return FailureKind.Transient;
            }
        }
    }
}
