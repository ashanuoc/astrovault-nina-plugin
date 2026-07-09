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
    [TestFixture]
    [Category("Resilience")]
    public class UploadManagerResilienceTests
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
            mockQueueRepo.Setup(r => r.RemoveJobAsync(It.IsAny<Guid>()))
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.EnqueueAsync(It.IsAny<UploadJob>()))
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.ResetRetryCountsForTransientAsync())
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.ScheduleRetryAsync(
                    It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.MarkFailedAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>()))
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.PromoteTransientFailedToPendingAsync())
                .ReturnsAsync(0);
            mockQueueRepo.Setup(r => r.GetTotalPendingSizeAsync()).ReturnsAsync(0L);

            sut = new UploadManager(
                mockQueueRepo.Object,
                mockApiClient.Object,
                mockAuthManager.Object);
        }

        [Test]
        public void CalculateBackoffDelay_ReturnsWithinBounds()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                var maxExpected = Math.Min(60.0, 2.0 * Math.Pow(2, attempt));

                for (int i = 0; i < 100; i++)
                {
                    var delay = UploadManager.CalculateBackoffDelay(attempt);
                    Assert.That(delay, Is.GreaterThanOrEqualTo(0.0),
                        $"Delay for attempt {attempt} iteration {i} should be >= 0");
                    Assert.That(delay, Is.LessThanOrEqualTo(maxExpected),
                        $"Delay for attempt {attempt} iteration {i} should be <= {maxExpected}");
                }
            }
        }

        [Test]
        public void CalculateBackoffDelay_ProducesVariation()
        {
            var delays = new double[100];
            for (int i = 0; i < 100; i++)
            {
                delays[i] = UploadManager.CalculateBackoffDelay(2); // attempt 2: max 8s
            }

            // Not all values should be identical (jitter is random)
            var allSame = true;
            for (int i = 1; i < delays.Length; i++)
            {
                if (Math.Abs(delays[i] - delays[0]) > 0.0001)
                {
                    allSame = false;
                    break;
                }
            }

            Assert.That(allSame, Is.False, "Jitter should produce variation in delay values");
        }

        [Test]
        public void CalculateBackoffDelay_NeverBelowOneSecondFloor()
        {
            // REL-07: even at minimum jitter, a fast-failing job cannot burn its budget in under a second.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                for (int i = 0; i < 200; i++)
                {
                    var delay = UploadManager.CalculateBackoffDelay(attempt);
                    Assert.That(delay, Is.GreaterThanOrEqualTo(1.0),
                        $"Backoff for attempt {attempt} iteration {i} must respect the ~1s floor");
                }
            }
        }

        [Test]
        public async Task HandleUploadFailure_TransientRetry_UsesNonBlockingScheduleRetry()
        {
            // REL-07: a transient (non-exhausting) failure must schedule a NextRetryAfter-based retry
            // (non-blocking), NOT block on Task.Delay and NOT mark the job Failed.
            var job = TestDataFactory.CreateUploadJob(retryCount: 0);
            mockQueueRepo.Setup(r => r.IncrementRetryCountAsync(job.Id))
                .Callback(() => job.RetryCount++)
                .Returns(Task.CompletedTask);

            await sut.HandleUploadFailure(job, UploadResult.Fail("Network timeout"), CancellationToken.None);

            mockQueueRepo.Verify(
                r => r.ScheduleRetryAsync(job.Id, It.IsAny<DateTime>(), "Network timeout"),
                Times.Once,
                "Transient retry must schedule a non-blocking NextRetryAfter retry");
            mockQueueRepo.Verify(
                r => r.MarkFailedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>()),
                Times.Never,
                "A non-exhausting transient failure must not mark the job Failed");
        }

        [Test]
        public async Task HandleUploadSuccess_AfterOutage_UnpenalizesJobsByResettingRetryBudget()
        {
            // REL-06: failures accrued during an outage must not permanently penalize jobs. Open the
            // breaker with 5 transient failures, then a recovery success resets the transient retry
            // budget so jobs that exhausted their budget under the outage get a clean slate (this, plus
            // the half-open transient-promotion seam, is what un-burns the per-job budget).
            for (int i = 0; i < 5; i++)
            {
                var seed = TestDataFactory.CreateUploadJob(retryCount: 0);
                mockQueueRepo.Setup(r => r.IncrementRetryCountAsync(seed.Id))
                    .Callback(() => seed.RetryCount++)
                    .Returns(Task.CompletedTask);
                await sut.HandleUploadFailure(seed, UploadResult.Fail("Network timeout"), CancellationToken.None);
            }
            Assert.That(sut.IsCircuitOpen, Is.True);

            // Recovery probe succeeds -> breaker closes AND the transient retry budget is reset.
            var recoveryJob = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadSuccess(recoveryJob);

            Assert.That(sut.IsCircuitOpen, Is.False);
            mockQueueRepo.Verify(
                r => r.ResetRetryCountsForTransientAsync(),
                Times.Once,
                "Outage recovery must reset the transient retry budget so outage failures do not permanently penalize jobs");
        }

        [Test]
        public async Task HandleUploadFailure_TransientFiveConsecutive_OpensCircuit()
        {
            for (int i = 0; i < 5; i++)
            {
                var job = TestDataFactory.CreateUploadJob(retryCount: 0);
                var result = UploadResult.Fail("Network timeout");
                await sut.HandleUploadFailure(job, result, CancellationToken.None);
            }

            Assert.That(sut.IsCircuitOpen, Is.True);
        }

        [Test]
        public async Task HandleUploadFailure_PermanentFailure_DoesNotCountTowardCircuit()
        {
            for (int i = 0; i < 5; i++)
            {
                var job = TestDataFactory.CreateUploadJob(retryCount: 0);
                var result = UploadResult.FailPermanent("File not found");
                await sut.HandleUploadFailure(job, result, CancellationToken.None);
            }

            Assert.That(sut.IsCircuitOpen, Is.False);
        }

        [Test]
        public async Task HandleUploadSuccess_AfterCircuitOpen_ClosesCircuitAndResetsRetries()
        {
            // Open circuit via 5 transient failures
            for (int i = 0; i < 5; i++)
            {
                var failJob = TestDataFactory.CreateUploadJob(retryCount: 0);
                var failResult = UploadResult.Fail("Network timeout");
                await sut.HandleUploadFailure(failJob, failResult, CancellationToken.None);
            }
            Assert.That(sut.IsCircuitOpen, Is.True);

            // Simulate a success
            var successJob = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadSuccess(successJob);

            Assert.That(sut.IsCircuitOpen, Is.False);
            mockQueueRepo.Verify(r => r.ResetRetryCountsForTransientAsync(), Times.Once);
        }

        [Test]
        public async Task HandleUploadFailure_ExhaustsRetries_MarksJobFailed()
        {
            // Job at retry count 4 -- next failure makes retryCount 5 which equals MaxRetries.
            // Simulate the production repo aliasing: IncrementRetryCountAsync mutates the
            // same job object in place (REL-01), so after increment RetryCount == 5.
            var job = TestDataFactory.CreateUploadJob(retryCount: 4);
            var result = UploadResult.Fail("Server error");
            mockQueueRepo.Setup(r => r.IncrementRetryCountAsync(job.Id))
                .Callback(() => job.RetryCount++)
                .Returns(Task.CompletedTask);

            await sut.HandleUploadFailure(job, result, CancellationToken.None);

            // REL-06: terminal TRANSIENT failure (budget exhausted) now goes through MarkFailedAsync with
            // isPermanent: false so it is auto-promotable on a half-open probe (the TC-16 fix), and D-12
            // persists the reason as LastFailureReason.
            mockQueueRepo.Verify(
                r => r.MarkFailedAsync(job.Id, result.ErrorMessage, false),
                Times.Once);
        }

        // ====================================================================
        // REL-04: Clean cancellation/shutdown must NOT be treated as a transient
        // failure -- it must not increment the retry count nor nudge the breaker.
        // ====================================================================

        [Test]
        public async Task UploadJobAsync_Cancellation_DoesNotIncrementRetryCount()
        {
            // Arrange: a cancelled token + an API client that observes cancellation.
            using var ctsSource = new CancellationTokenSource();
            ctsSource.Cancel();
            var token = ctsSource.Token;

            var job = TestDataFactory.CreateUploadJob(retryCount: 0);
            mockApiClient
                .Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException(token));
            mockQueueRepo.Setup(r => r.GetTotalPendingSizeAsync()).ReturnsAsync(0L);

            // Act: the cancellation propagates so the processing loop can break cleanly.
            Assert.ThrowsAsync<OperationCanceledException>(
                async () => await sut.UploadJobAsync(job, token));

            // Assert: cancellation is NOT a transient failure.
            mockQueueRepo.Verify(
                r => r.IncrementRetryCountAsync(It.IsAny<Guid>()),
                Times.Never,
                "Clean cancellation must not increment the retry count");
            mockQueueRepo.Verify(
                r => r.UpdateJobStatusAsync(job.Id, UploadStatus.Failed, It.IsAny<string>()),
                Times.Never,
                "Clean cancellation must not mark the job Failed");
            await Task.CompletedTask;
        }

        [Test]
        public async Task UploadJobAsync_Cancellation_DoesNotNudgeBreakerTowardOpen()
        {
            // Arrange: five clean cancellations. If they were counted as transient
            // failures the breaker would Open (threshold is 5).
            using var ctsSource = new CancellationTokenSource();
            ctsSource.Cancel();
            var token = ctsSource.Token;

            mockApiClient
                .Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException(token));
            mockQueueRepo.Setup(r => r.GetTotalPendingSizeAsync()).ReturnsAsync(0L);

            // Act
            for (int i = 0; i < 5; i++)
            {
                var job = TestDataFactory.CreateUploadJob(retryCount: 0);
                Assert.ThrowsAsync<OperationCanceledException>(
                    async () => await sut.UploadJobAsync(job, token));
            }

            // Assert: breaker stayed closed -- cancellation did not nudge it toward Open.
            Assert.That(sut.IsCircuitOpen, Is.False,
                "Clean cancellations must not push the circuit breaker toward Open");
            await Task.CompletedTask;
        }

        [Test]
        public async Task EnqueueJobAsync_WhileCircuitOpen_StillEnqueues()
        {
            // Open circuit via 5 transient failures
            for (int i = 0; i < 5; i++)
            {
                var failJob = TestDataFactory.CreateUploadJob(retryCount: 0);
                var failResult = UploadResult.Fail("Network timeout");
                await sut.HandleUploadFailure(failJob, failResult, CancellationToken.None);
            }
            Assert.That(sut.IsCircuitOpen, Is.True);

            // Enqueue should still work while circuit is open (SAFE-04)
            var newJob = TestDataFactory.CreateUploadJob();
            await sut.EnqueueJobAsync(newJob);

            mockQueueRepo.Verify(r => r.EnqueueAsync(newJob), Times.Once);
        }
    }
}
