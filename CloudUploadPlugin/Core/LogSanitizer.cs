using System;
using System.IO;

namespace Astrovault.Core
{
    /// <summary>
    /// Utility for masking sensitive data in log output.
    /// Prevents PII and credentials from appearing in logs.
    /// </summary>
    public static class LogSanitizer
    {
        /// <summary>
        /// Masks an email address for safe logging (e.g., "te***@domain.com").
        /// </summary>
        /// <param name="email">The email address to mask.</param>
        /// <returns>Masked email string, or "[empty]" for null/empty input.</returns>
        public static string MaskEmail(string email)
        {
            if (string.IsNullOrEmpty(email))
            {
                return "[empty]";
            }

            var atIndex = email.IndexOf('@');
            if (atIndex <= 0)
            {
                return "***";
            }

            var localPart = email.Substring(0, atIndex);
            var domain = email.Substring(atIndex);

            // Show first 2 chars of local part, mask the rest
            var visibleChars = Math.Min(2, localPart.Length);
            var masked = localPart.Substring(0, visibleChars) + "***";

            return masked + domain;
        }

        /// <summary>
        /// Masks an API key for safe logging.
        /// Returns "[empty]" for null/empty, "****" for 4-or-fewer chars,
        /// "...{last4}" for longer keys.
        /// </summary>
        /// <param name="apiKey">The API key to mask.</param>
        /// <returns>Masked key string safe for logging.</returns>
        public static string MaskApiKey(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                return "[empty]";
            }

            if (apiKey.Length <= 4)
            {
                return "****";
            }

            return $"...{apiKey[^4..]}";
        }

        /// <summary>
        /// Masks a local file path for safe logging by returning the filename ONLY.
        /// Drops the directory/site/target structure so a NINA log shared in a public
        /// support channel cannot leak the user's folder layout or target list.
        /// </summary>
        /// <param name="fullPath">The local path to mask.</param>
        /// <returns>The filename only, "[empty]" for null/empty, or "[path]" if the path is malformed.</returns>
        public static string MaskPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return "[empty]";
            }

            try
            {
                return Path.GetFileName(fullPath);
            }
            catch
            {
                return "[path]";
            }
        }

        /// <summary>
        /// Masks an account/observatory name for safe logging. Returns the first character
        /// plus the length (e.g., "O***(17)") so support can correlate a name across logs
        /// without the full observatory/account name being revealed.
        /// </summary>
        /// <param name="name">The account name to mask.</param>
        /// <returns>"[empty]" for null/empty, "*" for a single character, otherwise first-char + length.</returns>
        public static string MaskAccountName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "[empty]";
            }

            return name.Length <= 1 ? "*" : $"{name[0]}***({name.Length})";
        }
    }
}
