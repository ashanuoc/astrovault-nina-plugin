using System;

namespace Astrovault.Interfaces
{
    /// <summary>
    /// Contract for resolving and manipulating file paths.
    /// Converts local absolute paths to relative cloud storage paths.
    /// </summary>
    public interface IPathResolver
    {
        /// <summary>
        /// Extracts a relative path from an absolute path.
        /// Example: "D:\Astro\M31\Light\001.fits" with base "D:\Astro" returns "M31/Light/001.fits"
        /// </summary>
        /// <param name="absolutePath">Full local file path.</param>
        /// <param name="basePath">Base directory to make path relative to.</param>
        /// <returns>Relative path using forward slashes for cloud compatibility.</returns>
        string GetRelativePath(string absolutePath, string basePath);

        /// <summary>
        /// Converts a URI (from N.I.N.A. ImageSavedEventArgs.PathToImage) to a cloud upload path.
        /// Uses IProfileService.ActiveProfile.ImageFileSettings.FilePath as base to mirror local structure.
        /// </summary>
        /// <param name="imageUri">URI pointing to the saved image file.</param>
        /// <returns>Relative path mirroring user's local folder structure.</returns>
        string GetUploadPath(Uri imageUri);
    }
}
