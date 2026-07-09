using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Astrovault.Data;
using Astrovault.Models;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Repository-level tests for queue hardening behaviors.
    /// Uses real UploadQueueRepository with temp directory to test
    /// atomic save, LIFO ordering, retry/dismiss, timestamps, purge,
    /// stale detection, and persistence round-trip.
    /// </summary>
    [TestFixture]
    [Category("QueueHardening")]
    public class QueueHardeningTests
    {
        private string tempDir = null!;

        [SetUp]
        public void Setup()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "queue_hardening_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { /* best effort cleanup */ }
        }

        [Test]
        public async Task AtomicSave_WritesTmpThenRenames()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();

            await repo.EnqueueAsync(job);

            var queueDat = Path.Combine(tempDir, "queue.dat");
            var queueTmp = Path.Combine(tempDir, "queue.dat.tmp");

            Assert.That(File.Exists(queueDat), Is.True, "queue.dat should exist after enqueue");
            Assert.That(File.Exists(queueTmp), Is.False, "queue.dat.tmp should NOT exist after atomic save completes");
        }

        [Test]
        public void StaleTmpCleanup_DeletedOnLoad()
        {
            // Create a stale .tmp file before constructing the repository
            var tmpPath = Path.Combine(tempDir, "queue.dat.tmp");
            File.WriteAllText(tmpPath, "stale data");

            Assert.That(File.Exists(tmpPath), Is.True, "Precondition: .tmp exists");

            // Constructing repository should clean it up during LoadQueueFromDisk
            var repo = new UploadQueueRepository(tempDir);

            Assert.That(File.Exists(tmpPath), Is.False, "Stale .tmp should be deleted on repository load");
        }

        [Test]
        public async Task NewCapturePriority_NewestFirst()
        {
            var repo = new UploadQueueRepository(tempDir);

            // Enqueue 3 jobs with different QueuedAt timestamps
            var oldest = CreatePendingJob();
            oldest.QueuedAt = DateTime.UtcNow.AddHours(-3);
            await repo.EnqueueAsync(oldest);

            var middle = CreatePendingJob();
            middle.QueuedAt = DateTime.UtcNow.AddHours(-2);
            await repo.EnqueueAsync(middle);

            var newest = CreatePendingJob();
            newest.QueuedAt = DateTime.UtcNow.AddHours(-1);
            await repo.EnqueueAsync(newest);

            // PeekAsync should return the newest (LIFO)
            var peeked = await repo.PeekAsync();
            Assert.That(peeked, Is.Not.Null);
            // Note: EnqueueAsync sets QueuedAt to DateTime.UtcNow, so we need to
            // verify the newest enqueued job is returned (last one enqueued = most recent QueuedAt)
            Assert.That(peeked!.Id, Is.EqualTo(newest.Id),
                "PeekAsync should return the newest job (LIFO priority)");
        }

        [Test]
        public async Task RetryJob_ResetsAllState()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);

            // Transition to Failed
            await repo.UpdateJobStatusAsync(job.Id, UploadStatus.Failed, "Network timeout");

            // Add some chunk state
            await repo.UpdateJobChunkStateAsync(job.Id, "session-123",
                new List<int> { 0, 1, 2 }, 5);

            // Retry the job
            await repo.RetryJobAsync(job.Id);

            // Verify full state reset
            var jobs = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
            Assert.That(jobs, Has.Count.EqualTo(1));

            var retried = jobs[0];
            Assert.That(retried.Status, Is.EqualTo(UploadStatus.Pending));
            Assert.That(retried.RetryCount, Is.EqualTo(0));
            Assert.That(retried.ErrorMessage, Is.Null);
            Assert.That(retried.FailedAt, Is.Null);
            Assert.That(retried.ChunksSent, Is.Empty);
        }

        [Test]
        public async Task RetryJob_IgnoresNonFailed()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);

            // Job is Pending, not Failed -- RetryJobAsync should do nothing
            await repo.RetryJobAsync(job.Id);

            var pending = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
            Assert.That(pending, Has.Count.EqualTo(1));
            Assert.That(pending[0].Status, Is.EqualTo(UploadStatus.Pending));
        }

        [Test]
        public async Task DismissFailed_RemovesOnlyFailed()
        {
            var repo = new UploadQueueRepository(tempDir);

            var pending = CreatePendingJob();
            await repo.EnqueueAsync(pending);

            var failed = CreatePendingJob();
            await repo.EnqueueAsync(failed);
            await repo.UpdateJobStatusAsync(failed.Id, UploadStatus.Failed, "error");

            var completed = CreatePendingJob();
            await repo.EnqueueAsync(completed);
            await repo.UpdateJobStatusAsync(completed.Id, UploadStatus.Completed);

            // Dismiss failed
            await repo.RemoveFailedJobsAsync();

            // Should have 2 remaining (pending + completed)
            var pendingJobs = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
            var completedJobs = (await repo.GetJobsByStatusAsync(UploadStatus.Completed)).ToList();
            var failedJobs = (await repo.GetJobsByStatusAsync(UploadStatus.Failed)).ToList();

            Assert.That(pendingJobs, Has.Count.EqualTo(1));
            Assert.That(completedJobs, Has.Count.EqualTo(1));
            Assert.That(failedJobs, Has.Count.EqualTo(0));
        }

        [Test]
        public async Task CompletedTimestamp_SetOnTransition()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);

            var before = DateTime.UtcNow;
            await repo.UpdateJobStatusAsync(job.Id, UploadStatus.Completed);
            var after = DateTime.UtcNow;

            var completed = (await repo.GetJobsByStatusAsync(UploadStatus.Completed)).First();
            Assert.That(completed.CompletedAt, Is.Not.Null);
            Assert.That(completed.CompletedAt!.Value, Is.GreaterThanOrEqualTo(before));
            Assert.That(completed.CompletedAt!.Value, Is.LessThanOrEqualTo(after));
        }

        [Test]
        public async Task FailedTimestamp_SetOnTransition()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);

            var before = DateTime.UtcNow;
            await repo.UpdateJobStatusAsync(job.Id, UploadStatus.Failed, "timeout");
            var after = DateTime.UtcNow;

            var failed = (await repo.GetJobsByStatusAsync(UploadStatus.Failed)).First();
            Assert.That(failed.FailedAt, Is.Not.Null);
            Assert.That(failed.FailedAt!.Value, Is.GreaterThanOrEqualTo(before));
            Assert.That(failed.FailedAt!.Value, Is.LessThanOrEqualTo(after));
        }

        [Test]
        public async Task CompletedPurge_EnforcesLimit()
        {
            var repo = new UploadQueueRepository(tempDir);

            // Enqueue 55 jobs and mark all as Completed
            for (int i = 0; i < 55; i++)
            {
                var job = CreatePendingJob();
                await repo.EnqueueAsync(job);
                await repo.UpdateJobStatusAsync(job.Id, UploadStatus.Completed);
            }

            // Only 50 completed should remain (purge oldest)
            var completed = (await repo.GetCompletedJobsAsync()).ToList();
            Assert.That(completed.Count, Is.EqualTo(50),
                "Should retain at most 50 completed jobs");
        }

        [Test]
        public async Task CompletedTotal_ClimbsPastRetentionCap_AndPersistsAcrossReload()
        {
            // REGRESSION (manual-UAT bug): the "Completed" counter oscillated at the 50-job retention
            // cap (50 -> 51 -> reset 50 -> 51 ...) because it was sourced from GetJobCountAsync(Completed),
            // which only counts RETAINED records. CompletedTotal is the lifetime running total, decoupled
            // from the cap, and must keep climbing AND survive a restart.
            var repo = new UploadQueueRepository(tempDir);

            for (int i = 0; i < 52; i++)
            {
                var job = CreatePendingJob();
                await repo.EnqueueAsync(job);
                await repo.UpdateJobStatusAsync(job.Id, UploadStatus.Completed);
            }

            Assert.That(repo.CompletedTotal, Is.EqualTo(52),
                "Lifetime CompletedTotal must climb past the 50-record retention cap");
            Assert.That(await repo.GetJobCountAsync(UploadStatus.Completed), Is.EqualTo(50),
                "Retained completed records stay capped at 50 (storage hygiene unchanged)");

            // Reload from disk -> the persisted total must survive (not reset to the retained count).
            var reloaded = new UploadQueueRepository(tempDir);
            Assert.That(reloaded.CompletedTotal, Is.EqualTo(52),
                "CompletedTotal must persist across restart, not reset to the retained-record count");
        }

        [Test]
        public async Task CompletedTotal_NotDoubleCountedWhenStatusReapplied()
        {
            // Idempotency: marking an already-Completed job Completed again must NOT re-bump the total.
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);

            await repo.UpdateJobStatusAsync(job.Id, UploadStatus.Completed);
            await repo.UpdateJobStatusAsync(job.Id, UploadStatus.Completed);

            Assert.That(repo.CompletedTotal, Is.EqualTo(1),
                "Re-applying Completed to the same job must not double-count the running total");
        }

        [Test]
        public async Task ResetCountsAsync_ZeroesHistoricalCounters_PreservesLiveJobs_AndStaysResetAcrossReload()
        {
            var repo = new UploadQueueRepository(tempDir);

            // A Pending job and an InProgress job: both are LIVE state and must survive the reset.
            var pending = CreatePendingJob();
            await repo.EnqueueAsync(pending);
            var inProgress = CreatePendingJob();
            await repo.EnqueueAsync(inProgress);
            await repo.UpdateJobStatusAsync(inProgress.Id, UploadStatus.InProgress);

            // 5 completed (history) + 2 failed (history).
            for (int i = 0; i < 5; i++)
            {
                var job = CreatePendingJob();
                await repo.EnqueueAsync(job);
                await repo.UpdateJobStatusAsync(job.Id, UploadStatus.Completed);
            }
            for (int i = 0; i < 2; i++)
            {
                var job = CreatePendingJob();
                await repo.EnqueueAsync(job);
                await repo.UpdateJobStatusAsync(job.Id, UploadStatus.Failed);
            }
            Assert.That(repo.CompletedTotal, Is.EqualTo(5), "Precondition: 5 completed");
            Assert.That(await repo.GetJobCountAsync(UploadStatus.Failed), Is.EqualTo(2), "Precondition: 2 failed");

            await repo.ResetCountsAsync();

            // Historical counters cleared.
            Assert.That(repo.CompletedTotal, Is.EqualTo(0), "Reset must zero the running total");
            Assert.That(await repo.GetJobCountAsync(UploadStatus.Completed), Is.EqualTo(0),
                "Reset must clear retained completed records (so the on-load clamp can't restore the total)");
            Assert.That(await repo.GetJobCountAsync(UploadStatus.Failed), Is.EqualTo(0),
                "Reset must clear the Failed records so the Failed counter reads 0");

            // Live jobs preserved.
            Assert.That(await repo.GetJobCountAsync(UploadStatus.Pending), Is.EqualTo(1),
                "Reset must NOT touch Pending jobs (un-uploaded work)");
            Assert.That(await repo.GetJobCountAsync(UploadStatus.InProgress), Is.EqualTo(1),
                "Reset must NOT touch an in-flight upload");

            // Reload: total stays 0 (records cleared so the clamp seeds 0); the pending+inprogress jobs
            // survive (InProgress is reset to Pending on load, so 2 pending after reload).
            var reloaded = new UploadQueueRepository(tempDir);
            Assert.That(reloaded.CompletedTotal, Is.EqualTo(0),
                "Reset must persist across restart");
            Assert.That(await reloaded.GetJobCountAsync(UploadStatus.Pending), Is.EqualTo(2),
                "Both live jobs survive reset + reload (InProgress -> Pending on load)");
        }

        [Test]
        public void StaleQueue_Detected()
        {
            // Create a repository, enqueue a job, then modify QueuedAt via reflection
            // to simulate a job from 12 hours ago
            var setupDir = Path.Combine(tempDir, "stale");
            var repo = new UploadQueueRepository(setupDir);

            // We need to enqueue a job and then manipulate its QueuedAt
            // Since we can't directly access the private jobs list, we'll
            // create a new repo with pre-seeded old data
            var job = CreatePendingJob();
            repo.EnqueueAsync(job).GetAwaiter().GetResult();

            // Use reflection to access the private jobs list and modify QueuedAt
            var jobsField = typeof(UploadQueueRepository).GetField("jobs",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var jobsList = (List<UploadJob>)jobsField!.GetValue(repo)!;
            jobsList[0].QueuedAt = DateTime.UtcNow.AddHours(-12);

            // Save the modified queue to disk using reflection
            var saveMethod = typeof(UploadQueueRepository).GetMethod("SaveQueueToDiskAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            ((Task)saveMethod!.Invoke(repo, null)!).GetAwaiter().GetResult();

            // Construct a new repository from the same directory -- should detect stale
            var freshRepo = new UploadQueueRepository(setupDir);

            Assert.That(freshRepo.IsStaleQueue, Is.True, "Queue with 12-hour-old job should be stale");
            Assert.That(freshRepo.StaleJobCount, Is.GreaterThan(0), "Should report stale job count");
        }

        [Test]
        public void StaleQueue_NotDetected()
        {
            var repo = new UploadQueueRepository(tempDir);

            // Enqueue a job (QueuedAt = now by default)
            var job = CreatePendingJob();
            repo.EnqueueAsync(job).GetAwaiter().GetResult();

            // Construct fresh repo from same dir
            var freshRepo = new UploadQueueRepository(tempDir);

            Assert.That(freshRepo.IsStaleQueue, Is.False, "Recent queue should NOT be stale");
        }

        [Test]
        public async Task FailedJobPersistence_SurvivesRoundTrip()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);

            // Transition to Failed
            await repo.UpdateJobStatusAsync(job.Id, UploadStatus.Failed, "Permanent error");

            // Verify FailedAt was set
            var failedBefore = (await repo.GetJobsByStatusAsync(UploadStatus.Failed)).First();
            Assert.That(failedBefore.FailedAt, Is.Not.Null);
            var originalFailedAt = failedBefore.FailedAt!.Value;

            // Construct a new repository from the same directory (simulates restart)
            var freshRepo = new UploadQueueRepository(tempDir);

            // Verify FailedAt survived the round-trip
            var failedAfter = (await freshRepo.GetJobsByStatusAsync(UploadStatus.Failed)).First();
            Assert.That(failedAfter.FailedAt, Is.Not.Null);
            // Allow 1 second tolerance for serialization rounding
            Assert.That(failedAfter.FailedAt!.Value,
                Is.EqualTo(originalFailedAt).Within(TimeSpan.FromSeconds(1)),
                "FailedAt should survive save-and-reload round-trip");
        }

        // ====================================================================
        // Corrupt-queue quarantine (REL-02) + fsync-before-move (REL-12)
        // ====================================================================

        [Test]
        public void CorruptQueue_QuarantinedNotWiped_WithWarning()
        {
            // Write an undecryptable queue.dat (random bytes -- DPAPI Unprotect will throw)
            var queueDat = Path.Combine(tempDir, "queue.dat");
            File.WriteAllBytes(queueDat, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03 });

            // Constructing the repo loads the queue; corrupt file must be quarantined
            var repo = new UploadQueueRepository(tempDir);

            // Warning surfaced
            Assert.That(repo.HasCorruptQueueWarning, Is.True,
                "HasCorruptQueueWarning should be true after a corrupt queue.dat is loaded");
            Assert.That(repo.CorruptQueueFileName, Is.Not.Null.And.Not.Empty,
                "CorruptQueueFileName should point at the quarantine file");

            // The corrupt file must be renamed to a .corrupt-<ts> quarantine file, not wiped
            var corruptFiles = Directory.GetFiles(tempDir, "queue.dat.corrupt-*");
            Assert.That(corruptFiles, Has.Length.EqualTo(1),
                "Corrupt queue.dat should be quarantined as queue.dat.corrupt-<timestamp>");
            Assert.That(Path.GetFileName(corruptFiles[0]), Is.EqualTo(repo.CorruptQueueFileName),
                "CorruptQueueFileName should match the on-disk quarantine file name");
        }

        [Test]
        public async Task CorruptQueue_ManualRestoreOnly_EmptyLiveQueueQuarantinePreserved()
        {
            // Corrupt queue.dat
            var queueDat = Path.Combine(tempDir, "queue.dat");
            File.WriteAllBytes(queueDat, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

            var repo = new UploadQueueRepository(tempDir);

            // Live queue must be EMPTY -- no auto-restore from quarantine
            var pending = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
            var failed = (await repo.GetJobsByStatusAsync(UploadStatus.Failed)).ToList();
            Assert.That(pending, Is.Empty, "Live queue should be empty after quarantine (no auto-restore)");
            Assert.That(failed, Is.Empty, "Live queue should be empty after quarantine (no auto-restore)");

            // Capture the quarantine file
            var quarantineBefore = Directory.GetFiles(tempDir, "queue.dat.corrupt-*").Single();

            // A subsequent save (enqueue) must NOT overwrite or delete the quarantine file
            await repo.EnqueueAsync(CreatePendingJob());

            var quarantineAfter = Directory.GetFiles(tempDir, "queue.dat.corrupt-*");
            Assert.That(quarantineAfter, Has.Length.EqualTo(1),
                "Quarantine file must be preserved for manual restore after a new save");
            Assert.That(File.Exists(quarantineBefore), Is.True,
                "The original quarantine file must still exist on disk");

            // The new save created a fresh, valid queue.dat with only the new job
            Assert.That(File.Exists(queueDat), Is.True, "A fresh queue.dat should exist after the new save");
            var fresh = new UploadQueueRepository(tempDir);
            var freshPending = (await fresh.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
            Assert.That(freshPending, Has.Count.EqualTo(1),
                "Fresh queue should contain only the newly enqueued job, not auto-imported quarantine jobs");
        }

        [Test]
        public async Task CorruptQueue_PreservesLastGoodBak_AlongsideQuarantine()
        {
            // First, create a valid queue with a job (produces a good queue.dat and, on next save, a .bak)
            var repo1 = new UploadQueueRepository(tempDir);
            await repo1.EnqueueAsync(CreatePendingJob());
            // Second save creates a .bak of the previous good queue.dat
            await repo1.EnqueueAsync(CreatePendingJob());

            var bakPath = Path.Combine(tempDir, "queue.dat.bak");
            Assert.That(File.Exists(bakPath), Is.True,
                "A .bak of the last-good queue should exist after a second save");

            // Now corrupt the live queue.dat and reload
            var queueDat = Path.Combine(tempDir, "queue.dat");
            File.WriteAllBytes(queueDat, new byte[] { 0x00, 0x11, 0x22, 0x33 });

            var repo2 = new UploadQueueRepository(tempDir);

            // Both the .bak (rollback artifact) and the .corrupt-<ts> (forensic quarantine) coexist
            Assert.That(File.Exists(bakPath), Is.True,
                ".bak of the last-good queue must be preserved alongside the quarantine");
            var corruptFiles = Directory.GetFiles(tempDir, "queue.dat.corrupt-*");
            Assert.That(corruptFiles, Has.Length.EqualTo(1),
                "The corrupt queue must be quarantined to a distinct .corrupt-<ts> file");
            Assert.That(repo2.HasCorruptQueueWarning, Is.True);
        }

        [Test]
        public async Task DurableSave_NoTmpRemains_AfterFlushAndRename()
        {
            var repo = new UploadQueueRepository(tempDir);
            await repo.EnqueueAsync(CreatePendingJob());

            // After a durable atomic save, the data file exists and no partial .tmp remains
            var queueDat = Path.Combine(tempDir, "queue.dat");
            var queueTmp = Path.Combine(tempDir, "queue.dat.tmp");
            Assert.That(File.Exists(queueDat), Is.True, "queue.dat should exist after durable save");
            Assert.That(File.Exists(queueTmp), Is.False, "No .tmp should remain after flush + rename");
            Assert.That(new FileInfo(queueDat).Length, Is.GreaterThan(0),
                "queue.dat must be non-empty (flushed before rename, not zero-length)");
        }

        // ====================================================================
        // Configurable drain order (D-05) -- both modes via getter-delegate
        // ====================================================================

        [Test]
        public async Task DrainOrder_NewestFirst_LifoDefault()
        {
            // Default constructor (no delegate) must behave as Newest-first (LIFO) -- zero behavior change.
            var repo = new UploadQueueRepository(tempDir);

            var first = CreatePendingJob();
            await repo.EnqueueAsync(first);
            await Task.Delay(5);
            var second = CreatePendingJob();
            await repo.EnqueueAsync(second);
            await Task.Delay(5);
            var third = CreatePendingJob();
            await repo.EnqueueAsync(third);

            var peeked = await repo.PeekAsync();
            Assert.That(peeked!.Id, Is.EqualTo(third.Id),
                "Default drain order should return the NEWEST job (LIFO)");
        }

        [Test]
        public async Task DrainOrder_OldestFirst_FifoViaDelegate()
        {
            // Inject a getter-delegate that selects Oldest-first (FIFO).
            var newestFirst = false;
            var repo = new UploadQueueRepository(tempDir, () => newestFirst);

            var first = CreatePendingJob();
            await repo.EnqueueAsync(first);
            await Task.Delay(5);
            var second = CreatePendingJob();
            await repo.EnqueueAsync(second);
            await Task.Delay(5);
            var third = CreatePendingJob();
            await repo.EnqueueAsync(third);

            var peeked = await repo.PeekAsync();
            Assert.That(peeked!.Id, Is.EqualTo(first.Id),
                "Oldest-first delegate should return the OLDEST job (FIFO)");
        }

        [Test]
        public async Task DrainOrder_DelegateReadDynamically_SwitchesAtRuntime()
        {
            // The delegate is consulted on each Peek, so a settings change takes effect live.
            var newestFirst = true;
            var repo = new UploadQueueRepository(tempDir, () => newestFirst);

            var oldest = CreatePendingJob();
            await repo.EnqueueAsync(oldest);
            await Task.Delay(5);
            var newest = CreatePendingJob();
            await repo.EnqueueAsync(newest);

            Assert.That((await repo.PeekAsync())!.Id, Is.EqualTo(newest.Id), "LIFO before switch");

            newestFirst = false; // flip the setting at runtime
            Assert.That((await repo.PeekAsync())!.Id, Is.EqualTo(oldest.Id), "FIFO after switch");
        }

        // ====================================================================
        // Terminal Failed auto-pruning (D-06): 7-day age + 200-entry cap
        // ====================================================================

        [Test]
        public async Task FailedPrune_AgesOutOlderThan7Days()
        {
            var repo = new UploadQueueRepository(tempDir);

            var stale = CreatePendingJob();
            await repo.EnqueueAsync(stale);
            await repo.UpdateJobStatusAsync(stale.Id, UploadStatus.Failed, "old failure");

            var recent = CreatePendingJob();
            await repo.EnqueueAsync(recent);
            await repo.UpdateJobStatusAsync(recent.Id, UploadStatus.Failed, "recent failure");

            // Age the stale job's FailedAt to 8 days ago via reflection on the private jobs list.
            var jobsField = typeof(UploadQueueRepository).GetField("jobs",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var jobsList = (List<UploadJob>)jobsField!.GetValue(repo)!;
            jobsList.First(j => j.Id == stale.Id).FailedAt = DateTime.UtcNow.AddDays(-8);

            // Reload from a fresh repo (prune runs on load).
            var saveMethod = typeof(UploadQueueRepository).GetMethod("SaveQueueToDiskAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            ((Task)saveMethod!.Invoke(repo, null)!).GetAwaiter().GetResult();

            var fresh = new UploadQueueRepository(tempDir);
            var failed = (await fresh.GetJobsByStatusAsync(UploadStatus.Failed)).ToList();
            Assert.That(failed, Has.Count.EqualTo(1), "Failed jobs older than 7 days should be pruned");
            Assert.That(failed[0].Id, Is.EqualTo(recent.Id), "Only the recent Failed job should remain");
        }

        [Test]
        public async Task FailedPrune_CapsAt200Entries()
        {
            var repo = new UploadQueueRepository(tempDir);

            // Create 205 Failed jobs (all recent, so age-out does not trigger).
            for (int i = 0; i < 205; i++)
            {
                var job = CreatePendingJob();
                await repo.EnqueueAsync(job);
                await repo.UpdateJobStatusAsync(job.Id, UploadStatus.Failed, "fail " + i);
            }

            var failed = (await repo.GetJobsByStatusAsync(UploadStatus.Failed)).ToList();
            Assert.That(failed.Count, Is.LessThanOrEqualTo(200),
                "Failed jobs should be capped at 200 newest entries");
        }

        // ====================================================================
        // Schema envelope + StringEnumConverter + legacy migration (MAINT-01)
        // ====================================================================

        [Test]
        public async Task SchemaMigration_LegacyBareArray_RoundTripsToVersionedEnvelope()
        {
            // Seed a LEGACY bare-array queue.dat (pre-envelope format): a JSON array of jobs,
            // DPAPI-encrypted the same way the repo writes it.
            var legacyJobs = new List<UploadJob>
            {
                CreatePendingJob(),
                CreatePendingJob()
            };
            legacyJobs[1].Status = UploadStatus.Failed;
            legacyJobs[1].FailedAt = DateTime.UtcNow;

            var legacyJson = Newtonsoft.Json.JsonConvert.SerializeObject(legacyJobs, Newtonsoft.Json.Formatting.None);
            WriteEncryptedQueue(legacyJson);

            // Load via the repo (should migrate the bare array to the v1 envelope, no data loss)
            var repo = new UploadQueueRepository(tempDir);
            Assert.That(repo.HasCorruptQueueWarning, Is.False,
                "A legacy bare-array file is valid, not corrupt -- must NOT be quarantined");

            var pending = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
            var failed = (await repo.GetJobsByStatusAsync(UploadStatus.Failed)).ToList();
            Assert.That(pending, Has.Count.EqualTo(1), "Pending job should survive migration");
            Assert.That(failed, Has.Count.EqualTo(1), "Failed job + status should survive migration");

            // Trigger a re-save and confirm the on-disk format is now the {SchemaVersion, Jobs} envelope.
            await repo.EnqueueAsync(CreatePendingJob());
            var savedJson = ReadDecryptedQueue();
            var token = Newtonsoft.Json.Linq.JToken.Parse(savedJson);
            Assert.That(token.Type, Is.EqualTo(Newtonsoft.Json.Linq.JTokenType.Object),
                "Re-saved queue.dat must be a {SchemaVersion, Jobs} object, not a bare array");
            Assert.That(token["SchemaVersion"], Is.Not.Null, "Envelope must carry SchemaVersion");
            Assert.That(token["SchemaVersion"]!.ToObject<int>(), Is.EqualTo(2),
                "Current envelope schema is v2 (adds the persisted CompletedTotal running total)");
            Assert.That(token["CompletedTotal"], Is.Not.Null, "v2 envelope must carry CompletedTotal");
            Assert.That(token["Jobs"], Is.Not.Null, "Envelope must carry Jobs array");
        }

        [Test]
        public async Task SchemaEnvelope_EnumsSerializedAsStrings()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);
            await repo.UpdateJobStatusAsync(job.Id, UploadStatus.Failed, "boom");

            var savedJson = ReadDecryptedQueue();
            // Status must appear by NAME, not as an integer, so value reordering/removal is immune.
            Assert.That(savedJson, Does.Contain("\"Failed\""),
                "Enum status should be serialized by name (StringEnumConverter)");
            Assert.That(savedJson, Does.Not.Contain("\"Status\":3"),
                "Enum status should NOT be serialized as an integer");
        }

        [Test]
        public async Task SchemaEnvelope_UnknownEnumName_TolerantFallback()
        {
            // Hand-craft a v1 envelope whose job has an unknown/removed status name ("Cancelled").
            var envelope = "{\"SchemaVersion\":1,\"Jobs\":[{" +
                           "\"Id\":\"" + Guid.NewGuid() + "\"," +
                           "\"LocalPath\":\"D:/a.fits\",\"RelativePath\":\"a.fits\"," +
                           "\"FileSize\":1024,\"Status\":\"Cancelled\"," +
                           "\"QueuedAt\":\"" + DateTime.UtcNow.ToString("o") + "\"}]}";
            WriteEncryptedQueue(envelope);

            // Load must NOT throw and must NOT quarantine -- the unknown name falls back tolerantly.
            var repo = new UploadQueueRepository(tempDir);
            Assert.That(repo.HasCorruptQueueWarning, Is.False,
                "An unknown enum name must fall back tolerantly, not be treated as corruption");

            // The job is preserved (with a fallback status), not lost, and other values are intact.
            var all = new List<UploadJob>();
            foreach (UploadStatus s in Enum.GetValues(typeof(UploadStatus)))
                all.AddRange(await repo.GetJobsByStatusAsync(s));
            Assert.That(all, Has.Count.EqualTo(1),
                "Job with unknown status name should be preserved with a fallback status");
            Assert.That(all[0].FileSize, Is.EqualTo(1024),
                "Other field values must not shift when one enum name is unknown");
        }

        // ---- DPAPI seed helpers (mirror the repo's private encrypt/decrypt) ----

        private void WriteEncryptedQueue(string plainJson)
        {
            var encryptMethod = typeof(UploadQueueRepository).GetMethod("EncryptData",
                BindingFlags.NonPublic | BindingFlags.Static);
            var encrypted = (byte[])encryptMethod!.Invoke(null, new object[] { plainJson })!;
            File.WriteAllBytes(Path.Combine(tempDir, "queue.dat"), encrypted);
        }

        private string ReadDecryptedQueue()
        {
            var bytes = File.ReadAllBytes(Path.Combine(tempDir, "queue.dat"));
            var decryptMethod = typeof(UploadQueueRepository).GetMethod("DecryptData",
                BindingFlags.NonPublic | BindingFlags.Static);
            // DecryptData(byte[], out bool wasLegacyNullEntropy): pass a slot for the out arg and ignore it.
            var args = new object?[] { bytes, false };
            return (string)decryptMethod!.Invoke(null, args)!;
        }

        // ====================================================================
        // REL-06 / REL-07: non-blocking backoff (NextRetryAfter) + transient-only
        // half-open probe promotion. Repository-level behavior.
        // ====================================================================

        [Test]
        public async Task PeekAsync_SkipsJobsNotYetDue_AndReturnsThemOnceDue()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);

            // Schedule a future retry -> job is Pending but NOT yet due.
            await repo.ScheduleRetryAsync(job.Id, DateTime.UtcNow.AddMinutes(5), "transient error");
            Assert.That(await repo.PeekAsync(), Is.Null,
                "PeekAsync must skip a Pending job whose NextRetryAfter is in the future");

            // Schedule a past retry -> job is now due.
            await repo.ScheduleRetryAsync(job.Id, DateTime.UtcNow.AddSeconds(-1), "transient error");
            var due = await repo.PeekAsync();
            Assert.That(due, Is.Not.Null, "PeekAsync must return the job once NextRetryAfter has elapsed");
            Assert.That(due!.Id, Is.EqualTo(job.Id));
        }

        [Test]
        public async Task PromoteTransientFailedToPendingAsync_PromotesTransient_ExcludesPermanent()
        {
            var repo = new UploadQueueRepository(tempDir);
            var transient = CreatePendingJob();
            var permanent = CreatePendingJob();
            await repo.EnqueueAsync(transient);
            await repo.EnqueueAsync(permanent);

            await repo.MarkFailedAsync(transient.Id, "network timeout", isPermanent: false);
            await repo.MarkFailedAsync(permanent.Id, "file not found", isPermanent: true);

            var promoted = await repo.PromoteTransientFailedToPendingAsync();

            Assert.That(promoted, Is.EqualTo(1), "Only the transient-failed job should be promoted");
            Assert.That(await repo.GetJobCountAsync(UploadStatus.Pending), Is.EqualTo(1),
                "Transient job should be back to Pending");
            Assert.That(await repo.GetJobCountAsync(UploadStatus.Failed), Is.EqualTo(1),
                "Permanent-failed job must remain Failed (never auto-promoted)");

            var pending = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).Single();
            Assert.That(pending.Id, Is.EqualTo(transient.Id));
        }

        [Test]
        public async Task PromoteTransientFailedToPendingAsync_PermanentNeverPromoted_AcrossRepeatedProbes()
        {
            var repo = new UploadQueueRepository(tempDir);
            var permanent = CreatePendingJob();
            await repo.EnqueueAsync(permanent);
            await repo.MarkFailedAsync(permanent.Id, "access denied", isPermanent: true);

            // Repeated half-open probes must never re-queue a permanently-failed job.
            for (int i = 0; i < 5; i++)
            {
                var promoted = await repo.PromoteTransientFailedToPendingAsync();
                Assert.That(promoted, Is.EqualTo(0), $"Probe {i}: permanent failure must not be promoted");
            }

            Assert.That(await repo.GetJobCountAsync(UploadStatus.Failed), Is.EqualTo(1));
            Assert.That(await repo.GetJobCountAsync(UploadStatus.Pending), Is.EqualTo(0));
        }

        [Test]
        public async Task PromoteTransientFailedToPendingAsync_ClearsNextRetryAfter_PreservesLastFailureReason()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);
            await repo.MarkFailedAsync(job.Id, "transient boom", isPermanent: false);

            await repo.PromoteTransientFailedToPendingAsync();

            // Promoted job must be immediately due (NextRetryAfter cleared) and visible to PeekAsync.
            var due = await repo.PeekAsync();
            Assert.That(due, Is.Not.Null, "Promoted job should be immediately due (NextRetryAfter cleared)");
            Assert.That(due!.NextRetryAfter, Is.Null);
            Assert.That(due.RetryCount, Is.EqualTo(0), "Retry budget reset on promotion");
            Assert.That(due.LastFailureReason, Is.EqualTo("transient boom"),
                "LastFailureReason preserved as history across promotion");
        }

        [Test]
        public async Task RetryJobAsync_ClearsNextRetryAfter_PreservesLastFailureReason()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);
            await repo.MarkFailedAsync(job.Id, "manual-retry reason", isPermanent: true);

            await repo.RetryJobAsync(job.Id);

            var due = await repo.PeekAsync();
            Assert.That(due, Is.Not.Null, "Manually retried job should be immediately due");
            Assert.That(due!.NextRetryAfter, Is.Null, "Manual retry clears NextRetryAfter");
            Assert.That(due.IsPermanentFailure, Is.False, "Manual retry resets the permanent marker");
            Assert.That(due.LastFailureReason, Is.EqualTo("manual-retry reason"),
                "Manual retry preserves LastFailureReason as history");
        }

        [Test]
        public async Task MarkFailedAsync_PersistsReasonAndPermanentMarker_AcrossReload()
        {
            var job = CreatePendingJob();
            var repo1 = new UploadQueueRepository(tempDir);
            await repo1.EnqueueAsync(job);
            await repo1.MarkFailedAsync(job.Id, "permanent reason", isPermanent: true);

            // Reload from disk -- the new fields must round-trip through the envelope.
            var repo2 = new UploadQueueRepository(tempDir);
            var reloaded = (await repo2.GetJobsByStatusAsync(UploadStatus.Failed)).Single();
            Assert.That(reloaded.LastFailureReason, Is.EqualTo("permanent reason"));
            Assert.That(reloaded.IsPermanentFailure, Is.True);
        }

        /// <summary>
        /// Helper to create a minimal pending job for testing.
        /// </summary>
        private static UploadJob CreatePendingJob()
        {
            return new UploadJob
            {
                Id = Guid.NewGuid(),
                LocalPath = @"D:\Astro\M31\Light\" + Guid.NewGuid().ToString("N") + ".fits",
                RelativePath = "M31/Light/test.fits",
                FileSize = 10_485_760, // 10 MB
                CapturedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow,
                Status = UploadStatus.Pending,
                RetryCount = 0,
                Filter = "L",
                Duration = 300.0,
                FileType = "FITS",
                MetadataJson = "{}"
            };
        }
    }
}
