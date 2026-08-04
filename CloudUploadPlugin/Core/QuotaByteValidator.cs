namespace Astrovault.Core
{
    /// <summary>
    /// Shared numeric validation for server-reported storage-quota byte counts (storage-quota plan §4
    /// Steps 1 and 6). A missing, negative, or absurdly large value is "unavailable" (null) rather than
    /// displayed. Used identically by <see cref="QuotaInfoParser"/> (507 error body) and
    /// <see cref="QuotaUsageParser"/> (<c>GET .../quota</c> response) so the two endpoints can never
    /// drift on what counts as a sane value -- matches the codebase's "each rule lives in exactly one
    /// place" convention (see <see cref="FailureClassifier"/>).
    /// </summary>
    internal static class QuotaByteValidator
    {
        // Not a real quota limit -- a generous sanity ceiling against a malformed/corrupt body
        // producing a nonsensical value in the UI.
        internal const long MaxSaneBytes = 1024L * 1024 * 1024 * 1024 * 1024; // 1 PiB

        internal static long? Validate(long? value)
        {
            if (!value.HasValue) return null;
            if (value.Value < 0 || value.Value > MaxSaneBytes) return null;
            return value;
        }
    }
}
