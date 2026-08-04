using System;
using Astrovault.Models;

namespace Astrovault.Core
{
    /// <summary>
    /// Pure identity-scoping rules for the persisted block (storage-quota plan §4 Step 2, point 4).
    /// Deliberately free of any I/O (DPAPI, HTTP, disk) so the matching logic is unit-testable in
    /// isolation. Endpoint canonicalization and comparison both live here so callers never hand-roll
    /// string comparisons that could silently diverge between the "set" and "compare" call sites.
    /// </summary>
    internal static class UploadBlockIdentityMatcher
    {
        /// <summary>
        /// Canonicalizes an endpoint URL for identity comparison: lowercases scheme+host, drops a
        /// default port for that scheme, and PRESERVES the base path (storage-quota plan directive A#7)
        /// with only query/fragment/trailing-slash noise stripped -- collapsing to
        /// <c>scheme://host[:port][/path]</c>. Two custom backends that share a host but differ only by
        /// base path (e.g. a gateway routing <c>/prod</c> vs <c>/staging</c> to different servers) are
        /// DISTINCT identities; dropping the path used to collapse them into one, letting a reused key
        /// retain a quota/auth block across the wrong server. Never throws -- an unparsable value is
        /// returned trimmed and lowercased as a best-effort fallback so comparison still degrades
        /// gracefully instead of faulting the caller.
        /// </summary>
        public static string CanonicalizeEndpoint(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            var trimmed = url.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                var scheme = uri.Scheme.ToLowerInvariant();
                var host = uri.Host.ToLowerInvariant();
                var authority = uri.IsDefaultPort
                    ? $"{scheme}://{host}"
                    : $"{scheme}://{host}:{uri.Port}";

                // uri.AbsolutePath already excludes query/fragment; strip only the trailing slash so
                // "/v1/storage/nina" and "/v1/storage/nina/" canonicalize identically, and the bare-root
                // case ("/") collapses to "" (no noise appended to the authority).
                var path = uri.AbsolutePath.TrimEnd('/');
                return authority + path;
            }

            // Not a parseable absolute URL -- best-effort fallback, never throw.
            return trimmed.TrimEnd('/').ToLowerInvariant();
        }

        /// <summary>
        /// True when the persisted block identity is considered the SAME as the current one (the block
        /// should be retained), false when it is a DIFFERENT ("foreign") identity (the block should be
        /// durably cleared). Rules (storage-quota plan §4 Step 2 point 4, refined by directive E):
        /// <list type="bullet">
        /// <item>No persisted identity to compare against -- nothing to reconcile, treated as a match.</item>
        /// <item>The CURRENT identity is unavailable (<paramref name="currentFingerprint"/> is null/empty,
        /// e.g. auth.dat was lost while queue.dat survived) -- RETAINED, never treated as foreign.</item>
        /// <item>The persisted side has an <c>AccountId</c> but the CURRENT one is not yet known (e.g. the
        /// quota endpoint hasn't returned this session) -- an INCOMPLETE comparison, RETAINED rather than
        /// falling back to a fingerprint/endpoint guess; reconciliation happens once an accountId
        /// arrives (see <c>UploadManager.SetAccountIdAsync</c>).</item>
        /// <item>Both sides carry an <c>AccountId</c> -- compare that (ordinal) AND the canonicalized
        /// endpoint (a matching account id on a DIFFERENT endpoint is not a match -- the same account
        /// could exist against a different custom backend).</item>
        /// <item>Otherwise (neither side has a persisted <c>AccountId</c>) -- compare KeyFingerprint
        /// (ordinal) AND canonicalized endpoint (ordinal, case-insensitive already baked into
        /// <see cref="CanonicalizeEndpoint"/> output).</item>
        /// </list>
        /// </summary>
        public static bool MatchesCurrent(
            UploadBlockIdentity persisted,
            string currentEndpointCanonical,
            string currentAccountId,
            string currentFingerprint)
        {
            if (persisted == null)
            {
                return true; // nothing persisted to compare against
            }

            if (string.IsNullOrEmpty(currentFingerprint))
            {
                // Missing CURRENT identity (no API key available right now) -- retain, never "foreign".
                return true;
            }

            if (!string.IsNullOrEmpty(persisted.AccountId))
            {
                if (string.IsNullOrEmpty(currentAccountId))
                {
                    // Incomplete comparison: retain rather than risk a false-foreign clear off a
                    // fingerprint/endpoint fallback that says nothing about the ACCOUNT.
                    return true;
                }

                return string.Equals(persisted.AccountId, currentAccountId, StringComparison.Ordinal)
                       && string.Equals(persisted.EndpointUrl ?? string.Empty, currentEndpointCanonical ?? string.Empty, StringComparison.Ordinal);
            }

            return string.Equals(persisted.KeyFingerprint, currentFingerprint, StringComparison.Ordinal)
                   && string.Equals(persisted.EndpointUrl ?? string.Empty, currentEndpointCanonical ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
