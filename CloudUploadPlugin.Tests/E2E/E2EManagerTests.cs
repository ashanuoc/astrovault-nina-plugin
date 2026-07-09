using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Core;
using Astrovault.Data;
using Astrovault.Interfaces;
using Astrovault.Models;
using CloudUploadPlugin.Tests.E2E.Fixtures;
using CloudUploadPlugin.Tests.E2E.Helpers;
using NUnit.Framework;

namespace CloudUploadPlugin.Tests.E2E
{
    [TestFixture]
    [Category("E2E")]
    public class E2EManagerTests : E2ETestBase
    {
        // Inherits from E2ETestBase:
        //   - Fixture (MockServerFixture.Instance)
        //   - TempDir (per-test temp directory)
        //   - [SetUp] BaseSetUp() -- fixture init, temp dir, ResetServerState
        //   - [TearDown] BaseTearDown() -- DumpLogsOnFailure, temp dir cleanup
        //   - CreateUploadJob(filePath, fileSize) helper

        [Test]
        public async Task CircuitBreaker_After5ConsecutiveFailures_OpensCircuit()
        {
            // Arrange: enable reject_uploads error mode
            await Fixture.SetErrorMode("reject_uploads", true);

            var authStub = new TestAuthStub();
            var queueRepo = new UploadQueueRepository(TempDir);
            var apiClient = new AstrovaultApiClient(authStub, Fixture.BaseUrl);
            var manager = new UploadManager(queueRepo, apiClient, authStub);

            // Verify circuit starts closed
            Assert.That(manager.IsCircuitOpen, Is.False, "Circuit breaker should start closed");

            // Act: trigger 6 consecutive failures (threshold is 5)
            for (int i = 0; i < 6; i++)
            {
                var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.SmallFileSize, $"fail-{i}.bin");
                var job = CreateUploadJob(filePath, E2ETestFileHelper.SmallFileSize);

                var result = await apiClient.UploadFileAsync(job, new Progress<double>(), CancellationToken.None);
                Assert.That(result.Success, Is.False, $"Upload {i} should fail with reject_uploads enabled");

                // Feed failure through UploadManager to update circuit breaker
                await manager.HandleUploadFailure(job, result, CancellationToken.None);
            }

            // Assert: circuit breaker should now be open
            Assert.That(manager.IsCircuitOpen, Is.True, "Circuit breaker should be open after 6 consecutive failures");

            // Cleanup
            apiClient.Dispose();
        }

        [Test]
        public async Task QueuePersistence_JobSurvivesManagerRestart_UploadsSuccessfully()
        {
            var authStub = new TestAuthStub();
            var queueDir = Path.Combine(TempDir, "queue");
            Directory.CreateDirectory(queueDir);

            // Phase 1: Create a job and enqueue it, then stop the manager WITHOUT processing
            var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.SmallFileSize);
            var job = CreateUploadJob(filePath, E2ETestFileHelper.SmallFileSize);

            var queueRepo1 = new UploadQueueRepository(queueDir);
            var apiClient1 = new AstrovaultApiClient(authStub, Fixture.BaseUrl);
            var manager1 = new UploadManager(queueRepo1, apiClient1, authStub);

            // Enqueue the job (but do NOT start the manager -- job is persisted but not processed)
            await manager1.EnqueueJobAsync(job);

            // Verify job is persisted
            var pendingCount = await queueRepo1.GetJobCountAsync(UploadStatus.Pending);
            Assert.That(pendingCount, Is.EqualTo(1), "Job should be persisted in queue");

            // "Stop" the manager (simulate crash by just discarding it)
            apiClient1.Dispose();

            // Phase 2: Create a NEW UploadQueueRepository + UploadManager pointing at same directory
            var queueRepo2 = new UploadQueueRepository(queueDir);
            var apiClient2 = new AstrovaultApiClient(authStub, Fixture.BaseUrl);
            var manager2 = new UploadManager(queueRepo2, apiClient2, authStub);

            // Verify the persisted job is still there
            await manager2.InitializeCountsAsync();
            Assert.That(manager2.PendingCount, Is.EqualTo(1), "New manager should see the persisted pending job");

            // Start the manager and wait for upload to complete
            var uploadCompleted = new TaskCompletionSource<UploadCompletedEventArgs>();
            manager2.UploadCompleted += (sender, args) => uploadCompleted.TrySetResult(args);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await manager2.StartAsync(cts.Token);

            // Wait for upload completion (with timeout)
            var completedTask = await Task.WhenAny(uploadCompleted.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.That(completedTask, Is.EqualTo(uploadCompleted.Task), "Upload should complete within 15 seconds");

            var completedArgs = await uploadCompleted.Task;
            Assert.That(completedArgs.Success, Is.True, $"Upload should succeed. Error: {completedArgs.ErrorMessage}");

            await manager2.StopAsync();
            apiClient2.Dispose();
        }

        /// <summary>
        /// TC-16 auto-recovery regression (REL-06/REL-07, MAINT-02). Drives a sustained outage so the
        /// circuit breaker Opens and every job reaches Failed (retry budget exhausted under transient
        /// faults), then restores the server and asserts the queue auto-drains to success WITHOUT any
        /// manual RetryAll call -- the breaker probes (HalfOpen), promotes the transient-Failed jobs,
        /// and Closes on its own.
        ///
        /// Deterministic: the breaker's Open->HalfOpen transition is driven by a short recovery-timeout
        /// test seam (~300ms), NOT a real ~60s wall-clock sleep, so the test runs in a few seconds.
        ///
        /// Before the Task 2 fix this is RED: PeekAsync returns null once all jobs are Failed, no probe
        /// ever fires, and the breaker dead-ends in HalfOpen forever (the original TC-16 dead-end).
        /// </summary>
        [Test]
        [Category("Recovery")]
        public async Task CircuitBreaker_AfterSustainedOutage_AutoRecoversWithoutManualRetry()
        {
            var authStub = new TestAuthStub();
            var queueDir = Path.Combine(TempDir, "queue");
            Directory.CreateDirectory(queueDir);

            var queueRepo = new UploadQueueRepository(queueDir);
            var apiClient = new AstrovaultApiClient(authStub, Fixture.BaseUrl);
            var manager = new UploadManager(queueRepo, apiClient, authStub);

            // Deterministic breaker recovery: probe ~300ms after Open instead of the real 60s.
            manager.SetCircuitBreakerRecoveryTimeoutForTest(TimeSpan.FromMilliseconds(300));

            const int fileCount = 3;
            var jobIds = new List<Guid>();

            // Track terminal successes (auto-recovery target).
            var successfulJobs = new HashSet<Guid>();
            var allRecovered = new TaskCompletionSource<bool>();
            manager.UploadCompleted += (sender, args) =>
            {
                if (!args.Success) return;
                lock (successfulJobs)
                {
                    successfulJobs.Add(args.JobId);
                    if (successfulJobs.Count >= fileCount)
                        allRecovered.TrySetResult(true);
                }
            };

            // --- Set up the TC-16 dead-end state deterministically (no organic-timing flake) ---
            // A sustained outage drives every job to terminal transient-Failed and Opens the breaker.
            // We reproduce that end state directly via the production failure path: enqueue the jobs,
            // then feed each one MaxRetries transient failures so it exhausts its budget and is marked
            // terminal Failed, which also Opens the breaker after 5 consecutive failures. This is exactly
            // the state a 60s outage leaves: ALL jobs Failed + breaker Open = PeekAsync returns null =
            // the original HalfOpen dead-end with no probe.
            await Fixture.SetErrorMode("reject_uploads", true);
            for (int i = 0; i < fileCount; i++)
            {
                var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.SmallFileSize, $"recover-{i}.bin");
                var job = CreateUploadJob(filePath, E2ETestFileHelper.SmallFileSize);
                jobIds.Add(job.Id);
                await manager.EnqueueJobAsync(job);

                // Exhaust the per-job retry budget so the job reaches terminal (transient) Failed.
                var live = (await queueRepo.GetJobsByStatusAsync(UploadStatus.Pending))
                    .First(j => j.Id == job.Id);
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    live.NextRetryAfter = null; // ignore backoff window; we are exhausting the budget
                    await manager.HandleUploadFailure(live, UploadResult.Fail("Network timeout"), CancellationToken.None);
                    if (live.Status == UploadStatus.Failed) break;
                }
            }

            Assert.That(await queueRepo.GetJobCountAsync(UploadStatus.Failed), Is.EqualTo(fileCount),
                "All jobs should have reached terminal Failed under the sustained outage");
            Assert.That(manager.IsCircuitOpen, Is.True,
                "Circuit breaker should be Open after the sustained outage");

            // Restore the server. NO manual RetryAll -- start the processing loop and the breaker must
            // auto-recover on its own probe: HalfOpen -> promote transient-Failed -> upload -> Close.
            await Fixture.SetErrorMode("reject_uploads", false);
            await manager.InitializeCountsAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await manager.StartAsync(cts.Token);

            var recoveredTask = await Task.WhenAny(allRecovered.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.That(recoveredTask, Is.EqualTo(allRecovered.Task),
                $"Breaker must auto-recover (no manual Retry All): only {successfulJobs.Count}/{fileCount} jobs uploaded after the outage cleared. " +
                "This is the TC-16 dead-end -- HalfOpen with all-Failed jobs never probes.");

            Assert.That(successfulJobs.Count, Is.EqualTo(fileCount),
                "Every previously-Failed job should auto-upload once the breaker recovers");
            Assert.That(manager.IsCircuitOpen, Is.False,
                "Circuit breaker should be Closed after auto-recovery");

            await manager.StopAsync();
            apiClient.Dispose();
        }

        [Test]
        public async Task BulkQueueProcessing_MultipleFiles_AllCompleteSequentially()
        {
            const int fileCount = 5;

            var authStub = new TestAuthStub();
            var queueDir = Path.Combine(TempDir, "queue");
            Directory.CreateDirectory(queueDir);

            var queueRepo = new UploadQueueRepository(queueDir);
            var apiClient = new AstrovaultApiClient(authStub, Fixture.BaseUrl);
            var manager = new UploadManager(queueRepo, apiClient, authStub);

            // Track completions
            var completedJobs = new List<UploadCompletedEventArgs>();
            var allDone = new TaskCompletionSource<bool>();
            manager.UploadCompleted += (sender, args) =>
            {
                lock (completedJobs)
                {
                    completedJobs.Add(args);
                    if (completedJobs.Count >= fileCount)
                        allDone.TrySetResult(true);
                }
            };

            // Start the manager
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await manager.StartAsync(cts.Token);

            // Rapidly enqueue multiple files
            for (int i = 0; i < fileCount; i++)
            {
                var filePath = E2ETestFileHelper.CreateTestFile(TempDir, E2ETestFileHelper.SmallFileSize, $"stress-{i}.bin");
                var enqueueJob = CreateUploadJob(filePath, E2ETestFileHelper.SmallFileSize);
                await manager.EnqueueJobAsync(enqueueJob);
            }

            // Wait for all uploads to complete (with timeout)
            var completedTask = await Task.WhenAny(allDone.Task, Task.Delay(TimeSpan.FromSeconds(45)));
            Assert.That(completedTask, Is.EqualTo(allDone.Task), $"All {fileCount} uploads should complete within 45s. Only {completedJobs.Count}/{fileCount} completed.");

            // Verify all succeeded
            Assert.That(completedJobs.Count, Is.EqualTo(fileCount), $"Expected {fileCount} completions");
            foreach (var completed in completedJobs)
            {
                Assert.That(completed.Success, Is.True, $"Job {completed.JobId} failed: {completed.ErrorMessage}");
            }

            await manager.StopAsync();
            apiClient.Dispose();
        }
    }
}
