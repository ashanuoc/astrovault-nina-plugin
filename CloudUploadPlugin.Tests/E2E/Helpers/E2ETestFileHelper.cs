using System;
using System.IO;

namespace CloudUploadPlugin.Tests.E2E.Helpers
{
    /// <summary>
    /// Generates random binary test files at specified sizes.
    /// Uses a deterministic seed for reproducible content.
    /// </summary>
    public static class E2ETestFileHelper
    {
        /// <summary>1 KB baseline test file size.</summary>
        public const long SmallFileSize = 1024;

        /// <summary>4 MB -- below the 5 MB chunked upload threshold.</summary>
        public const long SingleUploadSize = 4 * 1024 * 1024;

        /// <summary>6 MB -- above the 5 MB chunked upload threshold.</summary>
        public const long ChunkedUploadSize = 6 * 1024 * 1024;

        /// <summary>
        /// Creates a test file filled with deterministic random bytes.
        /// </summary>
        /// <param name="directory">Directory to create the file in.</param>
        /// <param name="sizeBytes">Desired file size in bytes.</param>
        /// <param name="fileName">Optional file name. If null, generates a unique name.</param>
        /// <returns>Full path to the created file.</returns>
        public static string CreateTestFile(string directory, long sizeBytes, string fileName = null)
        {
            fileName ??= $"test-{Guid.NewGuid():N}.bin";
            var filePath = Path.Combine(directory, fileName);

            var random = new Random(42);
            var buffer = new byte[8192];
            long remaining = sizeBytes;

            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                while (remaining > 0)
                {
                    int toWrite = (int)Math.Min(buffer.Length, remaining);
                    random.NextBytes(buffer);
                    stream.Write(buffer, 0, toWrite);
                    remaining -= toWrite;
                }
            }

            return filePath;
        }
    }
}
