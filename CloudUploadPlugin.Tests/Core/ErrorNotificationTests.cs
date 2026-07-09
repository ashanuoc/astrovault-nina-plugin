using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Astrovault;
using Astrovault.Core;
using Astrovault.Data;
using Astrovault.Interfaces;
using Astrovault.Models;
using CloudUploadPlugin.Tests.Helpers;
using Moq;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Tests for error message mapping (ToActionableError) and
    /// NotificationVerbosity enum parsing/persistence logic.
    /// </summary>
    [TestFixture]
    [Category("ErrorNotifications")]
    public class ErrorNotificationTests
    {
        // ====================================================================
        // ToActionableError: Error Message Mapping
        // ====================================================================

        [Test]
        public void ToActionableError_FileNotFound_ReturnsDiskMessage()
        {
            var result = AstrovaultPlugin.ToActionableError("test.fits", "File not found at C:\\images\\test.fits");

            Assert.That(result, Does.StartWith("test.fits:"));
            Assert.That(result, Does.Contain("deleted or moved"));
        }

        [Test]
        public void ToActionableError_AccessDenied_ReturnsPermissionMessage()
        {
            var result = AstrovaultPlugin.ToActionableError("test.fits", "Access denied to file");

            Assert.That(result, Does.StartWith("test.fits:"));
            Assert.That(result, Does.Contain("Permission denied"));
        }

        [Test]
        public void ToActionableError_UnauthorizedAccess_ReturnsPermissionMessage()
        {
            var result = AstrovaultPlugin.ToActionableError("test.fits", "unauthorized access to resource");

            Assert.That(result, Does.StartWith("test.fits:"));
            Assert.That(result, Does.Contain("Permission denied"));
        }

        [Test]
        public void ToActionableError_InvalidApiKey_ReturnsApiKeyMessage()
        {
            var result = AstrovaultPlugin.ToActionableError("test.fits", "Invalid API key");

            Assert.That(result, Does.StartWith("test.fits:"));
            Assert.That(result, Does.Contain("API key rejected"));
        }

        [Test]
        public void ToActionableError_NetworkTimeout_ReturnsTimeoutMessage()
        {
            var result = AstrovaultPlugin.ToActionableError("test.fits", "Request timed out");

            Assert.That(result, Does.StartWith("test.fits:"));
            Assert.That(result, Does.Contain("Network timeout"));
        }

        [Test]
        public void ToActionableError_SessionExpired_ReturnsSessionMessage()
        {
            var result = AstrovaultPlugin.ToActionableError("test.fits", "Upload session expired");

            Assert.That(result, Does.StartWith("test.fits:"));
            Assert.That(result, Does.Contain("session expired"));
        }

        [Test]
        public void ToActionableError_ServerError500_ReturnsServerMessage()
        {
            var result = AstrovaultPlugin.ToActionableError("test.fits", "Server returned 500");

            Assert.That(result, Does.StartWith("test.fits:"));
            Assert.That(result, Does.Contain("Server error"));
        }

        [Test]
        public void ToActionableError_RateLimit429_ReturnsRateLimitMessage()
        {
            var result = AstrovaultPlugin.ToActionableError("test.fits", "429 Too Many Requests");

            Assert.That(result, Does.StartWith("test.fits:"));
            Assert.That(result, Does.Contain("Rate limited"));
        }

        [Test]
        public void ToActionableError_NullInput_ReturnsUnknownError()
        {
            var result = AstrovaultPlugin.ToActionableError("test.fits", null);

            Assert.That(result, Does.StartWith("test.fits:"));
            Assert.That(result, Does.Contain("Unknown error"));
        }

        [Test]
        public void ToActionableError_EmptyInput_ReturnsUploadFailed()
        {
            var result = AstrovaultPlugin.ToActionableError("test.fits", "");

            Assert.That(result, Does.StartWith("test.fits:"));
            Assert.That(result, Does.Contain("Upload failed"));
        }

        [Test]
        public void ToActionableError_UnrecognizedError_PassesThroughRawMessage()
        {
            var result = AstrovaultPlugin.ToActionableError("test.fits", "Some weird error");

            Assert.That(result, Does.StartWith("test.fits:"));
            Assert.That(result, Does.Contain("Some weird error"));
        }

        // ====================================================================
        // NotificationVerbosity: Enum Parsing & Persistence
        // ====================================================================

        [Test]
        public void NotificationVerbosity_DefaultIsErrorsOnly()
        {
            // Default stored value is "ErrorsOnly" -- Enum.TryParse should succeed
            var success = Enum.TryParse<NotificationVerbosity>("ErrorsOnly", out var result);

            Assert.That(success, Is.True, "Should parse 'ErrorsOnly' successfully");
            Assert.That(result, Is.EqualTo(NotificationVerbosity.ErrorsOnly));
        }

        [Test]
        public void NotificationVerbosity_PersistsAcrossReads()
        {
            // Roundtrip: enum -> string -> enum
            var original = NotificationVerbosity.AllUploads;
            var stored = original.ToString();
            var success = Enum.TryParse<NotificationVerbosity>(stored, out var restored);

            Assert.That(success, Is.True, "Should parse stored string successfully");
            Assert.That(restored, Is.EqualTo(NotificationVerbosity.AllUploads),
                "Restored value should match original");
        }

        [Test]
        public void NotificationVerbosity_InvalidStoredValue_FallsBackToErrorsOnly()
        {
            // Invalid stored string should fail TryParse -> fallback to ErrorsOnly
            var success = Enum.TryParse<NotificationVerbosity>("garbage", out var result);

            Assert.That(success, Is.False, "Should fail to parse 'garbage'");
            // Fallback logic: if !success, use ErrorsOnly
            var fallback = success ? result : NotificationVerbosity.ErrorsOnly;
            Assert.That(fallback, Is.EqualTo(NotificationVerbosity.ErrorsOnly),
                "Invalid value should fall back to ErrorsOnly");
        }

        // ====================================================================
        // D-12: persist a last-failure-reason on the job and surface it through
        // the EXISTING error-notification path (no new UI surface).
        // ====================================================================

        [Test]
        public async Task TerminalFailure_SetsLastFailureReason_AndSurfacesViaExistingNotification()
        {
            // Real repository so the persisted field is observable; permanent failure -> terminal Failed.
            var tempDir = Path.Combine(Path.GetTempPath(), "AstrovaultD12_" + Guid.NewGuid().ToString("N"));
            try
            {
                var repo = new UploadQueueRepository(tempDir);
                var apiClient = new Mock<ICloudApiClient>();
                var authManager = new Mock<IAuthManager>();
                var manager = new UploadManager(repo, apiClient.Object, authManager.Object);

                UploadCompletedEventArgs notified = null;
                manager.UploadCompleted += (s, e) => notified = e;

                var job = TestDataFactory.CreateUploadJob(retryCount: 0);
                await repo.EnqueueAsync(job);

                const string reason = "File not found at C:\\images\\m31.fits";
                await manager.HandleUploadFailure(job, UploadResult.FailPermanent(reason), CancellationToken.None);

                // Persisted on the job record (D-12).
                var failed = (await repo.GetJobsByStatusAsync(UploadStatus.Failed)).Single();
                Assert.That(failed.LastFailureReason, Is.EqualTo(reason),
                    "Terminal failure must persist LastFailureReason on the job record");

                // Surfaced via the EXISTING error-notification path (no new UI surface).
                Assert.That(notified, Is.Not.Null, "The existing UploadCompleted notification must fire");
                Assert.That(notified.Success, Is.False);
                Assert.That(notified.ErrorMessage, Is.EqualTo(reason),
                    "The existing error notification must carry the last-failure reason");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            }
        }

        [Test]
        public async Task LastFailureReason_PreservedAcrossPromotionAndManualRetry()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "AstrovaultD12Hist_" + Guid.NewGuid().ToString("N"));
            try
            {
                var repo = new UploadQueueRepository(tempDir);
                var transient = TestDataFactory.CreateUploadJob(retryCount: 0);
                var permanent = TestDataFactory.CreateUploadJob(retryCount: 0);
                await repo.EnqueueAsync(transient);
                await repo.EnqueueAsync(permanent);

                await repo.MarkFailedAsync(transient.Id, "transient reason", isPermanent: false);
                await repo.MarkFailedAsync(permanent.Id, "permanent reason", isPermanent: true);

                // Auto-promotion (transient) must PRESERVE LastFailureReason as history (not clear it).
                await repo.PromoteTransientFailedToPendingAsync();
                var promoted = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).Single();
                Assert.That(promoted.LastFailureReason, Is.EqualTo("transient reason"),
                    "Auto-promotion must preserve LastFailureReason as history");

                // Manual retry (permanent) must also PRESERVE LastFailureReason.
                await repo.RetryJobAsync(permanent.Id);
                var retried = (await repo.GetJobsByStatusAsync(UploadStatus.Pending))
                    .Single(j => j.Id == permanent.Id);
                Assert.That(retried.LastFailureReason, Is.EqualTo("permanent reason"),
                    "Manual retry must preserve LastFailureReason as history");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
