using System;
using System.IO;
using Astrovault.Interfaces;
using NINA.Profile.Interfaces;

namespace Astrovault.Core
{
    /// <summary>
    /// Resolves local file paths to cloud-relative paths.
    /// Uses NINA's profile settings to mirror user's local folder structure.
    /// </summary>
    public class PathResolver : IPathResolver
    {
        private readonly IProfileService profileService;

        public PathResolver(IProfileService profileService)
        {
            this.profileService = profileService;
        }

        /// <summary>
        /// Extracts a relative path from an absolute path.
        /// Example: "D:\Astro\M31\Light\001.fits" with base "D:\Astro" returns "M31/Light/001.fits"
        /// </summary>
        public string GetRelativePath(string absolutePath, string basePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
                return string.Empty;

            if (string.IsNullOrEmpty(basePath))
                return NormalizeToForwardSlashes(absolutePath);

            // Normalize paths for comparison
            var normalizedAbsolute = Path.GetFullPath(absolutePath);
            var normalizedBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar);

            // Check if absolute path starts with base path
            if (!normalizedAbsolute.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
                return NormalizeToForwardSlashes(Path.GetFileName(absolutePath));

            // Extract relative portion
            var relativePath = normalizedAbsolute.Substring(normalizedBase.Length)
                .TrimStart(Path.DirectorySeparatorChar);

            return NormalizeToForwardSlashes(relativePath);
        }

        /// <summary>
        /// Converts a URI (from N.I.N.A. ImageSavedEventArgs.PathToImage) to a cloud upload path.
        /// Uses NINA's configured image save path as base to mirror local folder structure.
        /// </summary>
        public string GetUploadPath(Uri imageUri)
        {
            if (imageUri == null)
                return string.Empty;

            var localPath = imageUri.LocalPath;
            var basePath = profileService.ActiveProfile.ImageFileSettings.FilePath;

            return GetRelativePath(localPath, basePath);
        }

        /// <summary>
        /// Converts Windows backslashes to forward slashes for cloud storage compatibility.
        /// </summary>
        private static string NormalizeToForwardSlashes(string path)
        {
            return path?.Replace('\\', '/') ?? string.Empty;
        }
    }
}
