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
    /// <summary>
    /// Manager-level tests for storage-quota plan §4 Steps 4/5: <c>UpdateContext</c> (the G13
    /// stale-auth-manager fix) and <c>ResumeAsync</c> (explicit resume from a blocked state, pinning,
    /// the durable-commit fail-safe, and the immediate drain-loop wake). Covers step-7 test table rows
    /// 4 (resume portion), 12 (context-swap regression), 13 (pin ordering), plus the resume->507
    /// re-latch and failed-commit scenarios called out in the Step 4/5 handoff.
    /// </summary>
    [TestFixture]
    [Category("StorageQuota")]
    public class UploadManagerResumeTests
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
            mockQueueRepo.Setup(r => r.GetTotalPendingSizeAsync()).ReturnsAsync(0L);
            mockQueueRepo.Setup(r => r.GetJobCountAsync(It.IsAny<UploadStatus>())).ReturnsAsync(0);
            mockQueueRepo.Setup(r => r.SetBlockAsync(
                    It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(), It.IsAny<UploadBlockIdentityInput>()))
                .ReturnsAsync(true);

            mockApiClient.Setup(c => c.BaseUrl).Returns("https://api.astrovault.io");
            mockAuthManager.Setup(a => a.GetApiKey()).Returns("test-api-key");

            sut = new UploadManager(mockQueueRepo.Object, mockApiClient.Object, mockAuthManager.Object);
        }

        private static UploadResult QuotaFail() =>
            UploadResult.Fail("Insufficient storage", 507, FailureKind.PermanentQuota,
                new QuotaInfo { UsedBytes = 16_000_000_000, LimitBytes = 10_000_000_000 });

        // ====================================================================
        // UpdateContext -- G13 regression (row 12/15's core mechanism)
        // ====================================================================

        [Test]
        public async Task UpdateContext_SwapsBothReferences_SubsequentReadsUseTheNewAuthManager()
        {
            // Simulate the reinit sequence: the manager starts with an auth manager that has NO key
            // (unconfigured), then a reinit swaps in a NEW auth manager WITH a key, and the OLD one is
            // disposed immediately after -- exactly what AstrovaultPlugin.ReinitializeHttpServicesAsync
            // does. G13's bug was: the manager kept reading the OLD (now-disposed) instance forever.
            var oldAuthManager = new Mock<IAuthManager>();
            oldAuthManager.Setup(a => a.GetApiKey()).Returns((string)null);
            var newAuthManager = new Mock<IAuthManager>();
            newAuthManager.Setup(a => a.GetApiKey()).Returns("live-key");
            var newApiClient = new Mock<ICloudApiClient>();
            newApiClient.Setup(c => c.BaseUrl).Returns("https://api.astrovault.io");

            var manager = new UploadManager(mockQueueRepo.Object, mockApiClient.Object, oldAuthManager.Object);

            manager.UpdateContext(newAuthManager.Object, newApiClient.Object);
            oldAuthManager.Object.Dispose(); // the disposed instance must never be touched again

            var job = TestDataFactory.CreateUploadJob();
            mockQueueRepo.Setup(r => r.PeekAsync()).ReturnsAsync(job);
            newApiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(UploadResult.Ok("https://cdn/x"));

            var processed = await manager.ProcessNextJobAsync(CancellationToken.None);

            Assert.That(processed, Is.True, "The manager must attempt the upload using the LIVE (new) auth manager's key");
            newApiClient.Verify(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once);
            oldAuthManager.Verify(a => a.GetApiKey(), Times.Never,
                "The old (disposed) auth manager must never be read again after UpdateContext");
        }

        [Test]
        public void UpdateContext_NullAuthManager_Throws()
        {
            Assert.That(() => sut.UpdateContext(null, mockApiClient.Object), Throws.ArgumentNullException);
        }

        [Test]
        public void UpdateContext_NullApiClient_Throws()
        {
            Assert.That(() => sut.UpdateContext(mockAuthManager.Object, null), Throws.ArgumentNullException);
        }

        [Test]
        public async Task UpdateContext_ThenStartAsync_ReconcilesPersistedBlockAgainstTheNewIdentity()
        {
            // Storage-quota plan §4 Step 2 point 4, verified end-to-end on the STEP 4 reinit path: this
            // reproduces AstrovaultPlugin.ReinitializeHttpServicesAsync's exact composition at the
            // manager level -- UpdateContext (Step E's G13 fix) swaps in a new identity, then the reinit
            // tail's EnsureUploadPipelineAsync calls StartAsync, whose existing RestoreBlockStateAsync
            // call must reconcile against the identity that is live AFTER the swap, not the one that was
            // live when the block was originally entered.
            var oldAuthManager = new Mock<IAuthManager>();
            oldAuthManager.Setup(a => a.GetApiKey()).Returns("old-key");
            var manager = new UploadManager(mockQueueRepo.Object, mockApiClient.Object, oldAuthManager.Object);

            var persisted = new UploadBlockState
            {
                Kind = UploadBlockKind.AuthInvalid,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", KeyFingerprint = "old-fp" }
            };
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(persisted);
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("new-key")).ReturnsAsync("new-fp");
            mockQueueRepo.Setup(r => r.ClearBlockAsync(null)).ReturnsAsync(true);

            var newAuthManager = new Mock<IAuthManager>();
            newAuthManager.Setup(a => a.GetApiKey()).Returns("new-key");
            var newApiClient = new Mock<ICloudApiClient>();
            newApiClient.Setup(c => c.BaseUrl).Returns("https://api.astrovault.io");

            manager.UpdateContext(newAuthManager.Object, newApiClient.Object); // the reinit context swap

            using var cts = new CancellationTokenSource();
            await manager.StartAsync(cts.Token); // the reinit tail's EnsureUploadPipelineAsync -> StartAsync
            try
            {
                Assert.That(manager.BlockKind, Is.EqualTo(UploadBlockKind.None),
                    "A foreign-identity block must be cleared using the NEW (post-UpdateContext) identity");
                mockQueueRepo.Verify(r => r.ComputeIdentityFingerprintAsync("new-key"), Times.Once,
                    "Reconciliation must fingerprint the NEW key -- the one active when the block was entered must never be consulted again");
            }
            finally
            {
                await manager.StopAsync();
            }
        }

        // ====================================================================
        // Storage-quota plan directive B (A#2): StopAsync/StartAsync serialization.
        // ====================================================================

        [Test]
        public async Task StopAsync_HoldsGateAcrossTheWholeSequence_ConcurrentStartAsync_WaitsThenStartsCleanly()
        {
            // The old bug: StopAsync released lifecycleLock right after Cancel() and only THEN awaited
            // the drain loop -- so a concurrent StartAsync could see IsRunning still true (the old task
            // hadn't actually finished) and wrongly no-op as "already running", leaving the manager
            // stopped forever with no restart. StopAsync must now hold the lock across the ENTIRE
            // sequence, so a concurrent StartAsync simply waits until the stop has truly finished, then
            // starts a fresh run deterministically.
            var releaseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mockQueueRepo.Setup(r => r.PeekAsync()).Returns(async () =>
            {
                await releaseGate.Task; // simulates the drain loop being mid-tick when Stop is requested
                return null;
            });

            using var cts = new CancellationTokenSource();
            await sut.StartAsync(cts.Token);
            Assert.That(sut.IsRunning, Is.True, "Precondition: running");

            var stopTask = sut.StopAsync(); // acquires the lock, cancels, then blocks awaiting the stuck drain loop
            await Task.Delay(50); // let StopAsync actually acquire the lock and call Cancel()

            using var cts2 = new CancellationTokenSource(); // a FRESH token -- cts was already cancelled by Stop
            var startTask = sut.StartAsync(cts2.Token); // must queue behind the still-in-flight stop

            releaseGate.TrySetResult(true); // let the stuck drain-loop tick unwind on cancellation

            await stopTask;
            await startTask;

            Assert.That(sut.IsRunning, Is.True,
                "The concurrent StartAsync, correctly serialized to run AFTER the stop fully finished, must have started a fresh run");
        }

        // ====================================================================
        // Storage-quota plan (C-P1#1 fix): clean cancellation must not strand the active job as
        // InProgress -- neither across a same-process restart (endpoint reinit / Disconnect+Reconnect)
        // nor within the same repository instance in general.
        // ====================================================================

        [Test]
        public async Task UploadJobAsync_CleanCancellation_ResetsJobToPendingDurably_SameRepositoryInstance()
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
                File.WriteAllText(job.LocalPath, "x");
                await repo.EnqueueAsync(job);

                using var cts = new CancellationTokenSource();
                apiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                    .Returns((UploadJob j, IProgress<double> p, CancellationToken ct) =>
                    {
                        // Simulate the caller (StopAsync) cancelling mid-upload -- the API client's own
                        // send path observes the token and throws, exactly like a real HttpClient send.
                        cts.Cancel();
                        throw new OperationCanceledException(ct);
                    });

                Assert.ThrowsAsync<OperationCanceledException>(async () => await manager.UploadJobAsync(job, cts.Token));

                Assert.That(await repo.GetJobCountAsync(UploadStatus.InProgress), Is.EqualTo(0),
                    "The job must not be stranded InProgress after a clean cancellation");
                var pending = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
                Assert.That(pending.Select(j => j.Id), Does.Contain(job.Id), "Cancel mid-upload -> the job is Pending again");
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        [Test]
        public async Task CleanCancellation_ThenEndpointReinitSequence_SameRepositoryInstance_JobUploads_NoInvisibleFrame()
        {
            // The exact C review scenario: cancel mid-upload -> job is Pending again; endpoint reinit
            // (stop -> UpdateContext -> start with the SAME repository instance) -> the job uploads, no
            // invisible frame (a job that stayed InProgress forever, invisible to PeekAsync, until a
            // full process restart forced a fresh disk load).
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
                File.WriteAllText(job.LocalPath, "x");
                await repo.EnqueueAsync(job);

                using var startCts = new CancellationTokenSource();
                await manager.StartAsync(startCts.Token);

                var uploadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                apiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                    .Returns(async (UploadJob j, IProgress<double> p, CancellationToken ct) =>
                    {
                        uploadStarted.TrySetResult(true);
                        await Task.Delay(Timeout.Infinite, ct); // blocks until StopAsync cancels
                        throw new OperationCanceledException(ct); // unreachable -- Delay throws first
                    });

                await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // Endpoint reinit sequence: stop -> UpdateContext -> start, reusing the SAME repository.
                await manager.StopAsync();

                Assert.That(await repo.GetJobCountAsync(UploadStatus.InProgress), Is.EqualTo(0),
                    "Clean cancellation during Stop must not strand the job InProgress");

                var newApiClient = new Mock<ICloudApiClient>();
                newApiClient.Setup(c => c.BaseUrl).Returns("https://api.astrovault.io");
                var newAuthManager = new Mock<IAuthManager>();
                newAuthManager.Setup(a => a.GetApiKey()).Returns("new-key");
                manager.UpdateContext(newAuthManager.Object, newApiClient.Object);
                newApiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(UploadResult.Ok("https://cdn/x"));

                using var restartCts = new CancellationTokenSource();
                await manager.StartAsync(restartCts.Token);
                try
                {
                    var uploaded = false;
                    for (int i = 0; i < 40 && !uploaded; i++)
                    {
                        uploaded = await repo.GetJobCountAsync(UploadStatus.Completed) == 1;
                        if (!uploaded) await Task.Delay(50);
                    }

                    Assert.That(uploaded, Is.True,
                        "The job must NOT be an invisible frame -- it must be attempted and upload after the same-process restart");
                }
                finally
                {
                    await manager.StopAsync();
                }
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        [Test]
        public void UpdateContext_ClearsCachedAccountId_AndBumpsContextGeneration()
        {
            // Storage-quota plan directive E (A#6, B#4): a new context means a new identity -- any
            // cached accountId belonged to the OLD endpoint/credential pair and must never leak into a
            // block latched under the new one.
            sut.SetAccountId("stale-account");
            var generationBefore = sut.ContextGeneration;

            sut.UpdateContext(mockAuthManager.Object, mockApiClient.Object);

            Assert.That(sut.ContextGeneration, Is.GreaterThan(generationBefore),
                "UpdateContext must bump the generation so an in-flight fetch from the OLD context is recognized as stale");
        }

        // ====================================================================
        // SetAccountIdAsync -- generation-guarded staleness + live-block reconciliation (directive E)
        // ====================================================================

        [Test]
        public async Task SetAccountIdAsync_StaleGeneration_Dropped_AccountIdNeverApplied()
        {
            // Storage-quota plan directive E (A#6, B#4): a quota fetch that started under an OLDER
            // context generation must never persist its result once the context has since changed.
            var staleGeneration = sut.ContextGeneration;
            sut.UpdateContext(mockAuthManager.Object, mockApiClient.Object); // bumps the generation

            var applied = await sut.SetAccountIdAsync("stale-account-from-before-reinit", staleGeneration);

            Assert.That(applied, Is.False);

            // Prove it was never cached: latch a quota block and check the identity input carries null.
            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(),
                UploadResult.Fail("Insufficient storage", 507, FailureKind.PermanentQuota, new QuotaInfo()),
                CancellationToken.None);
            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(),
                It.Is<UploadBlockIdentityInput>(i => i.AccountId == null)), Times.Once);
        }

        [Test]
        public async Task SetAccountIdAsync_CurrentGeneration_Applied()
        {
            var applied = await sut.SetAccountIdAsync("acct-live", sut.ContextGeneration);

            Assert.That(applied, Is.True);
            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(),
                UploadResult.Fail("Insufficient storage", 507, FailureKind.PermanentQuota, new QuotaInfo()),
                CancellationToken.None);
            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(),
                It.Is<UploadBlockIdentityInput>(i => i.AccountId == "acct-live")), Times.Once);
        }

        [Test]
        public async Task SetAccountIdAsync_SameAccountAsLiveBlock_KeepsBlock_NeverClears()
        {
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = "acct-same" }
            });

            var applied = await sut.SetAccountIdAsync("acct-same", sut.ContextGeneration);

            Assert.That(applied, Is.True);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "The SAME account must not clear its own block");
            mockQueueRepo.Verify(r => r.ClearBlockAsync(It.IsAny<Guid?>()), Times.Never);
        }

        [Test]
        public async Task SetAccountIdAsync_DifferentKnownAccount_ClearsLiveBlockDurably()
        {
            // Storage-quota plan directive E (B#3): switching to a DIFFERENT, now-valid account must
            // clear a live block scoped to a known but different account -- e.g. account A is
            // quota-blocked, the user validates account B's key, B's quota fetch must clear A's block.
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = "acct-A" }
            });
            mockQueueRepo.Setup(r => r.ClearBlockAsync(job.Id)).ReturnsAsync(true);
            // Storage-quota plan directive E (C-P1#3): live reconciliation now reuses
            // UploadBlockIdentityMatcher.MatchesCurrent, whose OWN fail-safe retains when the CURRENT
            // fingerprint is unavailable -- a non-empty fingerprint here represents the realistic
            // production case (the key that just succeeded the quota fetch IS present/fingerprintable).
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("current-fp");

            var applied = await sut.SetAccountIdAsync("acct-B", sut.ContextGeneration);

            Assert.That(applied, Is.True);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.None),
                "Account A's block must be durably cleared once account B is proven live and different");
            mockQueueRepo.Verify(r => r.ClearBlockAsync(job.Id), Times.Once);
        }

        [Test]
        public async Task SetAccountIdAsync_DifferentAccount_ClearCommitFails_StaysLatched_SetsWarning()
        {
            // The same fail-safe discipline (directive C) applies to reconciliation's own clear attempt.
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = "acct-A" }
            });
            mockQueueRepo.Setup(r => r.ClearBlockAsync(job.Id)).ReturnsAsync(false);
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("current-fp");

            await sut.SetAccountIdAsync("acct-B", sut.ContextGeneration);

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "Fail-safe: stays latched when the clear commit fails");
            Assert.That(sut.HasBlockPersistFailureWarning, Is.True);
        }

        [Test]
        public async Task SetAccountIdAsync_LiveBlockHasNoKnownAccount_DoesNotReconcile()
        {
            // A block persisted before account-id scoping existed (or restored with only a fingerprint)
            // has nothing to compare against -- must not guess, must not clear.
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = null, KeyFingerprint = "fp" }
            });

            await sut.SetAccountIdAsync("acct-B", sut.ContextGeneration);

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded));
            mockQueueRepo.Verify(r => r.ClearBlockAsync(It.IsAny<Guid?>()), Times.Never);
        }

        [Test]
        public async Task SetAccountIdAsync_NoLiveBlock_JustCachesTheAccountId()
        {
            var applied = await sut.SetAccountIdAsync("acct-x", sut.ContextGeneration);

            Assert.That(applied, Is.True);
            mockQueueRepo.Verify(r => r.ClearBlockAsync(It.IsAny<Guid?>()), Times.Never);
        }

        // ====================================================================
        // Storage-quota plan directive E (C-P1#2): the exact C review scenario -- replacing the key
        // must not let a stale cached account durably clear the NEW key's genuine block.
        // ====================================================================

        [Test]
        public async Task KeyReplacement_ClearsCachedAccount_NewKeysOwn507_ThenOwnQuotaRefresh_BlockRetained_NeverForeignCleared()
        {
            // account A was cached under the OLD key. TestConnectionAsync's fix (C-P1#2) clears it via
            // SetAccountId(null) BEFORE the replacement key (B) becomes active. B's own upload then 507s
            // while nothing is cached -- EnterBlockAsync's bounded latch-time lookup discovers B's REAL
            // account and persists the block scoped to it (never mis-tagged as A's). B's own later quota
            // refresh, reporting that SAME account, must retain the block -- not durably clear it as a
            // foreign identity.
            sut.SetAccountId("acct-A"); // stale identity cached under the OLD key
            sut.SetAccountId(null);     // C-P1#2 fix: TestConnectionAsync clears it before B becomes active

            mockApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QuotaUsage { AccountId = "acct-B" }); // B's real account, via the bounded lookup

            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "Precondition: latched");
            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(),
                It.Is<UploadBlockIdentityInput>(i => i.AccountId == "acct-B")), Times.Once,
                "The block must be scoped to B's REAL account (from the latch lookup) -- never left null or mis-tagged as A's");

            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = "acct-B" }
            });

            // B's own quota refresh lands afterward, reporting the SAME account it already proved.
            var applied = await sut.SetAccountIdAsync("acct-B", sut.ContextGeneration);

            Assert.That(applied, Is.True);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "B's genuine 507 must NOT be durably cleared by B's own later quota refresh");
            mockQueueRepo.Verify(r => r.ClearBlockAsync(It.IsAny<Guid?>()), Times.Never);
        }

        [Test]
        public async Task SetAccountId_Null_ThenOldGenerationSetAccountIdAsync_Rejected()
        {
            // The other half of C-P1#2: a stale in-flight quota refresh started under the OLD key (its
            // generation captured before the replacement) must be dropped once the key has been
            // replaced, never re-applying the OLD account.
            var staleGeneration = sut.ContextGeneration;
            sut.SetAccountId(null); // key replacement -- bumps the generation

            var applied = await sut.SetAccountIdAsync("acct-A-stale-result", staleGeneration);

            Assert.That(applied, Is.False, "A result captured under the pre-replacement generation must be dropped");
        }

        // ====================================================================
        // Pass I fix: TryBeginConnectAccountCycle -- the connect flow's clear-old-account step must be
        // ATOMIC with the staleness decision (compare-and-clear under one accountIdentityLock hold),
        // replacing the old check-then-act (compare ContextGeneration, then SetAccountId(null)) gap.
        // ====================================================================

        [Test]
        public async Task TryBeginConnectAccountCycle_GenerationMatch_ClearsAccountId_BumpsGeneration_ReturnsTrueWithNewGeneration()
        {
            sut.SetAccountId("acct-stale"); // seed a cached identity -- also bumps the generation to 1
            var expectedGeneration = sut.ContextGeneration;

            var owns = sut.TryBeginConnectAccountCycle(expectedGeneration, out var connectGeneration);

            Assert.That(owns, Is.True, "A matching generation must let this connect cycle own the begin");
            Assert.That(connectGeneration, Is.EqualTo(expectedGeneration + 1),
                "The begin bumps the generation exactly once and hands back the NEW value");
            Assert.That(sut.ContextGeneration, Is.EqualTo(connectGeneration),
                "The manager's live generation must reflect the bump immediately");

            // Prove the cached accountId was actually cleared (not just the generation bumped): a block
            // latched right now must carry a null accountId, exactly like a fresh, never-cached manager.
            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), QuotaFail(), CancellationToken.None);
            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(),
                It.Is<UploadBlockIdentityInput>(i => i.AccountId == null)), Times.Once,
                "TryBeginConnectAccountCycle must clear the cached accountId on a generation match, same as SetAccountId(null) did before this fix");
        }

        [Test]
        public async Task TryBeginConnectAccountCycle_GenerationMismatch_ReturnsFalse_AccountIdAndGenerationUntouched()
        {
            sut.SetAccountId("acct-live"); // seed a cached identity -- generation bumped to 1
            var currentGeneration = sut.ContextGeneration;
            var staleExpected = currentGeneration - 1; // simulates a caller holding a pre-swap generation

            var owns = sut.TryBeginConnectAccountCycle(staleExpected, out var connectGeneration);

            Assert.That(owns, Is.False, "A mismatched (stale) expected generation must not let the caller own the cycle");
            Assert.That(connectGeneration, Is.EqualTo(currentGeneration),
                "On mismatch, the out parameter must still report the CURRENT generation so the caller can log/compare");
            Assert.That(sut.ContextGeneration, Is.EqualTo(currentGeneration),
                "A mismatch must mutate nothing -- the generation must not move");

            // Prove the cached accountId was left untouched: a block latched now must still carry it.
            await sut.HandleUploadFailure(TestDataFactory.CreateUploadJob(), QuotaFail(), CancellationToken.None);
            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(),
                It.Is<UploadBlockIdentityInput>(i => i.AccountId == "acct-live")), Times.Once,
                "A mismatched TryBeginConnectAccountCycle must not clear the cached accountId");
        }

        // ====================================================================
        // Storage-quota plan directive E (C-P1#3): live reconciliation must be endpoint-aware, same as
        // restore matching -- reuses UploadBlockIdentityMatcher instead of a separate comparison.
        // ====================================================================

        [Test]
        public async Task SetAccountIdAsync_SameAccountIdString_DifferentEndpoint_TreatedAsForeign_ClearsDurably()
        {
            // A block persisted under endpoint 1 with account X; the endpoint changes to endpoint 2; a
            // quota fetch on endpoint 2 returns the SAME account-id string X. A matching accountId
            // string on a DIFFERENT endpoint is not proof of the same identity (the same account could
            // plausibly exist against a different custom backend) -- the matcher's own documented rule
            // (see UploadBlockIdentityMatcherTests.MatchesCurrent_SameAccountId_DifferentEndpoint_IsForeign).
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None); // latches against endpoint 1
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = "acct-X" }
            });
            mockQueueRepo.Setup(r => r.ClearBlockAsync(job.Id)).ReturnsAsync(true);
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("current-fp");

            // The endpoint changed (mirrors AstrovaultPlugin.ReinitializeHttpServicesAsync's UpdateContext swap).
            mockApiClient.Setup(c => c.BaseUrl).Returns("https://api2.astrovault.io");

            var applied = await sut.SetAccountIdAsync("acct-X", sut.ContextGeneration);

            Assert.That(applied, Is.True);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.None),
                "A matching accountId string on a DIFFERENT endpoint must be treated as foreign, not silently retained");
            mockQueueRepo.Verify(r => r.ClearBlockAsync(job.Id), Times.Once);
        }

        [Test]
        public async Task SetAccountIdAsync_SameAccountIdString_SameEndpoint_StillRetained()
        {
            // Counterpart regression: the endpoint-aware rule must not become OVER-eager -- same account,
            // same endpoint must still retain (already covered indirectly by
            // SetAccountIdAsync_SameAccountAsLiveBlock_KeepsBlock_NeverClears; this pins it explicitly
            // alongside the new different-endpoint test above for contrast).
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = "acct-X" }
            });

            var applied = await sut.SetAccountIdAsync("acct-X", sut.ContextGeneration);

            Assert.That(applied, Is.True);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded));
            mockQueueRepo.Verify(r => r.ClearBlockAsync(It.IsAny<Guid?>()), Times.Never);
        }

        // ====================================================================
        // Storage-quota plan directive E (C-P1#4a/4b): accountless blocks get upgraded once proven, and
        // EnterBlockAsync reads a generation-consistent identity snapshot.
        // ====================================================================

        [Test]
        public async Task SetAccountIdAsync_AccountlessLiveBlock_FingerprintAndEndpointMatch_UpgradesBlock_NeverClears()
        {
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None); // accountless (no cached accountId)
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = null, KeyFingerprint = "current-fp" }
            });
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("current-fp");
            mockQueueRepo.Setup(r => r.UpgradeBlockAccountIdAsync("acct-new")).ReturnsAsync(true);

            var applied = await sut.SetAccountIdAsync("acct-new", sut.ContextGeneration);

            Assert.That(applied, Is.True);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "An upgrade is not a clear -- the block stays latched");
            mockQueueRepo.Verify(r => r.UpgradeBlockAccountIdAsync("acct-new"), Times.Once);
            mockQueueRepo.Verify(r => r.ClearBlockAsync(It.IsAny<Guid?>()), Times.Never);
        }

        [Test]
        public async Task SetAccountIdAsync_AccountlessLiveBlock_FingerprintMismatch_ClearsDurably()
        {
            // D-verdict fix (P1 #2): a fingerprint/endpoint MISMATCH proven against a KNOWN current
            // identity means the accountless block is foreign -- the live path must honor the matcher's
            // verdict exactly as RestoreBlockStateAsync's startup reconciliation already does, instead of
            // retaining a proven-foreign block forever (the divergence the previous version of this test
            // enshrined: it asserted neither upgrade NOR clear happened here).
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = null, KeyFingerprint = "old-fp" }
            });
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("different-fp");
            mockQueueRepo.Setup(r => r.ClearBlockAsync(job.Id)).ReturnsAsync(true);

            var applied = await sut.SetAccountIdAsync("acct-new", sut.ContextGeneration);

            Assert.That(applied, Is.True);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.None),
                "A proven-foreign accountless block (fingerprint+endpoint mismatch against a KNOWN current identity) must be durably cleared, matching RestoreBlockStateAsync's behavior");
            mockQueueRepo.Verify(r => r.UpgradeBlockAccountIdAsync(It.IsAny<string>()), Times.Never);
            mockQueueRepo.Verify(r => r.ClearBlockAsync(job.Id), Times.Once);
        }

        [Test]
        public async Task AccountlessBlockUpgraded_ThenKeyRotatedOnSameAccount_RestoreBlockStateAsync_RetainsBlock_ViaUpgradedAccountId()
        {
            // End-to-end key-rotation-after-upgrade retention: an accountless block gets upgraded with
            // the account id once proven (fingerprint+endpoint match); a LATER key rotation on the SAME
            // account must then retain the block via the (now-upgraded) AccountId match -- not the
            // now-stale fingerprint -- proving the upgrade actually pays off across the rotation.
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None); // accountless block, key A's fingerprint

            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = null, KeyFingerprint = "key-A-fp" }
            });
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("key-A-fp"); // still key A -- proves the match
            mockQueueRepo.Setup(r => r.UpgradeBlockAccountIdAsync("acct-999")).ReturnsAsync(true);

            var applied = await sut.SetAccountIdAsync("acct-999", sut.ContextGeneration); // upgrades the block
            Assert.That(applied, Is.True);
            mockQueueRepo.Verify(r => r.UpgradeBlockAccountIdAsync("acct-999"), Times.Once, "Precondition: the block was upgraded");

            // Simulate the UPGRADED persisted state (what UpgradeBlockAccountIdAsync durably wrote) for
            // the subsequent restore, PLUS a key rotation: the CURRENT fingerprint (key B) differs from
            // the persisted one (key A), but the account id now matches.
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = "acct-999", KeyFingerprint = "key-A-fp" }
            });
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("key-B-fp-after-rotation");

            await sut.RestoreBlockStateAsync("acct-999");

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "Same account, rotated key -- the UPGRADED block must be retained via AccountId match, not cleared for a fingerprint mismatch");
            mockQueueRepo.Verify(r => r.ClearBlockAsync(It.IsAny<Guid?>()), Times.Never);
        }

        [Test]
        public async Task EnterBlockAsync_GenerationBumpDuringLatchTimeLookup_RejectsStaleAccountId_PersistsCurrentIdentity()
        {
            // Identity-race fix: the exact reviewed scenario, made deterministic via a
            // TaskCompletionSource seam (no timing sleeps) instead of the old test's purely SEQUENTIAL
            // SetAccountId calls (which never actually interleaved anything with the latch-time lookup's
            // await and so never exercised the race at all). Key A's bounded latch-time GetQuotaAsync
            // lookup is pending; key B is installed MID-FLIGHT (a generation bump); the lookup then
            // resolves with A's stale accountId. That result must be REJECTED -- the 507 block must
            // persist scoped to B's CURRENT identity, never A's stale one -- and B's own later quota
            // refresh (reporting the SAME account it already proved) must not durably clear its own
            // genuine block as foreign.
            var lookupGate = new TaskCompletionSource<QuotaUsage>();
            mockApiClient.Setup(c => c.GetQuotaAsync(It.IsAny<CancellationToken>())).Returns(lookupGate.Task);

            var job = TestDataFactory.CreateUploadJob();
            var enterTask = sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);

            // The lookup is now in flight, its generation captured before this point. Simulate key B
            // becoming active mid-flight (bumps the generation) with its own already-known account.
            sut.SetAccountId("acct-B");

            // The STALE lookup resolves AFTER the bump, reporting key A's account.
            lookupGate.SetResult(new QuotaUsage { AccountId = "acct-A-stale" });

            await enterTask;

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "Precondition: latched");
            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(),
                It.Is<UploadBlockIdentityInput>(i => i.AccountId == "acct-B")), Times.Once,
                "The stale lookup result (A) must be rejected -- the block must persist scoped to B's CURRENT identity, never A's");

            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = "acct-B" }
            });
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("current-fp");

            var applied = await sut.SetAccountIdAsync("acct-B", sut.ContextGeneration);

            Assert.That(applied, Is.True);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "B's genuine 507 must NOT be durably cleared by B's own later quota refresh");
            mockQueueRepo.Verify(r => r.ClearBlockAsync(It.IsAny<Guid?>()), Times.Never);
        }

        // ====================================================================
        // Hardening round F: re-validate contextGeneration after EVERY await inside identity-consuming
        // operations, immediately before the effect (the two remaining identity races). Layer 1 closes
        // the cheaply-closable windows (abort/retain instead of committing on stale data); Layer 2
        // self-heals the one window that cannot be closed without holding accountIdentityLock across an
        // await -- a context change landing DURING the repository commit itself.
        // ====================================================================

        [Test]
        public async Task ReconcileLiveBlockForAccountChangeLockedAsync_GenerationBumpDuringFingerprintAwait_AbortsEntirely_ThenFollowUpReconciliationSucceeds()
        {
            // Layer 1a scenario: an accountless block genuinely belongs to B (its
            // persisted KeyFingerprint IS B's current key's fingerprint). A stale SetAccountIdAsync call
            // (reporting some OTHER account) is mid-flight, its fingerprint compute (built from the apiKey
            // captured BEFORE the reinstall) about to resolve. While it is suspended, B is "reinstalled"
            // -- a direct SetAccountId call (accountIdentityLock only, never blocked by lifecycleLock)
            // bumps the generation. The stale call's eventual fingerprint result must never reach the
            // matcher/clear -- the WHOLE reconciliation must abort.
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None); // accountless block, B's fingerprint
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = null, KeyFingerprint = "key-B-fp" }
            });

            var fingerprintGate = new TaskCompletionSource<string>();
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).Returns(fingerprintGate.Task);

            var startingGeneration = sut.ContextGeneration;
            var reconcileTask = sut.SetAccountIdAsync("acct-stale", startingGeneration);

            // Reconciliation is now stalled awaiting the fingerprint. Simulate B's reinstall.
            sut.SetAccountId("acct-B");

            // The stale lookup finally resolves -- must never reach the matcher/clear regardless of what
            // it reports.
            fingerprintGate.SetResult("irrelevant-stale-fp");

            var applied = await reconcileTask;

            Assert.That(applied, Is.True, "TryApplyAccountIdForGeneration itself succeeded synchronously before the bump");
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "The stale reconciliation must ABORT entirely on a generation mismatch -- B's genuine block must survive untouched");
            mockQueueRepo.Verify(r => r.ClearBlockAsync(It.IsAny<Guid?>()), Times.Never);
            mockQueueRepo.Verify(r => r.UpgradeBlockAccountIdAsync(It.IsAny<string>()), Times.Never);

            // A follow-up reconciliation under B's CURRENT identity must still behave correctly -- proves
            // the abort only skipped THIS stale call, not reconciliation generally.
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("key-B-fp");
            mockQueueRepo.Setup(r => r.UpgradeBlockAccountIdAsync("acct-B")).ReturnsAsync(true);

            var followUpApplied = await sut.SetAccountIdAsync("acct-B", sut.ContextGeneration);

            Assert.That(followUpApplied, Is.True);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "Still latched -- an upgrade, not a clear");
            mockQueueRepo.Verify(r => r.UpgradeBlockAccountIdAsync("acct-B"), Times.Once,
                "The follow-up reconciliation under B's real identity must upgrade the accountless block, proving reconciliation recovers cleanly after the aborted stale attempt");
        }

        [Test]
        public async Task RestoreBlockStateAsync_GenerationBumpDuringFingerprintAwait_RetainsBlock_FailSafe_ThenFollowUpReconciliationSucceeds()
        {
            // Layer 1b: the same hazard on the restore path. A cached accountId is used as the fallback
            // (no explicit argument); the fingerprint compute straddles a concurrent context change. The
            // PRIOR fix only re-validated the accountId half of the pair, leaving a stale fingerprint
            // (captured from the apiKey read before the change) to decide the outcome via a PARTIAL
            // repair. This round retains the block wholesale instead of risking a mixed-identity verdict.
            sut.SetAccountId("acct-cached"); // seed a cached accountId + baseline generation

            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = "acct-cached", KeyFingerprint = "old-fp" }
            });

            var fingerprintGate = new TaskCompletionSource<string>();
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).Returns(fingerprintGate.Task);

            var restoreTask = sut.RestoreBlockStateAsync(); // no explicit accountId -- uses the cache

            // Context changes WHILE the fingerprint compute is in flight (e.g. a reinit / key rotation).
            sut.SetAccountId("acct-new");

            // Resolve with a fingerprint that would MISMATCH under the stale accountId assumption -- the
            // prior partial-reconcile logic would have durably cleared the block off this.
            fingerprintGate.SetResult("mismatched-fp");

            await restoreTask;

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "A generation change during the fingerprint await must RETAIN (fail-safe), never clear off partial data");
            mockQueueRepo.Verify(r => r.ClearBlockAsync(It.IsAny<Guid?>()), Times.Never);

            // A follow-up restore call under the now-stable identity must still behave correctly --
            // proves the retain only deferred repair, it did not disable it.
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("mismatched-fp");
            mockQueueRepo.Setup(r => r.ClearBlockAsync(null)).ReturnsAsync(true);

            await sut.RestoreBlockStateAsync();

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.None),
                "Once the context is stable, a fresh restore reconciliation correctly clears the now-proven-foreign block");
            mockQueueRepo.Verify(r => r.ClearBlockAsync(null), Times.Once);
        }

        [Test]
        public async Task EnterBlockAsync_GenerationBumpDuringCommit_SelfHealsAfterPersist_ThenExactlyOneReLatchAttempt_NoStorm()
        {
            // Layer 2 -- the irreducible residue: the ONE window Layer 1 cannot close without holding
            // accountIdentityLock across an await (forbidden) is a context change landing DURING
            // SetBlockAsync's own commit. Gate the commit with a TCS, bump the generation (a genuine
            // account switch) while it is in flight, and verify the just-persisted (now-foreign) block
            // gets self-healed: cleared, then re-attempted exactly once, then correctly re-latched.
            sut.SetAccountId("acct-old"); // the identity in place when the latch begins

            var job = TestDataFactory.CreateUploadJob();
            var commitGate = new TaskCompletionSource<bool>();
            mockQueueRepo.Setup(r => r.SetBlockAsync(
                    It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(), It.IsAny<UploadBlockIdentityInput>()))
                .Returns(commitGate.Task);

            var enterTask = sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);

            // The commit is in flight -- the identity being persisted is "acct-old" (captured before this
            // bump). A genuine account switch happens WHILE the commit is still pending on disk.
            sut.SetAccountId("acct-new");

            // Wire what the self-heal reconciliation will need BEFORE resolving the commit -- its own
            // lock/read chain can run inline with EnterBlockAsync's continuation once the gate resolves,
            // so everything it touches must already be configured.
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = "acct-old" }
            });
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("current-fp");
            mockQueueRepo.Setup(r => r.ClearBlockAsync(job.Id)).ReturnsAsync(true);

            // The commit now lands (as if it reached disk moments after the switch). Note: the self-heal
            // below is scheduled but NOT awaited by EnterBlockAsync, yet its own lock acquisition and the
            // fully-mocked (non-TCS-gated) reconciliation chain can complete SYNCHRONOUSLY inline with
            // this continuation -- so by the time `enterTask` completes, the "entered, then self-healed"
            // cycle may already be over. Assert on invocations recorded (timing-independent), not on an
            // intermediate in-memory state that is not guaranteed to be observable.
            commitGate.SetResult(true);
            await enterTask;

            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(),
                It.Is<UploadBlockIdentityInput>(i => i.AccountId == "acct-old")), Times.Once,
                "Precondition: the just-persisted block carries the PRE-bump identity -- the race landed during the commit itself, the one window Layer 1 cannot close");
            Assert.That(sut.LastSelfHealTaskForTests, Is.Not.Null,
                "A generation change detected after the commit must schedule a self-heal (fire-and-forget from EnterBlockAsync's perspective)");

            await sut.LastSelfHealTaskForTests; // deterministic -- no sleeps, whether it ran inline or not

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.None),
                "The self-heal must reconcile the just-persisted block against the NEW current identity and clear it (foreign per the matcher)");
            mockQueueRepo.Verify(r => r.ClearBlockAsync(job.Id), Times.Once);

            // Worst-case user-visible effect: EXACTLY one subsequent upload attempt, then a correct
            // re-latch under the NEW identity -- never a retry storm, never a lost frame.
            mockQueueRepo.Setup(r => r.PeekAsync()).ReturnsAsync(job);
            mockApiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(QuotaFail()); // still over quota under the new account too

            var reAttempted = await sut.ProcessNextJobAsync(CancellationToken.None);
            var afterLatchProcessed = await sut.ProcessNextJobAsync(CancellationToken.None);

            Assert.That(reAttempted, Is.True, "The cleared block lets the pinned job through for exactly one probe attempt");
            Assert.That(afterLatchProcessed, Is.False, "Once re-latched, subsequent ticks report no work done -- no storm");
            mockApiClient.Verify(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once,
                "Exactly ONE HTTP attempt for the re-probe -- no storm");
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "Re-latched, this time scoped to the CURRENT (acct-new) identity");
            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(),
                It.Is<UploadBlockIdentityInput>(i => i.AccountId == "acct-new")), Times.Once,
                "The re-latch must persist under acct-new -- the correct re-latch the invariant promises");
        }

        [Test]
        public async Task EnterBlockAsync_GenerationBumpDuringIdentityBuild_BeforeCommit_StillSchedulesSelfHeal()
        {
            // Pass F regression: the self-heal baseline must be the generation OBSERVED by the reconcile
            // decision, never a later re-read. A bump landing BETWEEN the reconcile and the SetBlockAsync
            // commit -- injected here from the mocked GetApiKey read, which sits exactly in that gap --
            // previously became the new baseline, so the post-commit equality check saw no movement and
            // the stale-identity block was latched with NO bounded repair. It must now self-heal.
            sut.SetAccountId("acct-old");

            var job = TestDataFactory.CreateUploadJob();

            // The genuine account switch fires deterministically from INSIDE the identity build (once).
            var bumped = false;
            mockAuthManager.Setup(a => a.GetApiKey()).Returns(() =>
            {
                if (!bumped)
                {
                    bumped = true;
                    sut.SetAccountId("acct-new");
                }
                return "test-api-key";
            });

            mockQueueRepo.Setup(r => r.SetBlockAsync(
                    It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(), It.IsAny<UploadBlockIdentityInput>()))
                .ReturnsAsync(true);

            // Everything the self-heal reconciliation touches must be configured up front -- it can run
            // inline with EnterBlockAsync's continuation (see the DuringCommit test above).
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id,
                Identity = new UploadBlockIdentity { EndpointUrl = "https://api.astrovault.io", AccountId = "acct-old" }
            });
            mockQueueRepo.Setup(r => r.ComputeIdentityFingerprintAsync("test-api-key")).ReturnsAsync("current-fp");
            mockQueueRepo.Setup(r => r.ClearBlockAsync(job.Id)).ReturnsAsync(true);

            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);

            mockQueueRepo.Verify(r => r.SetBlockAsync(
                It.IsAny<UploadBlockKind>(), It.IsAny<Guid>(), It.IsAny<UploadBlockSnapshot>(),
                It.Is<UploadBlockIdentityInput>(i => i.AccountId == "acct-old")), Times.Once,
                "Precondition: the persisted identity is the PRE-bump one -- the race landed in the reconcile-to-commit gap");
            Assert.That(sut.LastSelfHealTaskForTests, Is.Not.Null,
                "The bump preceded the commit but FOLLOWED the reconcile observation -- it must register against the observed baseline and schedule a self-heal");

            await sut.LastSelfHealTaskForTests;

            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.None),
                "The self-heal reconciles the stale-identity block against the CURRENT (acct-new) identity and clears it as foreign");
            mockQueueRepo.Verify(r => r.ClearBlockAsync(job.Id), Times.Once);
        }

        // ====================================================================
        // ResumeAsync -- basic contract
        // ====================================================================

        [Test]
        public async Task ResumeAsync_NotBlocked_ReturnsTrue_NoOp()
        {
            var result = await sut.ResumeAsync();

            Assert.That(result, Is.True);
            mockQueueRepo.Verify(r => r.ClearBlockAsync(It.IsAny<Guid?>()), Times.Never);
        }

        [Test]
        public async Task ResumeAsync_QuotaBlocked_ClearsDurably_PinsFailedJob_ReleasesLatch()
        {
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "Precondition: latched");

            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState
            {
                Kind = UploadBlockKind.QuotaExceeded,
                FailedJobId = job.Id
            });
            mockQueueRepo.Setup(r => r.ClearBlockAsync(job.Id)).ReturnsAsync(true);

            var result = await sut.ResumeAsync();

            Assert.That(result, Is.True);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.None), "The in-memory latch must be released");
            Assert.That(sut.HasBlockPersistFailureWarning, Is.False);
            mockQueueRepo.Verify(r => r.ClearBlockAsync(job.Id), Times.Once,
                "The job that triggered the block must be pinned, read from the repository's persisted state");
        }

        [Test]
        public async Task ResumeAsync_AuthBlocked_AlsoClears_KindAgnostic()
        {
            // ResumeAsync itself is kind-agnostic -- the KIND GATE (AuthInvalid only clearable via a
            // validated key) is the CALLER's responsibility (AstrovaultPlugin), not this primitive's.
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, UploadResult.Fail("Invalid API key", 401, FailureKind.PermanentAuth), CancellationToken.None);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.AuthInvalid));

            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState { Kind = UploadBlockKind.AuthInvalid, FailedJobId = job.Id });
            mockQueueRepo.Setup(r => r.ClearBlockAsync(job.Id)).ReturnsAsync(true);

            var result = await sut.ResumeAsync();

            Assert.That(result, Is.True);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.None));
        }

        [Test]
        public async Task ResumeAsync_NoPersistedFailedJobId_ClearsWithNullPin()
        {
            // A block restored from a pre-Step-5 v3 file (FailedJobId absent/null) must still resume --
            // just with no pin (falls back to normal drain order).
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);

            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState { Kind = UploadBlockKind.QuotaExceeded, FailedJobId = null });
            mockQueueRepo.Setup(r => r.ClearBlockAsync(null)).ReturnsAsync(true);

            var result = await sut.ResumeAsync();

            Assert.That(result, Is.True);
            mockQueueRepo.Verify(r => r.ClearBlockAsync(null), Times.Once);
        }

        [Test]
        public async Task ResumeAsync_DurableCommitFails_StaysLatched_SetsPersistFailureWarning_AndNotifiesUi()
        {
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);
            var snapshotBefore = sut.BlockSnapshot;

            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState { Kind = UploadBlockKind.QuotaExceeded, FailedJobId = job.Id });
            mockQueueRepo.Setup(r => r.ClearBlockAsync(job.Id)).ReturnsAsync(false);

            var queueStateChangedRaised = false;
            sut.QueueStateChanged += (s, e) => queueStateChangedRaised = true;
            UploadBlockedEventArgs captured = null;
            sut.UploadBlocked += (s, e) => captured = e;

            var result = await sut.ResumeAsync();

            Assert.That(result, Is.False);
            Assert.That(sut.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "Fail-safe: must stay latched when the commit fails");
            Assert.That(sut.BlockSnapshot, Is.EqualTo(snapshotBefore), "The snapshot must be preserved, not wiped, on a failed clear");
            Assert.That(sut.HasBlockPersistFailureWarning, Is.True);
            // Storage-quota plan directive H (B#2): a failed ClearBlockAsync is still a live transition
            // that must reach the UI -- both events fire, marked persist-failure, so IsUploading ends and
            // the warning banner shows instead of the old silent no-op.
            Assert.That(queueStateChangedRaised, Is.True, "A failed resume commit is still a live transition -- the UI must be notified");
            Assert.That(captured, Is.Not.Null, "The typed event must also fire so the block banner stays visible");
            Assert.That(captured.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded));
            Assert.That(captured.IsPersistFailure, Is.True);
        }

        [Test]
        public async Task ResumeAsync_Success_RaisesQueueStateChanged()
        {
            var job = TestDataFactory.CreateUploadJob();
            await sut.HandleUploadFailure(job, QuotaFail(), CancellationToken.None);
            mockQueueRepo.Setup(r => r.CurrentBlock).Returns(new UploadBlockState { Kind = UploadBlockKind.QuotaExceeded, FailedJobId = job.Id });
            mockQueueRepo.Setup(r => r.ClearBlockAsync(job.Id)).ReturnsAsync(true);

            var raised = false;
            sut.QueueStateChanged += (s, e) => raised = true;

            await sut.ResumeAsync();

            Assert.That(raised, Is.True);
        }

        // ====================================================================
        // Row 13: pinned job attempted first after resume, under sustained enqueue.
        // ====================================================================

        [Test]
        public async Task ResumeAsync_ThenProcessNextJobAsync_AttemptsPinnedJobFirst_RealRepository()
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

                var blockedJob = TestDataFactory.CreateUploadJob(Path.Combine(tempDir, "blocked.fits"));
                File.WriteAllText(blockedJob.LocalPath, "x");
                await repo.EnqueueAsync(blockedJob);

                apiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(QuotaFail());
                await manager.ProcessNextJobAsync(CancellationToken.None); // latches on blockedJob
                Assert.That(manager.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "Precondition: latched");

                // Sustained capture while blocked: more frames keep arriving, and (LIFO default) would
                // normally be drained BEFORE the older blockedJob.
                var newerJob1 = TestDataFactory.CreateUploadJob(Path.Combine(tempDir, "newer1.fits"));
                File.WriteAllText(newerJob1.LocalPath, "x");
                await repo.EnqueueAsync(newerJob1);
                var newerJob2 = TestDataFactory.CreateUploadJob(Path.Combine(tempDir, "newer2.fits"));
                File.WriteAllText(newerJob2.LocalPath, "x");
                await repo.EnqueueAsync(newerJob2);

                apiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(UploadResult.Ok("https://cdn/x"));

                var resumed = await manager.ResumeAsync();
                Assert.That(resumed, Is.True);

                var attemptedJobs = new System.Collections.Generic.List<Guid>();
                apiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                    .Returns((UploadJob j, IProgress<double> p, CancellationToken ct) =>
                    {
                        attemptedJobs.Add(j.Id);
                        return Task.FromResult(UploadResult.Ok("https://cdn/x"));
                    });

                await manager.ProcessNextJobAsync(CancellationToken.None);

                Assert.That(attemptedJobs, Has.Count.EqualTo(1));
                Assert.That(attemptedJobs[0], Is.EqualTo(blockedJob.Id),
                    "The job that triggered the block must be attempted FIRST after resume, ahead of newer captures");
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        // ====================================================================
        // Resume -> server still says 507: re-latch after exactly one attempt, no storm.
        // ====================================================================

        [Test]
        public async Task ResumeAsync_ServerStillReturns507_RelatchesAfterExactlyOneAttempt_NoStorm()
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
                File.WriteAllText(job.LocalPath, "x");
                await repo.EnqueueAsync(job);

                apiClient.Setup(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(QuotaFail());

                await manager.ProcessNextJobAsync(CancellationToken.None); // first latch
                Assert.That(manager.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded));

                await manager.ResumeAsync(); // still returns 507 -- QuotaFail is still the setup

                apiClient.Invocations.Clear();
                var reProbeProcessed = await manager.ProcessNextJobAsync(CancellationToken.None);
                var afterLatchProcessed = await manager.ProcessNextJobAsync(CancellationToken.None);

                Assert.That(reProbeProcessed, Is.True, "The resume probe tick attempts the pinned job and re-latches");
                Assert.That(afterLatchProcessed, Is.False, "Once re-latched, subsequent ticks report no work done");
                Assert.That(manager.BlockKind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "Re-latched after the server still said 507");
                apiClient.Verify(c => c.UploadFileAsync(It.IsAny<UploadJob>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Once,
                    "Exactly ONE HTTP attempt for the resume probe -- no storm");

                var pending = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
                Assert.That(pending.Select(j => j.Id), Does.Contain(job.Id), "The re-latched job stays Pending, never Failed");
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private static string MakeTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "upload_manager_resume_" + Guid.NewGuid().ToString("N"));
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
