using System;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    /// <summary>
    /// Manager-level tests for the blocked-state machine (storage-quota plan §4 Step 2). Covers the
    /// plan's step-7 test table rows 1-3, 9, 11, plus the 401/403 case and the persist-failure warning
    /// contract (point 6). Deterministic: Moq for <see cref="ICloudApiClient"/>/<see cref="IUploadQueueRepository"/>/
    /// <see cref="IAuthManager"/>, and a REAL <see cref="UploadQueueRepository"/> (temp dir) wherever the
    /// scenario needs the actual atomic-commit/PeekAsync/identity-fingerprint contract, matching the
    /// existing RetryBudget-style tests in <c>UploadManagerFailureTests</c>.
    /// </summary>
    [TestFixture]
    [Category("StorageQuota")]
    public class UploadManagerBlockTests
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

            mockQueueRepo.Setup(r => r.UpdateJobStatusAsync(It.IsAny<Guid>(), It.IsAny<UploadStatus>(), It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.IncrementRetryCountAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.ScheduleRetryAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.MarkFailedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>()))
                .Returns(Task.CompletedTask);
            mockQueueRepo.Setup(r => r.GetTotalPendingSizeAsync()).ReturnsAsync(0L);
            mockQueueRepo.Setup(r => r.GetJobCountAsync(It.IsAny<UploadStatus>())).ReturnsAsync(0);
            mockQueueRepo.Setup(r => r.SetBlockAsync(
                    It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(), It.IsAny<UploadBlockIdentityInput>()))
                .ReturnsAsync(true);

            mockApiClient.Setup(c => c.BaseUrl).Returns("https://api.astrovault.io");
            mockAuthManager.Setup(a => a.GetApiKey()).Returns("test-api-key");

            sut = new UploadManager(mockQueueRepo.Object, mockApiClient.Object, mockAuthManager.Object);
        }

        private static UploadResult QuotaFail(long? usedBytes = 16_000_000_000, long? limitBytes = 10_000_000_000)
        {
            return UploadResult.Fail("Insufficient storage", 507, FailureKind.PermanentQuota,
                new QuotaInfo { UsedBytes = usedBytes, LimitBytes = limitBytes });
        }

        private static UploadResult AuthFail(int statusCode = 401)
        {
            return UploadResult.Fail("Invalid API key", statusCode, FailureKind.PermanentAuth);
        }

        // ====================================================================
        // Entering a block (HandleUploadFailure branch)
        // ====================================================================

        [Test]
        public async Task HandleUploadFailure_Quota507_LatchesQuotaExceeded_ReturnsJobToPending_NoRetryScheduled()
        {
            var job = TestDataFactory.CreateUploadJob();

            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded));
            Assert.That(sut.BlockSnapshot, Is.Not.Null);
            Assert.That(sut.BlockSnapshot!.UsedBytes, Is.EqualTo(16_000_000_000));
            Assert.That(sut.BlockSnapshot!.LimitBytes, Is.EqualTo(10_000_000_000));

            mockQueueRepo.Verify(r => r.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id, It.IsAny<UploadBlockSnapshot>(), It.IsAny<UploadBlockIdentityInput>()), Times.Once);
            mockQueueRepo.Verify(r => r.MarkFailedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never,
                "A blocked job must never be marked Failed");
            mockQueueRepo.Verify(r => r.ScheduleRetryAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<string>()), Times.Never,
                "No retry is scheduled for a blocked result");
            mockQueueRepo.Verify(r => r.IncrementRetryCountAsync(It.IsAny<Guid>()), Times.Never,
                "A blocked result must not consume the job's retry budget");
        }

        [Test]
        public async Task HandleUploadFailure_Auth401_LatchesAuthInvalid_NoQuotaSnapshot()
        {
            var job = TestDataFactory.CreateUploadJob();

            await sut.HandleUploadFailure(job, AuthFail(401), CancellationToken.None);

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.AuthInvalid));
            Assert.That(sut.BlockSnapshot!.UsedBytes, Is.Null, "AuthInvalid carries no quota numbers");
            Assert.That(sut.BlockSnapshot!.LimitBytes, Is.Null);
            mockQueueRepo.Verify(r => r.SetBlockAsync(UploadBlockKind.AuthInvalid, job.Id, It.IsAny<UploadBlockSnapshot>(), It.IsAny<UploadBlockIdentityInput>()), Times.Once);
        }

        [Test]
        public async Task HandleUploadFailure_Auth403_AlsoLatchesAuthInvalid()
        {
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, AuthFail(403), CancellationToken.None);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.AuthInvalid));
        }

        [Test]
        public async Task HandleUploadFailure_QuotaOrAuth_DoesNotFeedCircuitBreaker()
        {
            for (int i = 0; i < 5; i++)
            {
                var job = TestDataFactory.CreateUploadJob();
                await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);
            }

            Assert.That(sut.IsCircuitOpen, Is.False,
                "Five quota blocks must never open the breaker -- blocked results do not feed it");
        }

        [Test]
        public async Task HandleUploadFailure_QuotaBlock_DurableCommitSucceeds_RaisesQueueStateChanged()
        {
            var raised = false;
            sut.QueueStateChanged += (s, e) => raised = true;

            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), QuotaFail(), CancellationToken.None);

            Assert.That(raised, Is.True, "A successfully-persisted block transition must notify the UI");
            Assert.That(sut.HasBlockPersistFailureWarning, Is.False);
        }

        [Test]
        public async Task HandleUploadFailure_QuotaBlock_DurableCommitFails_StaysLatchedInMemory_WarnsAndRaisesEvent()
        {
            // Storage-quota plan directive H (B#2): a failed commit must never claim a durability that
            // did not happen, but it must NOT be invisible either -- the manager stays latched (fail-safe
            // stopped), surfaces the distinct warning flag, AND still raises QueueStateChanged (through
            // the same dispatcher path a successful transition uses) so the UI actually refreshes
            // (IsUploading, the warning banner) instead of staying stuck until an unrelated later event.
            mockQueueRepo.Setup(r => r.SetBlockAsync(
                    It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(), It.IsAny<UploadBlockIdentityInput>()))
                .ReturnsAsync(false);

            var raised = false;
            sut.QueueStateChanged += (s, e) => raised = true;

            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), QuotaFail(), CancellationToken.None);

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "Fail-safe: the manager must stay latched in memory even when the disk commit fails");
            Assert.That(sut.HasBlockPersistFailureWarning, Is.True);
            Assert.That(raised, Is.True, "A failed commit is still a live transition -- the UI must be notified");
        }

        [Test]
        public async Task HandleUploadFailure_QuotaOrAuth_ResetsCircuitBreakerBeforeLatching()
        {
            // Open the breaker with 4 transient failures (still Closed at 4).
            for (int i = 0; i < 4; i++)
            {
                await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), UploadResult.Fail("timeout"), CancellationToken.None);
            }
            Assert.That(sut.IsCircuitOpen, Is.False, "Precondition: still closed at 4 consecutive failures");

            // A 507 arrives -- the response proves connectivity, so it must reset the failure count to 0
            // (not just "still closed at 4") BEFORE latching.
            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), QuotaFail(), CancellationToken.None);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded));

            // One MORE transient failure: if the count had NOT been reset (still sitting at 4), this
            // single failure would push it to 5 and open the breaker. HandleUploadFailure is called
            // directly here (bypassing the manager-level block gate that lives in ProcessNextJobAsync)
            // specifically to probe the breaker's internal counter in isolation.
            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), UploadResult.Fail("timeout"), CancellationToken.None);
            Assert.That(sut.IsCircuitOpen, Is.False,
                "The connectivity proof must have reset the consecutive-failure count to 0, not left it at 4");
        }

        // ====================================================================
        // Test 3 (regression): 500/503 must NOT become terminal/blocking.
        // ====================================================================

        [TestCase(500)]
        [TestCase(503)]
        [TestCase(502)]
        public async Task HandleUploadFailure_ServerErrors_StayTransient_KeepBackoffAndBreakerFeeding(int statusCode)
        {
            var job = TestDataFactory.CreateUploadJob(retryCount: 0);
            mockQueueRepo.Setup(r => r.IncrementRetryCountAsync(job.Id))
                .Callback(() => job.RetryCount++)
                .Returns(Task.CompletedTask);

            var result = UploadResult.Fail("Server error", statusCode); // default FailureKind.Transient

            await sut.HandleUploadFailure(job, result, CancellationToken.None);

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.None),
                $"HTTP {statusCode} must never latch the queue -- it stays transient");
            mockQueueRepo.Verify(r => r.ScheduleRetryAsync(job.Id, It.IsAny<DateTime>(), result.ErrorMessage), Times.Once,
                "A transient failure must still schedule non-blocking backoff");
            mockQueueRepo.Verify(r => r.SetBlockAsync(It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(), It.IsAny<UploadBlockIdentityInput>()), Times.Never);
        }

        [Test]
        public async Task HandleUploadFailure_FiveConsecutiveServerErrors_StillOpensBreaker_Regression()
        {
            // Regression guard: making quota/auth terminal must not accidentally short-circuit the
            // EXISTING transient breaker-feeding path for ordinary 5xx failures.
            for (int i = 0; i < 5; i++)
            {
                await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(retryCount: 0), UploadResult.Fail("Server error", 500), CancellationToken.None);
            }

            Assert.That(sut.IsCircuitOpen, Is.True);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.None));
        }

        // ====================================================================
        // Row 16: 400/413 (PermanentSkipFile) -- that file only is marked failed; the queue
        // continues to the NEXT job; no retry; no block latched. Real repository (temp dir) with
        // TWO jobs so "queue continues" is an observable fact, not just an absence of a block flag.
        // ====================================================================

        [TestCase(400)]
        [TestCase(413)]
        public async Task ProcessNextJobAsync_SkipFileStatus_MarksOnlyThatJobFailed_QueueContinues_NoBlockLatched(int statusCode)
        {
            var tempDir = MakeTempDir();
            try
            {
                var repo = new UploadQueueRepository(tempDir);
                var apiClient = new Mock<ICloudApiClient>();
                apiClient.Setup(c => c.BaseUrl).Returns("https://api.astrovault.io");
                var authManager = new Mock<IAuthManager>();
                authManager.Setup(a => a.GetApiKey()).Returns("test-api-key");
                var manager = new UploadManager(repo, apiClient.Object, authManager.Object);

                var badJob = TestDataFactory.CreateUploadJob(Path.Combine(tempDir, "bad.fits"));
                var goodJob = TestDataFactory.CreateUploadJob(Path.Combine(tempDir, "good.fits"));
                File.WriteAllText(badJob.LocalPath, "x");
                File.WriteAllText(goodJob.LocalPath, "x");
                await repo.EnqueueAsync(badJob);
                await repo.EnqueueAsync(goodJob);

                // Keyed by job id (not call order) -- the queue's default drain order is
                // newest-first, so which of the two is dequeued first is not this test's concern.
                apiClient.Setup(c => c.UploadFileAsync(
                        It.Is<UploadJob>(j => j.Id == badJob.Id), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(UploadResult.Fail($"Chunk failed: HTTP {statusCode}", statusCode, FailureKind.PermanentSkipFile));
                apiClient.Setup(c => c.UploadFileAsync(
                        It.Is<UploadJob>(j => j.Id == goodJob.Id), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(UploadResult.Ok("https://cdn.astrovault.io/good.fits"));

                var firstProcessed = await manager.ProcessNextJobAsync(CancellationToken.None);
                Assert.That(firstProcessed, Is.True);
                Assert.That(manager.BlockKind, Is.EqualTo(UploadBlockKind.None),
                    $"A {statusCode} skip-file status must never latch the queue");

                var secondProcessed = await manager.ProcessNextJobAsync(CancellationToken.None);
                Assert.That(secondProcessed, Is.True,
                    "The queue must continue to the NEXT job -- a skip-file failure must not stop it");
                Assert.That(manager.BlockKind, Is.EqualTo(UploadBlockKind.None));

                var failed = (await repo.GetJobsByStatusAsync(UploadStatus.Failed)).ToList();
                Assert.That(failed.Select(j => j.Id), Does.Contain(badJob.Id));
                Assert.That(failed, Has.Count.EqualTo(1), "Only the bad file is marked failed");

                var completed = (await repo.GetJobsByStatusAsync(UploadStatus.Completed)).ToList();
                Assert.That(completed.Select(j => j.Id), Does.Contain(goodJob.Id),
                    "The OTHER job must still succeed -- the queue was never blocked by the skip-file failure");

                apiClient.Verify(c => c.UploadFileAsync(
                        It.Is<UploadJob>(j => j.Id == badJob.Id), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()),
                    Times.Once, "No retry for a skip-file failure");
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        // ====================================================================
        // Row 1/2 + 401/403: full drain-loop behavior via ProcessNextJobAsync (real repository so
        // PeekAsync/job-state are authoritative, not mocked).
        // ====================================================================

        [Test]
        public async Task ProcessNextJobAsync_MidSession507_StopsQueue_JobsStayPending_ZeroFurtherAttempts_SourceFilesUntouched()
        {
            var tempDir = MakeTempDir();
            try
            {
                var repo = new UploadQueueRepository(tempDir);
                var apiClient = new Mock<ICloudApiClient>();
                apiClient.Setup(c => c.BaseUrl).Returns("https://api.astrovault.io");
                var authManager = new Mock<IAuthManager>();
                authManager.Setup(a => a.GetApiKey()).Returns("test-api-key");
                var manager = new UploadManager(repo, apiClient.Object, authManager.Object);

                // Three source files on disk -- proving none of them get deleted by the block path.
                var sourcePaths = Enumerable.Range(0, 3)
                    .Select(i => Path.Combine(tempDir, $"frame-{i}.fits"))
                    .ToArray();
                foreach (var p in sourcePaths) File.WriteAllText(p, "fits-bytes");

                var jobs = sourcePaths.Select(p => TestDataFactory.CreateUploadJob(p)).ToArray();
                foreach (var j in jobs) await repo.EnqueueAsync(j);

                // First attempt (whichever job PeekAsync selects) 507s; every OTHER call must never happen.
                apiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(QuotaFail());

                // Act: drive the drain loop directly (deterministic, no background Task.Delay polling).
                var firstProcessed = await manager.ProcessNextJobAsync(CancellationToken.None);
                var secondProcessed = await manager.ProcessNextJobAsync(CancellationToken.None);
                var thirdProcessed = await manager.ProcessNextJobAsync(CancellationToken.None);

                Assert.That(firstProcessed, Is.True, "The first tick attempts the upload and latches");
                Assert.That(secondProcessed, Is.False, "While latched, ProcessNextJobAsync must report no work done");
                Assert.That(thirdProcessed, Is.False);

                apiClient.Verify(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once,
                    "Exactly ONE upload attempt total -- zero further HTTP requests while latched");

                var pending = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
                Assert.That(pending, Has.Count.EqualTo(3), "ALL three jobs must remain Pending -- none discarded, none Failed");

                var failed = (await repo.GetJobsByStatusAsync(UploadStatus.Failed)).ToList();
                Assert.That(failed, Is.Empty);

                Assert.That(manager.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded));

                foreach (var p in sourcePaths)
                {
                    Assert.That(File.Exists(p), Is.True, $"Source file {p} must be untouched on disk");
                    Assert.That(File.ReadAllText(p), Is.EqualTo("fits-bytes"));
                }
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        [Test]
        public async Task ProcessNextJobAsync_FirstUpload507_LatchesAfterExactlyOneAttempt()
        {
            var job = TestDataFactory.CreateUploadJob();
            mockQueueRepo.Setup(r => r.PeekAsync()).ReturnsAsync(job);

            mockApiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(QuotaFail());

            var processed = await sut.ProcessNextJobAsync(CancellationToken.None);

            Assert.That(processed, Is.True);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded));
            mockApiClient.Verify(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once);

            // A second tick must not touch PeekAsync/upload at all.
            mockQueueRepo.Invocations.Clear();
            mockApiClient.Invocations.Clear();
            var secondProcessed = await sut.ProcessNextJobAsync(CancellationToken.None);
            Assert.That(secondProcessed, Is.False);
            mockQueueRepo.Verify(r => r.PeekAsync(), Times.Never, "Latched: zero dequeuing");
            mockApiClient.Verify(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task ProcessNextJobAsync_401_LatchesAuthInvalid_JobKeptPending_ZeroFurtherAttempts()
        {
            var tempDir = MakeTempDir();
            try
            {
                var repo = new UploadQueueRepository(tempDir);
                var apiClient = new Mock<ICloudApiClient>();
                apiClient.Setup(c => c.BaseUrl).Returns("https://api.astrovault.io");
                var authManager = new Mock<IAuthManager>();
                authManager.Setup(a => a.GetApiKey()).Returns("test-api-key");
                var manager = new UploadManager(repo, apiClient.Object, authManager.Object);

                var job = TestDataFactory.CreateUploadJob(Path.Combine(tempDir, "a.fits"));
                await repo.EnqueueAsync(job);

                apiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(AuthFail(401));

                await manager.ProcessNextJobAsync(CancellationToken.None);
                var second = await manager.ProcessNextJobAsync(CancellationToken.None);

                Assert.That(second, Is.False);
                Assert.That(manager.BlockKind, Is.EqualTo(UploadBlockKind.AuthInvalid));
                var pending = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
                Assert.That(pending, Has.Count.EqualTo(1), "The job must be kept Pending, not Failed");
                apiClient.Verify(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once);
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        // ====================================================================
        // Test 9: circuit-breaker matrix.
        // ====================================================================

        [Test]
        public async Task Breaker_Open_NoAttemptOccursAtAll()
        {
            // Open the breaker via 5 consecutive transient failures.
            for (int i = 0; i < 5; i++)
            {
                await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), UploadResult.Fail("timeout"), CancellationToken.None);
            }
            Assert.That(sut.IsCircuitOpen, Is.True);

            var job = TestDataFactory.CreateUploadJob();
            mockQueueRepo.Setup(r => r.PeekAsync()).ReturnsAsync(job);

            var processed = await sut.ProcessNextJobAsync(CancellationToken.None);

            Assert.That(processed, Is.False, "Open breaker: no work should be reported");
            mockApiClient.Verify(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Never,
                "Open breaker gates BEFORE dequeue -- a 507 cannot even be attempted while Open");
            mockQueueRepo.Verify(r => r.PeekAsync(), Times.Never);
        }

        [Test]
        public async Task Breaker_507OnHalfOpenProbe_LatchesBlock_AndClosesBreaker()
        {
            var tempDir = MakeTempDir();
            try
            {
                var repo = new UploadQueueRepository(tempDir);
                var apiClient = new Mock<ICloudApiClient>();
                apiClient.Setup(c => c.BaseUrl).Returns("https://api.astrovault.io");
                var authManager = new Mock<IAuthManager>();
                authManager.Setup(a => a.GetApiKey()).Returns("test-api-key");
                var manager = new UploadManager(repo, apiClient.Object, authManager.Object);
                manager.SetCircuitBreakerRecoveryTimeoutForTest(TimeSpan.FromMilliseconds(1));

                // Open the breaker (5 consecutive transient failures against jobs not in the repo --
                // HandleUploadFailure tolerates unknown job ids the same way the mocked-repo tests do).
                for (int i = 0; i < 5; i++)
                {
                    await manager.HandleUploadFailure(TestDataFactory.CreateUploadJob(), UploadResult.Fail("timeout"), CancellationToken.None);
                }
                Assert.That(manager.IsCircuitOpen, Is.True, "Precondition: breaker Open");

                await Task.Delay(20); // let the 1ms recovery timeout elapse

                var probeJob = TestDataFactory.CreateUploadJob(Path.Combine(tempDir, "probe.fits"));
                await repo.EnqueueAsync(probeJob);
                apiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(QuotaFail());

                var processed = await manager.ProcessNextJobAsync(CancellationToken.None);

                Assert.That(processed, Is.True, "The Half-Open probe must have been attempted");
                Assert.That(manager.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded));
                Assert.That(manager.IsCircuitOpen, Is.False, "The connectivity proof must close the breaker on the probe");
                apiClient.Verify(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once,
                    "The probe costs exactly one request");

                var pending = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
                Assert.That(pending.Select(j => j.Id), Does.Contain(probeJob.Id), "The probed job returns to Pending, not Failed");
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        [Test]
        public async Task Breaker_400OnHalfOpenProbe_SkipFileClosesBreaker()
        {
            // Storage-quota plan directive F (A#8): a SERVER-originated 400/413 (PermanentSkipFile)
            // landing on the Half-Open probe still proves connectivity -- without this, the breaker
            // dead-ends Half-Open forever (ShouldAttempt keeps granting probes, IsOpen never clears)
            // because the skip-file branch neither records a breaker failure NOR a connectivity proof.
            var tempDir = MakeTempDir();
            try
            {
                var repo = new UploadQueueRepository(tempDir);
                var apiClient = new Mock<ICloudApiClient>();
                apiClient.Setup(c => c.BaseUrl).Returns("https://api.astrovault.io");
                var authManager = new Mock<IAuthManager>();
                authManager.Setup(a => a.GetApiKey()).Returns("test-api-key");
                var manager = new UploadManager(repo, apiClient.Object, authManager.Object);
                manager.SetCircuitBreakerRecoveryTimeoutForTest(TimeSpan.FromMilliseconds(1));

                for (int i = 0; i < 5; i++)
                {
                    await manager.HandleUploadFailure(TestDataFactory.CreateUploadJob(), UploadResult.Fail("timeout"), CancellationToken.None);
                }
                Assert.That(manager.IsCircuitOpen, Is.True, "Precondition: breaker Open");

                await Task.Delay(20); // let the 1ms recovery timeout elapse

                var probeJob = TestDataFactory.CreateUploadJob(Path.Combine(tempDir, "probe.fits"));
                await repo.EnqueueAsync(probeJob);
                apiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(UploadResult.Fail("Bad request", 400, FailureKind.PermanentSkipFile));

                var processed = await manager.ProcessNextJobAsync(CancellationToken.None);

                Assert.That(processed, Is.True, "The Half-Open probe must have been attempted");
                Assert.That(manager.IsCircuitOpen, Is.False,
                    "A server-originated 400 still proves connectivity -- it must close the breaker on the probe");

                var failed = (await repo.GetJobsByStatusAsync(UploadStatus.Failed)).ToList();
                Assert.That(failed.Select(j => j.Id), Does.Contain(probeJob.Id), "The file itself is still marked failed (skip-file semantics)");
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        [Test]
        public async Task HandleUploadFailure_LocalOriginPermanentFailure_DoesNotRecordConnectivityProof()
        {
            // The counterpart to the test above: a LOCAL-origin permanent failure (file not found --
            // StatusCode 0, never reached the server) proves nothing about server reachability and must
            // stay neutral -- it must not reset an already-elevated breaker failure count.
            for (int i = 0; i < 4; i++)
            {
                await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), UploadResult.Fail("timeout"), CancellationToken.None);
            }
            Assert.That(sut.IsCircuitOpen, Is.False, "Precondition: still closed at 4 consecutive failures");

            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(),
                UploadResult.FailPermanent("File not found: x.fits"), CancellationToken.None);

            // One more transient failure: if the local-origin permanent failure HAD reset the count
            // (wrongly treated as a connectivity proof), this would still show 4 -> 5 open; if it did
            // NOT reset it (correct), the SAME thing happens here since local failures don't touch the
            // breaker at all -- the real assertion is that it never dipped below 4 in between.
            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), UploadResult.Fail("timeout"), CancellationToken.None);
            Assert.That(sut.IsCircuitOpen, Is.True,
                "A local-origin permanent failure must not have reset the consecutive-failure count -- the 5th transient failure must still open the breaker");
        }

        // ====================================================================
        // Test 11: identity scoping via RestoreBlockStateAsync (foreign cleared / missing retained).
        // ====================================================================

        [Test]
        public async Task RestoreBlockStateAsync_NoPersistedBlock_StaysUnblocked()
        {
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(UploadBlockState.None);

            await sut.RestoreBlockStateAsync();

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.None));
            mockQueueRepo.Verify(r => r.ComputeIdentityFingerprintAsync(It.IsAny<string>()), Times.Never,
                "No block persisted -- no need to even compute a fingerprint");
        }

        [Test]
        public async Task RestoreBlockStateAsync_SameIdentity_RetainsBlock_NoDurableClear()
        {
            var persisted = new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                Snapshot = new UploadBlockSnapshot { UsedBytes = 1, LimitBytes = 2 },
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", KeyFingerprint = "same-fp" }
            };
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(persisted);
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("same-fp");

            await sut.RestoreBlockStateAsync();

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded));
            Assert.That(sut.BlockSnapshot!.UsedBytes, Is.EqualTo(1));
            mockQueueRepo.Verify(r => r.ClearBlockAsync(It.IsAny<Guid?>()), Times.Never);
        }

        [Test]
        public async Task RestoreBlockStateAsync_ForeignIdentity_ClearsBlock_Durably()
        {
            var persisted = new UploadBlockState
            {
                Kind = UploadBlockKind.AuthInvalid,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", KeyFingerprint = "old-fp" }
            };
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(persisted);
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("a-totally-different-fp");
            mockQueueRepo.Setup(r => r.ClearBlockAsync(null)).ReturnsAsync(true);

            await sut.RestoreBlockStateAsync();

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.None), "A foreign-identity block must be cleared, not retained");
            mockQueueRepo.Verify(r => r.ClearBlockAsync(null), Times.Once);
        }

        [Test]
        public async Task RestoreBlockStateAsync_ForeignIdentity_ClearFails_RetainsOriginalBlock_SetsPersistFailureWarning()
        {
            // Storage-quota plan directive C (B#1): the old code unconditionally cleared the in-memory
            // latch here regardless of ClearBlockAsync's result -- a disk-full/locked-file failure while
            // reconciling a foreign identity released the latch anyway, so uploads started even though
            // the durable block was still sitting on disk (and would resurrect on the next restart).
            var persisted = new UploadBlockState
            {
                Kind = UploadBlockKind.AuthInvalid,
                Snapshot = new UploadBlockSnapshot { UsedBytes = 1, LimitBytes = 2 },
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", KeyFingerprint = "old-fp" }
            };
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(persisted);
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("a-totally-different-fp");
            mockQueueRepo.Setup(r => r.ClearBlockAsync(null)).ReturnsAsync(false);

            await sut.RestoreBlockStateAsync();

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.AuthInvalid),
                "Fail-safe: retain the ORIGINAL block when the foreign-identity clear could not be persisted durably");
            Assert.That(sut.HasBlockPersistFailureWarning, Is.True);
        }

        [Test]
        public async Task RestoreBlockStateAsync_MissingCurrentIdentity_RetainsBlock_NeverClears()
        {
            // e.g. auth.dat lost while queue.dat survived: GetApiKey() returns null/empty.
            mockAuthManager.Setup(a => a.GetApiKey()).Returns((string)null);

            var persisted = new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                Snapshot = new UploadBlockSnapshot { UsedBytes = 5, LimitBytes = 10 },
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", KeyFingerprint = "whatever" }
            };
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(persisted);

            await sut.RestoreBlockStateAsync();

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "A missing CURRENT identity must RETAIN the block, never treat it as foreign");
            mockQueueRepo.Verify(r => r.ClearBlockAsync(It.IsAny<Guid?>()), Times.Never);
            mockQueueRepo.Verify(r => r.ComputeIdentityFingerprintAsync(It.IsAny<string>()), Times.Never,
                "No API key available -- nothing to fingerprint");
        }

        [Test]
        public async Task StartAsync_RestoresPersistedBlock_ProcessingTaskStaysAliveButIdle()
        {
            var persisted = new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", KeyFingerprint = "same-fp" }
            };
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(persisted);
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("same-fp");
            mockQueueRepo.Setup(r => r.GetJobCountAsync(It.IsAny<UploadStatus>())).ReturnsAsync(0);

            using var cts = new CancellationTokenSource();
            await sut.StartAsync(cts.Token);
            try
            {
                Assert.That(sut.IsRunning, Is.True, "The processing task stays alive even while latched on start");
                Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded));
            }
            finally
            {
                await sut.StopAsync();
            }
        }

        // ====================================================================
        // Storage-quota plan §4 Step 3: the typed UploadBlocked event.
        // ====================================================================

        [Test]
        public async Task HandleUploadFailure_QuotaBlock_CommitSucceeds_RaisesUploadBlocked_WithCorrectPayload()
        {
            UploadBlockedEventArgs captured = null;
            sut.UploadBlocked += (s, e) => captured = e;

            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), QuotaFail(16_000_000_000, 10_000_000_000), CancellationToken.None);

            Assert.That(captured, Is.Not.Null, "A successfully-persisted block transition must raise UploadBlocked");
            Assert.That(captured.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded));
            Assert.That(captured.Snapshot, Is.Not.Null);
            Assert.That(captured.Snapshot.UsedBytes, Is.EqualTo(16_000_000_000));
            Assert.That(captured.Snapshot.LimitBytes, Is.EqualTo(10_000_000_000));
            Assert.That(captured.IsPersistFailure, Is.False, "The commit succeeded -- this must not be flagged as a persist failure");
        }

        [Test]
        public async Task HandleUploadFailure_AuthBlock_CommitSucceeds_RaisesUploadBlocked_AuthInvalidKind()
        {
            UploadBlockedEventArgs captured = null;
            sut.UploadBlocked += (s, e) => captured = e;

            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), AuthFail(403), CancellationToken.None);

            Assert.That(captured, Is.Not.Null);
            Assert.That(captured.Kind, Is.EqualTo(UploadBlockKind.AuthInvalid));
        }

        [Test]
        public async Task HandleUploadFailure_QuotaBlock_DurableCommitFails_RaisesUploadBlocked_MarkedPersistFailure()
        {
            // Storage-quota plan directive H (B#2): a failed SetBlockAsync commit still raises the typed
            // UploadBlocked event -- marked IsPersistFailure so a subscriber can tell "durably committed"
            // from "in-memory only, surface the warning" apart, instead of the old behavior where a
            // failed commit was completely invisible to every UI subscriber.
            mockQueueRepo.Setup(r => r.SetBlockAsync(
                    It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(), It.IsAny<UploadBlockIdentityInput>()))
                .ReturnsAsync(false);

            UploadBlockedEventArgs captured = null;
            sut.UploadBlocked += (s, e) => captured = e;

            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), QuotaFail(), CancellationToken.None);

            Assert.That(captured, Is.Not.Null, "A failed commit is still a live transition into a block");
            Assert.That(captured.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded));
            Assert.That(captured.IsPersistFailure, Is.True, "The commit did not durably succeed -- the payload must say so");
        }

        [Test]
        public async Task RestoreBlockStateAsync_SameIdentity_RetainsBlock_DoesNotRaiseUploadBlocked()
        {
            // A startup restore must NEVER raise UploadBlocked -- only QueueStateChanged. This is what
            // lets the plugin's toast fire exactly once per LIVE transition and never replay on reload.
            var persisted = new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                Snapshot = new UploadBlockSnapshot { UsedBytes = 1, LimitBytes = 2 },
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", KeyFingerprint = "same-fp" }
            };
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(persisted);
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("same-fp");

            var uploadBlockedRaised = false;
            var queueStateChangedRaised = false;
            sut.UploadBlocked += (s, e) => uploadBlockedRaised = true;
            sut.QueueStateChanged += (s, e) => queueStateChangedRaised = true;

            await sut.RestoreBlockStateAsync();

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "Precondition: block restored");
            Assert.That(uploadBlockedRaised, Is.False, "A startup restore must never raise the typed UploadBlocked event");
            Assert.That(queueStateChangedRaised, Is.True, "The general QueueStateChanged notification still fires on restore");
        }

        // ====================================================================
        // Storage-quota plan directive E (A#6): latch-time bounded accountId lookup.
        // ====================================================================

        [Test]
        public async Task EnterBlockAsync_QuotaBlock_NoCachedAccountId_AttemptsOneBoundedQuotaLookup()
        {
            // The key is still valid here (quota block only, never AuthInvalid) -- one bounded
            // GetQuotaAsync call upgrades the block's scoping from fingerprint to account immediately
            // instead of waiting for some later incidental refresh.
            mockApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QuotaUsage { AccountId = "acct-from-latch-lookup" });

            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), QuotaFail(), CancellationToken.None);

            mockApiClient.Verify(c => c.GetQuotaAsync(It.IsAny<CancellationToken>()), Times.Once);
            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(),
                It.Is<UploadBlockIdentityInput>(i => i.AccountId == "acct-from-latch-lookup")), Times.Once);
        }

        [Test]
        public async Task EnterBlockAsync_QuotaBlock_AccountAlreadyCached_SkipsTheLookup()
        {
            sut.SetAccountId("already-known");

            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), QuotaFail(), CancellationToken.None);

            mockApiClient.Verify(c => c.GetQuotaAsync(It.IsAny<CancellationToken>()), Times.Never,
                "No extra round trip when the account id is already known");
        }

        [Test]
        public async Task EnterBlockAsync_AuthBlock_NeverAttemptsTheLookup()
        {
            // AuthInvalid means the key is DEAD -- a lookup would just fail with another 401/403.
            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), AuthFail(401), CancellationToken.None);

            mockApiClient.Verify(c => c.GetQuotaAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task EnterBlockAsync_QuotaBlock_LookupFails_PersistsWithNullAccountId_LatchStillSucceeds()
        {
            mockApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("network down"));

            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), QuotaFail(), CancellationToken.None);

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "The lookup failure must never block the latch itself");
            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(),
                It.Is<UploadBlockIdentityInput>(i => i.AccountId == null)), Times.Once);
        }

        // ====================================================================
        // Storage-quota plan §4 Step 6: accountId threaded into block identity.
        // ====================================================================

        [Test]
        public async Task SetAccountId_ThenEnterBlock_IdentityCarriesAccountId()
        {
            sut.SetAccountId("acct-123");

            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), QuotaFail(), CancellationToken.None);

            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(),
                It.Is<UploadBlockIdentityInput>(i => i.AccountId == "acct-123")), Times.Once,
                "A prior SetAccountId call must be threaded into EnterBlockAsync's identity input");
        }

        [Test]
        public async Task NoSetAccountId_EnterBlock_IdentityCarriesNullAccountId()
        {
            // No SetAccountId call this session (e.g. GetQuotaAsync never succeeded yet) -- must degrade
            // to the pre-Step-6 null placeholder, not throw or fabricate a value.
            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), QuotaFail(), CancellationToken.None);

            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(),
                It.Is<UploadBlockIdentityInput>(i => i.AccountId == null)), Times.Once);
        }

        [Test]
        public async Task SetAccountId_EmptyOrWhitespace_NormalizedToNull()
        {
            sut.SetAccountId("   ");

            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), QuotaFail(), CancellationToken.None);

            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(),
                It.Is<UploadBlockIdentityInput>(i => i.AccountId == null)), Times.Once);
        }

        [Test]
        public async Task RestoreBlockStateAsync_KeyRotationSameAccount_RetainsBlock_ViaAccountIdMatch()
        {
            // Plan row (test table 11 / D-note): key rotation on the SAME account keeps the block.
            // The persisted identity's fingerprint reflects the OLD key; the CURRENT fingerprint (for
            // the newly-rotated key) is deliberately made to differ -- proving the match succeeds via
            // AccountId, not by coincidentally matching fingerprints.
            sut.SetAccountId("acct-999");

            var persisted = new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                Snapshot = new UploadBlockSnapshot { UsedBytes = 5, LimitBytes = 10 },
                Identity = new UploadBlockIdentity
                {
                    EndpointUrl = "https://api.astrovault.io",
                    AccountId = "acct-999",
                    KeyFingerprint = "old-key-fingerprint"
                }
            };
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(persisted);
            // The rotated (current) key hashes to a DIFFERENT fingerprint than the persisted one.
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("rotated-key-fingerprint");

            await sut.RestoreBlockStateAsync();

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "Same account, rotated key -- the block must be retained via AccountId match, not cleared");
            mockQueueRepo.Verify(r => r.ClearBlockAsync(It.IsAny<Guid?>()), Times.Never);
        }

        [Test]
        public async Task RestoreBlockStateAsync_ExplicitAccountIdArgument_TakesPrecedenceOverStoredField()
        {
            // The optional parameter (a test seam / a future direct caller) wins over whatever
            // SetAccountId recorded -- proves the fallback is `accountId ?? this.accountId`, not the
            // other way around.
            sut.SetAccountId("stale-account-from-earlier-session");

            var persisted = new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                Identity = new UploadBlockIdentity
                {
                    EndpointUrl = "https://api.astrovault.io",
                    AccountId = "fresh-account",
                    KeyFingerprint = "whatever-fp"
                }
            };
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(persisted);
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("different-fp");

            await sut.RestoreBlockStateAsync("fresh-account");

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "The explicit argument must be used for the identity match, not the stale stored field");
        }

        // ====================================================================
        // Storage-quota plan directive G: EnterBlockAsync exception boundary.
        // ====================================================================

        [Test]
        public async Task UploadJobAsync_LatchPathThrows_FailsSafeInMemory_NeverFabricatesTransientRetry()
        {
            // Storage-quota plan directive G (A#9): the old broad try/catch in UploadJobAsync wrapped
            // HandleUploadFailure itself, so an exception from INSIDE the latch path (SetBlockAsync here)
            // was recast as a fabricated Fail(ex.Message) and re-fed through HandleUploadFailure a SECOND
            // time -- losing the original 507 classification, feeding the breaker, and scheduling a
            // retry. The fix must fail safe instead: stay latched in memory with the ORIGINAL kind.
            var job = TestDataFactory.CreateUploadJob();
            mockApiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(QuotaFail(16_000_000_000, 10_000_000_000));
            mockQueueRepo.Setup(r => r.SetBlockAsync(
                    It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(), It.IsAny<UploadBlockIdentityInput>()))
                .ThrowsAsync(new IOException("disk exploded"));

            Assert.DoesNotThrowAsync(async () => await sut.UploadJobAsync(job, CancellationToken.None),
                "An exception from the latch path must never escape UploadJobAsync");

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "Fail-safe: the ORIGINAL 507 classification must be preserved, not lost to a fabricated retry");
            Assert.That(sut.HasBlockPersistFailureWarning, Is.True);
            Assert.That(sut.IsCircuitOpen, Is.False,
                "The exception must never feed the breaker as if it were an ordinary transient failure");
            mockQueueRepo.Verify(r => r.ScheduleRetryAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<string>()), Times.Never,
                "The exception must never be recast into a scheduled retry");
            mockQueueRepo.Verify(r => r.MarkFailedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never,
                "The exception must never be recast into a permanent skip-file failure");
        }

        // ====================================================================
        // Contract-delta round: QuotaBlockFlavor threaded from QuotaInfo through EnterBlockAsync into
        // the persisted snapshot -- classification (BlockKind) stays purely status-driven regardless.
        // ====================================================================

        [Test]
        public async Task HandleUploadFailure_Quota507_PlanRequiredFlavor_SnapshotCarriesPlanRequired()
        {
            var quotaInfo = new QuotaInfo { UsedBytes = 0, LimitBytes = 0, Flavor = QuotaBlockFlavor.PlanRequired };
            var result = UploadResult.Fail("Insufficient storage", 507, FailureKind.PermanentQuota, quotaInfo);

            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), result, CancellationToken.None);

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "Classification is purely status-driven -- PLAN_REQUIRED is still a 507/PermanentQuota latch");
            Assert.That(sut.BlockSnapshot!.Flavor, Is.EqualTo(QuotaBlockFlavor.PlanRequired));
            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(),
                It.Is<UploadBlockSnapshot>(s => s.Flavor == QuotaBlockFlavor.PlanRequired),
                It.IsAny<UploadBlockIdentityInput>()), Times.Once);
        }

        [Test]
        public async Task HandleUploadFailure_Quota507_StorageLimitExceededFlavor_SnapshotCarriesStorageLimitExceeded()
        {
            var quotaInfo = new QuotaInfo { UsedBytes = 5, LimitBytes = 10, Flavor = QuotaBlockFlavor.StorageLimitExceeded };
            var result = UploadResult.Fail("Insufficient storage", 507, FailureKind.PermanentQuota, quotaInfo);

            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), result, CancellationToken.None);

            Assert.That(sut.BlockSnapshot!.Flavor, Is.EqualTo(QuotaBlockFlavor.StorageLimitExceeded));
        }

        [Test]
        public async Task HandleUploadFailure_Quota507_NullQuotaInfo_MalformedBody_FlavorDefaultsToStorageLimitExceeded_LatchStillCorrect()
        {
            // A malformed/unparseable 507 body: QuotaInfoParser.TryParse returned null, so
            // UploadResult.QuotaInfo is null altogether -- the latch itself must still succeed
            // (classification never depends on the body) and the Flavor must default safely.
            var result = UploadResult.Fail("Insufficient storage", 507, FailureKind.PermanentQuota, quotaInfo: null);

            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), result, CancellationToken.None);

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "A malformed/absent quota body must never prevent the latch");
            Assert.That(sut.BlockSnapshot!.Flavor, Is.EqualTo(QuotaBlockFlavor.StorageLimitExceeded));
        }

        [Test]
        public async Task HandleUploadFailure_AuthInvalid_FlavorDefaultsToStorageLimitExceeded_Meaningless()
        {
            // AuthInvalid never carries a QuotaInfo (never a 507) -- Flavor is meaningless here but must
            // still default safely rather than being left at some undefined value.
            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), AuthFail(401), CancellationToken.None);

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.AuthInvalid));
            Assert.That(sut.BlockSnapshot!.Flavor, Is.EqualTo(QuotaBlockFlavor.StorageLimitExceeded));
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private static string MakeTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "upload_manager_block_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void CleanupTempDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
