using System;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Models;

namespace Astrovault.Interfaces
{
    /// <summary>
    /// Contract for Astrovault cloud API communication.
    /// Implement this interface for mock testing or real API integration.
    /// </summary>
    public interface ICloudApiClient : IDisposable
    {
        /// <summary>
        /// Tests connectivity to the cloud API.
        /// </summary>
        /// <returns>True if connection successful and credentials valid.</returns>
        Task<bool> TestConnectionAsync();

        /// <summary>
        /// Uploads a file to cloud storage with metadata.
        /// Routes to single-upload or chunked-upload based on file size.
        /// </summary>
        /// <param name="job">Upload job containing file path, remote path, metadata, and resume state.</param>
        /// <param name="progress">Progress reporter (0.0 to 1.0).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Upload result with success status and remote URL.</returns>
        Task<UploadResult> UploadFileAsync(
            UploadJob job,
            IProgress<double> progress,
            CancellationToken cancellationToken);

        /// <summary>
        /// Gets the base URL of the configured API endpoint.
        /// </summary>
        string BaseUrl { get; }
    }
}
