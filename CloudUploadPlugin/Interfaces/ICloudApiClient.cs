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

        /// <summary>
        /// Storage-quota plan §4 Step 6: fetches the account's current storage usage from
        /// <c>GET v1/storage/nina/quota</c> (endpoint contract confirmed by backend §8). No caching --
        /// callers control the refresh cadence (Test-Connection success, queue transitioning to idle,
        /// block transitions -- NO polling/timers). Bounded by a short per-call timeout.
        /// <para>
        /// Returns null -- never throws -- when the usage is unavailable for ANY reason: no API key,
        /// the HTTPS send-boundary gate rejects the endpoint, a 404 (older server predating this
        /// endpoint -- NOT an error; hide the usage line silently, no toast, no log spam beyond debug),
        /// any other non-2xx status, a malformed/unparseable body, or a timeout/network fault.
        /// </para>
        /// </summary>
        Task<QuotaUsage> GetQuotaAsync(CancellationToken cancellationToken);
    }
}
