using System;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Core;
using Astrovault.Interfaces;
using Astrovault.Models;
using CloudUploadPlugin.Tests.Helpers;
using Moq;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Manager-level tests for queue hardening behaviors.
    /// Uses Moq for IUploadQueueRepository to test capacity warning,
    /// completed retention, and retry/dismiss delegation.
    /// </summary>
    [TestFixture]
    [Category("QueueHardening")]
    public class UploadManagerQueueTests
    {
        private Mock<IUploadQueueRepository> mockQueueRepo = null!;
        private Mock<ICloudApiClient> mockApiClient = null!;
        private Mock<IAuthManager> mockAuthManager = null!;
        private UploadManager sut = null!;

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
            mockQueueRepo.Setup(r => r.RemoveJobAsync(It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.EnqueueAsync(It.IsAny<UploadJob>()))
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.ResetRetryCountsForTransientAsync())
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.RetryJobAsync(It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.RemoveFailedJobsAsync())
                .Returns(Task.CompletedTask);

            // Default: auth returns test key
            mockAuthManager.Setup(a => a.GetApiKey()).Returns("test-key");

            sut = new UploadManager(
                mockQueueRepo.Object,
                mockApiClient.Object,
                mockAuthManager.Object);
        }

        [Test]
        public async Task CapacityWarning_TrueAbove10GB()
        {
            // 11 GB in bytes
            long elevenGb = 11L * 1024 * 1024 * 1024;
            mockQueueRepo.Setup(r => r.GetTotalPendingSizeAsync())
                .ReturnsAsync(elevenGb);

            var job = TestDataFactory.CreateUploadJob();
            await sut.EnqueueJobAsync(job);

            Assert.That(sut.IsCapacityWarning, Is.True,
                "IsCapacityWarning should be true when total pending size exceeds 10GB");
        }

        [Test]
        public async Task CapacityWarning_FalseBelow10GB()
        {
            // 5 GB in bytes
            long fiveGb = 5L * 1024 * 1024 * 1024;
            mockQueueRepo.Setup(r => r.GetTotalPendingSizeAsync())
                .ReturnsAsync(fiveGb);

            var job = TestDataFactory.CreateUploadJob();
            await sut.EnqueueJobAsync(job);

            Assert.That(sut.IsCapacityWarning, Is.False,
                "IsCapacityWarning should be false when total pending size is below 10GB");
        }

        [Test]
        public async Task CapacityNeverBlocks_Enqueue()
        {
            // 20 GB -- way above threshold
            long twentyGb = 20L * 1024 * 1024 * 1024;
            mockQueueRepo.Setup(r => r.GetTotalPendingSizeAsync())
                .ReturnsAsync(twentyGb);

            var job = TestDataFactory.CreateUploadJob();

            // Should NOT throw even when capacity exceeds threshold
            Assert.DoesNotThrowAsync(async () => await sut.EnqueueJobAsync(job));

            // Verify EnqueueAsync was actually called
            mockQueueRepo.Verify(r => r.EnqueueAsync(job), Times.Once);
        }

        [Test]
        public async Task HandleUploadSuccess_NoRemoveCall()
        {
            // Setup: capacity check returns small value
            mockQueueRepo.Setup(r => r.GetTotalPendingSizeAsync())
                .ReturnsAsync(0L);

            var job = TestDataFactory.CreateUploadJob();

            await sut.HandleUploadSuccess(job);

            // UpdateJobStatusAsync(Completed) should be called
            mockQueueRepo.Verify(
                r => r.UpdateJobStatusAsync(job.Id, UploadStatus.Completed, It.IsAny<string>()),
                Times.Once,
                "HandleUploadSuccess should call UpdateJobStatusAsync with Completed");

            // RemoveJobAsync should NOT be called (completed jobs are retained)
            mockQueueRepo.Verify(
                r => r.RemoveJobAsync(job.Id),
                Times.Never,
                "HandleUploadSuccess should NOT call RemoveJobAsync (retain completed jobs)");
        }

        [Test]
        public async Task RetryDelegation_CallsRepository()
        {
            var jobId = Guid.NewGuid();

            await sut.RetryFailedJobAsync(jobId);

            mockQueueRepo.Verify(
                r => r.RetryJobAsync(jobId),
                Times.Once,
                "RetryFailedJobAsync should delegate to repository.RetryJobAsync");
        }

        [Test]
        public async Task DismissDelegation_CallsRepository()
        {
            await sut.DismissAllFailedAsync();

            mockQueueRepo.Verify(
                r => r.RemoveFailedJobsAsync(),
                Times.Once,
                "DismissAllFailedAsync should delegate to repository.RemoveFailedJobsAsync");
        }
    }
}
