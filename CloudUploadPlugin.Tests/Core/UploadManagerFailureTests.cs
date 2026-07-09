using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Core;
using Astrovault.Data;
using Astrovault.Interfaces;
using Astrovault.Models;
using CloudUploadPlugin.Tests.Helpers;
using Moq;

namespace CloudUploadPlugin.Tests.Core
{
    [TestFixture]
    [Category("FilePreparation")]
    public class UploadManagerFailureTests
    {
        private Mock<IUploadQueueRepository> mockQueueRepo;
        private Mock<ICloudApiClient> mockApiClient;
        private Mock<IAuthManager> mockAuthManager;
        private UploadManager sut;

        [SetUp]
        public void Setup()
        {
            mockQueueRepo = new Mock<IUploadQueueRepository>();
            mockApiClient = new Mock<ICloudApiClient>();
            mockAuthManager = new Mock<IAuthManager>();

            // Default: repository operations complete successfully
            mockQueueRepo.Setup(r => r.UpdateJobStatusAsync(
                    It.IsAny<Guid>(), It.IsAny<UploadStatus>(), It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.IncrementRetryCountAsync(It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.ScheduleRetryAsync(
                    It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.MarkFailedAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>()))
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.GetTotalPendingSizeAsync()).ReturnsAsync(0L);

            sut = new UploadManager(
                mockQueueRepo.Object,
                mockApiClient.Object,
                mockAuthManager.Object);
        }

        [Test]
        public async Task HandleUploadFailure_PermanentFailure_SkipsRetries()
        {
            // Arrange
            var job = TestDataFactory.CreateUploadJob();
            var result = UploadResult.FailPermanent("File not found: D:\\Astro\\M31\\Light\\001.fits");

            // Act
            await sut.HandleUploadFailure(job, result, CancellationToken.None);

            // Assert -- permanent failure goes straight to Failed (marked permanent so it is never
            // auto-promoted on a half-open probe -- REL-06), no retry increment.
            mockQueueRepo.Verify(
                r => r.MarkFailedAsync(job.Id, result.ErrorMessage, true),
                Times.Once);
            mockQueueRepo.Verify(
                r => r.IncrementRetryCountAsync(It.IsAny<Guid>()),
                Times.Never);
        }

        [Test]
        public async Task HandleUploadFailure_TransientFailure_RetriesNormally()
        {
            // Arrange -- job at retry count 0; after one increment the post-increment
            // value is 1 (< MaxRetries=5), so the job must go back to Pending, not Failed.
            var job = TestDataFactory.CreateUploadJob(retryCount: 0);
            var result = UploadResult.Fail("Network timeout");

            // Simulate the production repo aliasing: IncrementRetryCountAsync mutates the
            // same job object Peek returned, so HandleUploadFailure must read the
            // post-increment value (1), not job.RetryCount + 1 (which would double-count).
            mockQueueRepo.Setup(r => r.IncrementRetryCountAsync(job.Id))
                .Callback(() => job.RetryCount++)
                .Returns(Task.CompletedTask);

            // Act
            await sut.HandleUploadFailure(job, result, CancellationToken.None);

            // Assert -- post-increment retry count is 1 (< 5), so the job is re-queued via the
            // non-blocking NextRetryAfter backoff (ScheduleRetryAsync, REL-07), NOT marked Failed.
            mockQueueRepo.Verify(
                r => r.ScheduleRetryAsync(job.Id, It.IsAny<DateTime>(), result.ErrorMessage),
                Times.Once);
            mockQueueRepo.Verify(
                r => r.MarkFailedAsync(job.Id, It.IsAny<string>(), It.IsAny<bool>()),
                Times.Never,
                "A job at retry count 1/5 must NOT be marked Failed");
        }

        // ====================================================================
        // REL-01 RetryBudget regression: real repository (DPAPI temp dir), NOT a
        // mock. Reproduces the production aliasing (PeekAsync returns the same job
        // object that IncrementRetryCountAsync mutates) so the off-by-one is
        // observable. A transient job must fail after EXACTLY 5 attempts, not 4.
        // ====================================================================

        [Test]
        [Category("RetryBudget")]
        public async Task RetryBudget_TransientFailures_FailsAfterExactlyFiveAttempts()
        {
            // Arrange -- a real repository backed by a temp directory so the same
            // job reference flows through Peek/Increment/UpdateStatus as in production.
            var tempDir = Path.Combine(Path.GetTempPath(), "AstrovaultRetryBudget_" + Guid.NewGuid().ToString("N"));
            try
            {
                var repo = new UploadQueueRepository(tempDir);
                var apiClient = new Mock<ICloudApiClient>();
                var authManager = new Mock<IAuthManager>();
                var manager = new UploadManager(repo, apiClient.Object, authManager.Object);

                var job = TestDataFactory.CreateUploadJob(retryCount: 0);
                await repo.EnqueueAsync(job);

                var result = UploadResult.Fail("Network timeout");

                // Act -- drive the job through transient failures. Each call fetches the live Pending
                // job (mutated in place by IncrementRetryCountAsync) just like the production loop does,
                // then fails it again. REL-07 note: the non-blocking backoff now stamps NextRetryAfter on
                // each transient retry, so we fetch via GetJobsByStatusAsync (not PeekAsync, which would
                // skip the not-yet-due job) and clear NextRetryAfter to simulate the backoff elapsing --
                // we are measuring the per-job retry BUDGET here, not the backoff timing.
                int attempts = 0;
                while (true)
                {
                    var current = (await repo.GetJobsByStatusAsync(UploadStatus.Pending))
                        .FirstOrDefault();
                    if (current == null)
                    {
                        break; // job left the Pending set -> it was marked Failed
                    }

                    current.NextRetryAfter = null; // simulate the backoff window having elapsed
                    attempts++;
                    // The per-job retry budget is not gated by the breaker here.
                    await manager.HandleUploadFailure(current, result, CancellationToken.None);

                    if (attempts > 10)
                    {
                        Assert.Fail("Retry budget never exhausted -- possible infinite retry loop");
                    }
                }

                // Assert -- the job must survive exactly 5 transient attempts before Failed.
                Assert.That(attempts, Is.EqualTo(5),
                    "A transient job must fail after EXACTLY 5 attempts (MaxRetries), not 4 (off-by-one)");

                var failed = await repo.GetJobsByStatusAsync(UploadStatus.Failed);
                Assert.That(failed, Is.Not.Empty,
                    "The job should be in the Failed set after exhausting its retry budget");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
