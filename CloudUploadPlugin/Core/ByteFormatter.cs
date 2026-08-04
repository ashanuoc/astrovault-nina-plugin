using System.Globalization;

namespace Astrovault.Core
{
    /// <summary>
    /// Shared "N.N GB" formatter for user-facing storage numbers (storage-quota plan §4 Steps 3 and 6:
    /// the quota-blocked banner and the Upload Status usage line). Uses BINARY gibibytes (1024^3)
    /// internally but labels the unit "GB", matching the numbers shown on the Astrovault web site (not
    /// strictly SI, but consistent with what the user already sees there). Always one decimal place, so
    /// "16.0 GB of 10.0 GB used" reads identically regardless of how round the underlying byte count is.
    /// </summary>
    internal static class ByteFormatter
    {
        private const double BytesPerGiB = 1024d * 1024 * 1024;

        private const double BytesPerMiB = 1024d * 1024;

        /// <summary>
        /// Formats a validated non-negative byte count as "N.N GB". Callers must only pass a value that
        /// has already gone through <see cref="QuotaByteValidator"/> (or an equivalent guard) -- this
        /// method does no validation of its own and is display formatting only.
        /// </summary>
        public static string FormatGB(long bytes)
        {
            var gib = bytes / BytesPerGiB;
            return $"{gib.ToString("F1", CultureInfo.InvariantCulture)} GB";
        }

        /// <summary>
        /// Two-decimal variant for the Upload Status usage line (owner request, dev-testing round):
        /// one decimal is blind near the limit -- 9.96 GB of 10.00 GB rendered as "10.0 of 10.0"
        /// hides the ~40 MB that decides whether tonight's frames still fit. The BANNER keeps the
        /// one-decimal <see cref="FormatGB"/> (site-matching convention; at the stop moment the
        /// difference is never subtle).
        /// </summary>
        public static string FormatGB2(long bytes)
        {
            var gib = bytes / BytesPerGiB;
            return $"{gib.ToString("F2", CultureInfo.InvariantCulture)} GB";
        }

        /// <summary>
        /// Compact adaptive form for the "free" suffix: whole MB below 1 GiB ("44 MB"), otherwise
        /// two-decimal GB. Same validated-input contract as the other formatters.
        /// </summary>
        public static string FormatCompact(long bytes)
        {
            if (bytes < BytesPerGiB)
            {
                var mib = bytes / BytesPerMiB;
                return $"{mib.ToString("F0", CultureInfo.InvariantCulture)} MB";
            }
            return FormatGB2(bytes);
        }
    }
}
