using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Astrovault.Data;
using Astrovault.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CloudUploadPlugin.Tests.Data
{
    /// <summary>
    /// Repository-level tests for the blocked-state machine's durable persistence contract
    /// (storage-quota plan §4 Step 2): <c>SetBlockAsync</c>/<c>ClearBlockAsync</c> as one
    /// semaphore-held commit, <c>PeekAsync</c> honoring the resume-priority pin, the v2->v3 queue.dat
    /// envelope migration, and the salted identity-fingerprint contract. Mirrors the real-repository
    /// (temp dir) style already used by <c>QueueHardeningTests</c> / <c>QueueDpapiEntropyTests</c>.
    /// Covers the plan's step-7 test table rows 10 and 11 (repository half) plus the underlying
    /// SetBlockAsync/ClearBlockAsync/PeekAsync contract rows 1-3 depend on.
    /// </summary>
    [TestFixture]
    [Category("StorageQuota")]
    [Category("Queue")]
    public class UploadQueueRepositoryBlockTests
    {
        private string tempDir = null!;

        [SetUp]
        public void Setup()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "queue_block_" + Guid.NewGuid().ToString("N"));
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

        // ====================================================================
        // SetBlockAsync: atomic commit contract
        // ====================================================================

        [Test]
        public async Task SetBlockAsync_ReturnsFailingJobToPending_NeverFailed_PreservesQueuedAt()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);
            await repo.UpdateJobStatusAsync(job.Id, UploadStatus.InProgress);
            var queuedAtBefore = job.QueuedAt;

            var committed = await repo.SetBlockAsync(
                UploadBlockKind.QuotaExceeded,
                job.Id,
                new UploadBlockSnapshot { UsedBytes = 16_000_000_000, LimitBytes = 10_000_000_000 },
                new UploadBlockIdentityInput { EndpointUrl = "https://api.astrovault.io", AccountId = null, ApiKey = "key-1" });

            Assert.That(committed, Is.True);

            var pending = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
            Assert.That(pending.Select(j => j.Id), Does.Contain(job.Id), "The failing job must return to Pending");
            Assert.That(await repo.GetJobCountAsync(UploadStatus.Failed), Is.EqualTo(0), "Never marked Failed");
            Assert.That(pending.Single(j => j.Id == job.Id).QueuedAt,
                Is.EqualTo(queuedAtBefore).Within(TimeSpan.FromMilliseconds(5)),
                "QueuedAt must never be mutated by a block transition");

            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded));
            Assert.That(repo.CurrentBlock.Snapshot!.UsedBytes, Is.EqualTo(16_000_000_000));
            Assert.That(repo.CurrentBlock.Snapshot!.LimitBytes, Is.EqualTo(10_000_000_000));
            Assert.That(repo.CurrentBlock.Snapshot!.TimestampUtc, Is.Not.Null);
            Assert.That(repo.CurrentBlock.Identity!.EndpointUrl, Is.EqualTo("https://api.astrovault.io"));
            Assert.That(repo.CurrentBlock.Identity!.KeyFingerprint, Is.Not.Null.And.Not.Empty);
            Assert.That(repo.CurrentBlock.Identity!.KeyFingerprint, Is.Not.EqualTo("key-1"),
                "The persisted identity must never be a bare copy/digest of the raw API key");
        }

        [Test]
        public async Task SetBlockAsync_NoRetryScheduled_NoRetryCountIncrement()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);

            await repo.SetBlockAsync(UploadBlockKind.AuthInvalid, job.Id, new UploadBlockSnapshot(),
                new UploadBlockIdentityInput { EndpointUrl = "https://x", ApiKey = "k" });

            var reloaded = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).Single(j => j.Id == job.Id);
            Assert.That(reloaded.RetryCount, Is.EqualTo(0));
            Assert.That(reloaded.NextRetryAfter, Is.Null);
        }

        [Test]
        public async Task SetBlockAsync_EnteringNewBlock_ClearsAnyStaleResumePriorityPin()
        {
            var repo = new UploadQueueRepository(tempDir);
            var older = CreatePendingJob();
            await repo.EnqueueAsync(older);
            await Task.Delay(5);
            var newer = CreatePendingJob();
            await repo.EnqueueAsync(newer);

            // Pin `older` (simulating a prior resume cycle's leftover pin).
            await repo.ClearBlockAsync(older.Id);
            Assert.That((await repo.PeekAsync())!.Id, Is.EqualTo(older.Id), "Precondition: pin honored");

            // A brand-new block latches -- entering it must drop the stale pin from the PREVIOUS cycle.
            await repo.SetBlockAsync(UploadBlockKind.QuotaExceeded, newer.Id, new UploadBlockSnapshot(),
                new UploadBlockIdentityInput { EndpointUrl = "https://x", ApiKey = "k" });
            await repo.ClearBlockAsync(null); // clear with no NEW pin

            Assert.That((await repo.PeekAsync())!.Id, Is.EqualTo(newer.Id),
                "Normal newest-first order must resume -- the stale `older` pin must not have survived SetBlockAsync");
        }

        [Test]
        public async Task SetBlockAsync_WhenDiskWriteFails_ReturnsFalse_ButInMemoryStateStillApplied()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);

            // Force the NEXT save to fail: remove the data folder so the .tmp write throws.
            Directory.Delete(tempDir, recursive: true);

            var committed = await repo.SetBlockAsync(UploadBlockKind.AuthInvalid, job.Id, new UploadBlockSnapshot(),
                new UploadBlockIdentityInput { EndpointUrl = "https://x", ApiKey = "k" });

            Assert.That(committed, Is.False, "The commit could not be written to disk");
            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.AuthInvalid),
                "Fail-safe: the in-memory state is still applied even though persistence failed");

            Directory.CreateDirectory(tempDir); // let TearDown's delete succeed cleanly
        }

        [Test]
        public async Task SetBlockAsync_JobStateAndBlockState_CommitTogetherInOneAtomicSave()
        {
            // Storage-quota plan directive I (B#8): regression guard for the single-commit contract.
            // The existing failure test above forces the WHOLE disk write to fail (proving fail-safe
            // in-memory application) but cannot detect a regression where SetBlockAsync was split into
            // TWO separate saves (job status first, block state second) -- both would still individually
            // succeed on a healthy disk. This test instead inspects the .bak rollback snapshot that
            // CommitTmpToQueue takes of the PRE-call queue.dat: if SetBlockAsync ever performed a SEPARATE
            // job-status save before the block-state save, that intermediate state (job already Pending,
            // block not yet set) would leak into .bak as the "previous" snapshot for the block save.
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);
            await repo.UpdateJobStatusAsync(job.Id, UploadStatus.InProgress);

            await repo.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id, new UploadBlockSnapshot(),
                new UploadBlockIdentityInput { EndpointUrl = "https://x", ApiKey = "k" });

            var bakBytes = File.ReadAllBytes(Path.Combine(tempDir, "queue.dat.bak"));
            var decryptMethod = typeof(UploadQueueRepository).GetMethod("DecryptData", BindingFlags.NonPublic | BindingFlags.Static);
            var bakJson = (string)decryptMethod!.Invoke(null, new object?[] { bakBytes, false })!;
            var bakToken = JObject.Parse(bakJson);

            var bakHasNoBlock = bakToken["Block"] == null || bakToken["Block"]!.Type == JTokenType.Null;
            Assert.That(bakHasNoBlock, Is.True,
                "The .bak snapshot is the state immediately BEFORE SetBlockAsync's commit -- it must show NO block yet, proving there was only ONE save");
            var bakJobStatus = ((JArray)bakToken["Jobs"]!).Single(j => j["Id"]!.Value<string>() == job.Id.ToString())["Status"]!.Value<string>();
            Assert.That(bakJobStatus, Is.EqualTo("InProgress"),
                "The .bak snapshot must show the job's PRE-call status (InProgress) -- a two-save regression would already show Pending here");
        }

        // ====================================================================
        // ClearBlockAsync + PeekAsync pin priority
        // ====================================================================

        [Test]
        public async Task PeekAsync_HonorsResumePriorityJobId_BeforeNormalDrainOrder()
        {
            var repo = new UploadQueueRepository(tempDir);
            var older = CreatePendingJob();
            await repo.EnqueueAsync(older);
            await Task.Delay(5);
            var newer = CreatePendingJob();
            await repo.EnqueueAsync(newer);

            Assert.That((await repo.PeekAsync())!.Id, Is.EqualTo(newer.Id), "Precondition: normal LIFO order picks `newer`");

            var cleared = await repo.ClearBlockAsync(older.Id);

            Assert.That(cleared, Is.True);
            Assert.That((await repo.PeekAsync())!.Id, Is.EqualTo(older.Id),
                "The pinned job must be attempted first, ahead of the normal drain order");
        }

        [Test]
        public async Task PeekAsync_PinnedJobNoLongerDue_FallsThroughToNormalOrder()
        {
            var repo = new UploadQueueRepository(tempDir);
            var pinnedJob = CreatePendingJob();
            await repo.EnqueueAsync(pinnedJob);
            var other = CreatePendingJob();
            await repo.EnqueueAsync(other);

            await repo.ClearBlockAsync(pinnedJob.Id);
            // The pinned job completes -- it is no longer a due Pending job.
            await repo.UpdateJobStatusAsync(pinnedJob.Id, UploadStatus.Completed);

            var peeked = await repo.PeekAsync();
            Assert.That(peeked, Is.Not.Null);
            Assert.That(peeked!.Id, Is.EqualTo(other.Id),
                "A stale pin (job no longer due/Pending) must fall through to the normal drain order");
        }

        [Test]
        public async Task ClearBlockAsync_NeverMutatesQueuedAt()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);
            var before = job.QueuedAt;

            await repo.ClearBlockAsync(job.Id);
            var peeked = await repo.PeekAsync();

            Assert.That(peeked!.QueuedAt, Is.EqualTo(before));
        }

        [Test]
        public async Task ClearBlockAsync_WhenDiskWriteFails_ReturnsFalse_ButCurrentBlockStillReflectsThePreviousBlock()
        {
            // Storage-quota plan directive C (the durability-latch P0): a failed ClearBlockAsync commit
            // must NEVER release the in-memory latch. The old implementation mutated currentBlock/pin to
            // "cleared" BEFORE attempting the commit and never rolled back on failure -- so CurrentBlock
            // lied about being cleared even though the disk still held the durable block.
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);
            var committed = await repo.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id,
                new UploadBlockSnapshot { UsedBytes = 5, LimitBytes = 10 },
                new UploadBlockIdentityInput { EndpointUrl = "https://api.astrovault.io", ApiKey = "the-key" });
            Assert.That(committed, Is.True, "Precondition: the block is durably latched");

            // Force the NEXT save to fail: remove the data folder so the .tmp write throws.
            Directory.Delete(tempDir, recursive: true);

            var cleared = await repo.ClearBlockAsync(job.Id);

            Assert.That(cleared, Is.False, "The clear could not be written to disk");
            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "Fail-safe: CurrentBlock must still reflect the block that failed to clear, not a phantom cleared state");
            Assert.That(repo.CurrentBlock.FailedJobId, Is.EqualTo(job.Id),
                "The original block's FailedJobId must survive the failed-clear rollback unchanged");

            Directory.CreateDirectory(tempDir); // let TearDown's delete succeed cleanly
        }

        [Test]
        public async Task ClearBlockAsync_AfterAFailedAttempt_ASubsequentSuccessfulClear_StartsFromTheTruePreviousState()
        {
            // Commit-ordering regression: a failed ClearBlockAsync must roll back cleanly enough that a
            // LATER, successful ClearBlockAsync call is not corrupted by the earlier failure's leftover
            // (would-be-wrong) in-memory state -- e.g. losing the original resume pin/job association.
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);
            await repo.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id, new UploadBlockSnapshot(),
                new UploadBlockIdentityInput { EndpointUrl = "https://x", ApiKey = "k" });

            // First clear attempt fails (disk write fails).
            Directory.Delete(tempDir, recursive: true);
            var firstAttempt = await repo.ClearBlockAsync(job.Id);
            Assert.That(firstAttempt, Is.False);
            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "Still latched after the failed attempt");

            // Restore the data folder -- the second attempt can now succeed.
            Directory.CreateDirectory(tempDir);
            var secondAttempt = await repo.ClearBlockAsync(job.Id);

            Assert.That(secondAttempt, Is.True, "The retried clear must succeed once the disk is writable again");
            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.None));
            Assert.That((await repo.PeekAsync())!.Id, Is.EqualTo(job.Id), "The pin from the successful second attempt must be honored");
        }

        [Test]
        public async Task ClearBlockAsync_SetsCurrentBlockToNone()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);
            await repo.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id, new UploadBlockSnapshot(),
                new UploadBlockIdentityInput { EndpointUrl = "https://x", ApiKey = "k" });
            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded));

            var cleared = await repo.ClearBlockAsync(job.Id);

            Assert.That(cleared, Is.True);
            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.None));
        }

        // ====================================================================
        // ComputeIdentityFingerprintAsync: salted, stable, never a bare digest
        // ====================================================================

        [Test]
        public async Task ComputeIdentityFingerprintAsync_NullOrEmptyKey_ReturnsNull()
        {
            var repo = new UploadQueueRepository(tempDir);
            Assert.That(await repo.ComputeIdentityFingerprintAsync(null), Is.Null);
            Assert.That(await repo.ComputeIdentityFingerprintAsync(""), Is.Null);
        }

        [Test]
        public async Task ComputeIdentityFingerprintAsync_DifferentKeys_ProduceDifferentFingerprints()
        {
            var repo = new UploadQueueRepository(tempDir);
            var fpA = await repo.ComputeIdentityFingerprintAsync("key-A");
            var fpB = await repo.ComputeIdentityFingerprintAsync("key-B");

            Assert.That(fpA, Is.Not.Null.And.Not.Empty);
            Assert.That(fpA, Is.Not.EqualTo(fpB));
            Assert.That(fpA, Is.Not.EqualTo("key-A"));
        }

        [Test]
        public async Task ComputeIdentityFingerprintAsync_SameKey_StableAcrossReload()
        {
            var repo1 = new UploadQueueRepository(tempDir);
            var fp1 = await repo1.ComputeIdentityFingerprintAsync("same-key");

            var repo2 = new UploadQueueRepository(tempDir);
            var fp2 = await repo2.ComputeIdentityFingerprintAsync("same-key");

            Assert.That(fp2, Is.EqualTo(fp1),
                "The persisted per-installation salt must survive a reload so the SAME key always fingerprints the SAME way");
        }

        // ====================================================================
        // Storage-quota plan directive I (B#10): pin the ACTUAL security contract -- HMAC-SHA256 keyed
        // with the envelope salt -- rather than only the weaker "non-empty, differs between keys, not
        // literally the raw key" properties above (which a regression to plain unsalted SHA-256 would
        // also satisfy). Uses the narrow IdentitySaltForTests internal seam to independently recompute
        // the expected value.
        // ====================================================================

        [Test]
        public async Task ComputeIdentityFingerprintAsync_IsNotBareUnsaltedSha256_OfTheKey()
        {
            var repo = new UploadQueueRepository(tempDir);
            var fingerprint = await repo.ComputeIdentityFingerprintAsync("some-api-key");

            var bareSha256 = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("some-api-key")));

            Assert.That(fingerprint, Is.Not.EqualTo(bareSha256),
                "A regression to a bare/unsalted SHA-256 digest must fail this test -- the fingerprint must be KEYED (HMAC), not a plain hash");
        }

        [Test]
        public async Task ComputeIdentityFingerprintAsync_IsExactlyHmacSha256_KeyedWithTheEnvelopeSalt()
        {
            var repo = new UploadQueueRepository(tempDir);
            var fingerprint = await repo.ComputeIdentityFingerprintAsync("some-api-key");

            var salt = repo.IdentitySaltForTests;
            Assert.That(salt, Is.Not.Null, "The salt must have been lazily generated by the fingerprint call above");

            using var hmac = new HMACSHA256(salt);
            var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes("some-api-key")));

            Assert.That(fingerprint, Is.EqualTo(expected),
                "The fingerprint must be EXACTLY HMAC-SHA256 keyed with the persisted envelope salt");
        }

        // ====================================================================
        // Storage-quota plan directive I (C review B#10 residual): prove the raw key never reaches the
        // PERSISTED ENVELOPE -- round-trips a block with a known raw API key, decrypts queue.dat via the
        // existing DecryptData test seam, and asserts the raw key string is absent from the decrypted
        // JSON while the derived fingerprint field IS present.
        // ====================================================================

        [Test]
        public async Task SetBlockAsync_PersistedEnvelope_NeverContainsTheRawApiKey_OnlyTheFingerprint()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);

            const string rawApiKey = "av_live_THE-RAW-SECRET-KEY-VALUE-12345";
            var committed = await repo.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id,
                new UploadBlockSnapshot { UsedBytes = 1, LimitBytes = 2 },
                new UploadBlockIdentityInput { EndpointUrl = "https://api.astrovault.io", AccountId = "acct-1", ApiKey = rawApiKey });
            Assert.That(committed, Is.True);

            var expectedFingerprint = repo.CurrentBlock.Identity!.KeyFingerprint;
            Assert.That(expectedFingerprint, Is.Not.Null.And.Not.Empty, "Precondition: a fingerprint must have been derived");

            var decryptedJson = ReadDecryptedQueue();

            Assert.That(decryptedJson, Does.Not.Contain(rawApiKey),
                "The raw API key must NEVER appear anywhere in the decrypted persisted envelope");
            Assert.That(decryptedJson, Does.Contain(expectedFingerprint),
                "The derived fingerprint must be the value actually persisted for the block's identity");
        }

        [Test]
        public async Task ComputeIdentityFingerprintAsync_DifferentSalt_ProducesADifferentFingerprint_ForTheSameKey()
        {
            // Proves the salt actually PARTICIPATES in the computation (not merely present but unused):
            // two installations (two different salts) fingerprinting the SAME raw key must disagree.
            var repoA = new UploadQueueRepository(tempDir);
            var fpA = await repoA.ComputeIdentityFingerprintAsync("shared-key");

            var otherDir = Path.Combine(Path.GetTempPath(), "queue_block_salt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(otherDir);
            try
            {
                var repoB = new UploadQueueRepository(otherDir);
                var fpB = await repoB.ComputeIdentityFingerprintAsync("shared-key");

                Assert.That(repoA.IdentitySaltForTests, Is.Not.EqualTo(repoB.IdentitySaltForTests),
                    "Precondition: two installations must generate independent random salts");
                Assert.That(fpA, Is.Not.EqualTo(fpB),
                    "The SAME raw key must fingerprint DIFFERENTLY under a different salt -- proves the salt participates");
            }
            finally
            {
                try { Directory.Delete(otherDir, recursive: true); } catch { /* best effort */ }
            }
        }

        // ====================================================================
        // Test 10: persistence -- block survives reload, v2 fixture compatibility, legacy-null-entropy,
        // corrupt-quarantine, malformed salt, failed commit (covered above), .bak stays manual-only.
        // ====================================================================

        [Test]
        public async Task Block_SurvivesRepositoryReload()
        {
            var repo1 = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo1.EnqueueAsync(job);
            await repo1.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id,
                new UploadBlockSnapshot { UsedBytes = 111, LimitBytes = 222 },
                new UploadBlockIdentityInput { EndpointUrl = "https://api.astrovault.io", AccountId = "acct-9", ApiKey = "the-key" });

            var repo2 = new UploadQueueRepository(tempDir);

            Assert.That(repo2.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded));
            Assert.That(repo2.CurrentBlock.Snapshot!.UsedBytes, Is.EqualTo(111));
            Assert.That(repo2.CurrentBlock.Snapshot!.LimitBytes, Is.EqualTo(222));
            Assert.That(repo2.CurrentBlock.Identity!.EndpointUrl, Is.EqualTo("https://api.astrovault.io"));
            Assert.That(repo2.CurrentBlock.Identity!.AccountId, Is.EqualTo("acct-9"));

            // The reloaded repo must derive the SAME fingerprint for the SAME key (persisted salt).
            var expectedFingerprint = await repo2.ComputeIdentityFingerprintAsync("the-key");
            Assert.That(repo2.CurrentBlock.Identity!.KeyFingerprint, Is.EqualTo(expectedFingerprint));

            var pending = (await repo2.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
            Assert.That(pending.Select(j => j.Id), Does.Contain(job.Id));
        }

        [Test]
        public async Task SetBlockAsync_PersistsFailedJobId_SurvivesReload()
        {
            // Storage-quota plan §4 Step 5: FailedJobId must be persisted (not just held in-memory) so
            // UploadManager.ResumeAsync can pin the SAME job even after a NINA restart -- a fresh
            // repository instance has no other way to know which job triggered the block.
            var repo1 = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo1.EnqueueAsync(job);
            await repo1.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id,
                new UploadBlockSnapshot { UsedBytes = 1, LimitBytes = 2 },
                new UploadBlockIdentityInput { EndpointUrl = "https://api.astrovault.io", ApiKey = "the-key" });

            Assert.That(repo1.CurrentBlock.FailedJobId, Is.EqualTo(job.Id), "In-memory FailedJobId must be set immediately");

            var repo2 = new UploadQueueRepository(tempDir);
            Assert.That(repo2.CurrentBlock.FailedJobId, Is.EqualTo(job.Id),
                "FailedJobId must survive a reload (simulated NINA restart)");
        }

        [Test]
        public async Task RealV2EncryptedFixture_LoadsCleanlyUnderV3Code_BlockDefaultsToNone()
        {
            // Build a REAL v2-shaped envelope: the repository's OWN serializer output with the v3
            // additive keys (Block/ResumePriorityJobId/IdentitySalt) stripped out -- exactly what a
            // pre-Step-2 v2 file looked like on disk.
            var seedRepo = new UploadQueueRepository(tempDir);
            var pendingJob = CreatePendingJob();
            await seedRepo.EnqueueAsync(pendingJob);
            var completedJob = CreatePendingJob();
            await seedRepo.EnqueueAsync(completedJob);
            await seedRepo.UpdateJobStatusAsync(completedJob.Id, UploadStatus.Completed);

            var json = ReadDecryptedQueue();
            var token = JObject.Parse(json);
            token["SchemaVersion"] = 2;
            token.Remove("Block");
            token.Remove("ResumePriorityJobId");
            token.Remove("IdentitySalt");
            WriteEncryptedQueue(token.ToString(Formatting.None));

            var repo = new UploadQueueRepository(tempDir);

            Assert.That(repo.HasCorruptQueueWarning, Is.False, "A real v2 file must load cleanly under v3 code");
            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.None), "v2 has no Block field -- defaults to not-blocked");
            Assert.That(await repo.GetJobCountAsync(UploadStatus.Pending), Is.EqualTo(1));
            Assert.That(repo.CompletedTotal, Is.EqualTo(1));
        }

        [Test]
        public async Task LegacyNullEntropyFile_WithPersistedBlock_MigratesOnRead_BlockSurvives()
        {
            var seedRepo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await seedRepo.EnqueueAsync(job);
            await seedRepo.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id,
                new UploadBlockSnapshot { UsedBytes = 5, LimitBytes = 10 },
                new UploadBlockIdentityInput { EndpointUrl = "https://api.astrovault.io", ApiKey = "key-1" });

            var queueDat = Path.Combine(tempDir, "queue.dat");
            var entropyBytes = File.ReadAllBytes(queueDat);
            var plainJson = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(entropyBytes, UploadQueueRepository.DpapiEntropyForTests, DataProtectionScope.CurrentUser));

            // Re-protect the EXACT plaintext with null entropy -- a byte-for-byte legacy fixture.
            File.WriteAllBytes(queueDat,
                ProtectedData.Protect(Encoding.UTF8.GetBytes(plainJson), null, DataProtectionScope.CurrentUser));

            var migratingRepo = new UploadQueueRepository(tempDir);

            Assert.That(migratingRepo.HasCorruptQueueWarning, Is.False);
            Assert.That(migratingRepo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "The block must survive the null-entropy migration-on-read path");
            Assert.That(migratingRepo.CurrentBlock.Snapshot!.UsedBytes, Is.EqualTo(5));

            // The on-disk file must now be re-protected with entropy (migrated).
            var migratedBytes = File.ReadAllBytes(queueDat);
            Assert.That(
                () => ProtectedData.Unprotect(migratedBytes, null, DataProtectionScope.CurrentUser),
                Throws.TypeOf<CryptographicException>(),
                "After migration, queue.dat must no longer be decryptable with null entropy");
        }

        [Test]
        public void CorruptQueue_BlockDefaultsToNone_QuarantineUnaffected()
        {
            File.WriteAllBytes(Path.Combine(tempDir, "queue.dat"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

            var repo = new UploadQueueRepository(tempDir);

            Assert.That(repo.HasCorruptQueueWarning, Is.True);
            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.None),
                "A quarantined/undecryptable queue must default to not-blocked, never throw");
        }

        [Test]
        public async Task MalformedIdentitySalt_TreatedAsAbsent_LoadsWithoutThrow_RegeneratesOnNextUse()
        {
            var seedRepo = new UploadQueueRepository(tempDir);
            await seedRepo.EnqueueAsync(CreatePendingJob());

            var json = ReadDecryptedQueue();
            var token = JObject.Parse(json);
            token["IdentitySalt"] = "not-valid-base64!!!";
            WriteEncryptedQueue(token.ToString(Formatting.None));

            UploadQueueRepository repo = null!;
            Assert.That(() => repo = new UploadQueueRepository(tempDir), Throws.Nothing,
                "A malformed salt must never fault construction");
            Assert.That(repo.HasCorruptQueueWarning, Is.False, "A malformed salt alone must not quarantine the whole queue");

            var fp = await repo.ComputeIdentityFingerprintAsync("some-key");
            Assert.That(fp, Is.Not.Null.And.Not.Empty, "A fresh salt must be generated so identity operations still work");
        }

        [Test]
        public async Task UnknownBlockKindName_TolerantFallback_TreatedAsNoBlock()
        {
            var seedRepo = new UploadQueueRepository(tempDir);
            await seedRepo.EnqueueAsync(CreatePendingJob());

            var json = ReadDecryptedQueue();
            var token = JObject.Parse(json);
            token["Block"] = new JObject { ["Kind"] = "SomeFutureKindNotYetKnown" };
            WriteEncryptedQueue(token.ToString(Formatting.None));

            var repo = new UploadQueueRepository(tempDir);

            Assert.That(repo.HasCorruptQueueWarning, Is.False,
                "An unknown block kind must fall back tolerantly, not be treated as corruption");
            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.None));
        }

        [Test]
        public async Task SchemaEnvelope_BlockKindSerializedByName_NotInteger()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);
            await repo.SetBlockAsync(UploadBlockKind.AuthInvalid, job.Id, new UploadBlockSnapshot(),
                new UploadBlockIdentityInput { EndpointUrl = "https://x", ApiKey = "k" });

            var savedJson = ReadDecryptedQueue();
            Assert.That(savedJson, Does.Contain("\"AuthInvalid\""),
                "Block Kind should be serialized by name (StringEnumConverter), matching job Status");
            Assert.That(savedJson, Does.Not.Contain("\"Kind\":2"),
                "Block Kind should NOT be serialized as a raw integer");
        }

        // ====================================================================
        // Contract-delta round: QuotaBlockFlavor is an additive envelope field on Snapshot -- must
        // round-trip on reload, and a pre-existing v3 file written before this field existed must
        // default cleanly to StorageLimitExceeded.
        // ====================================================================

        [Test]
        public async Task Block_PlanRequiredFlavor_SurvivesRepositoryReload()
        {
            var repo1 = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo1.EnqueueAsync(job);
            await repo1.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id,
                new UploadBlockSnapshot { UsedBytes = 0, LimitBytes = 0, Flavor = QuotaBlockFlavor.PlanRequired },
                new UploadBlockIdentityInput { EndpointUrl = "https://api.astrovault.io", AccountId = "acct-9", ApiKey = "the-key" });
            Assert.That(repo1.CurrentBlock.Snapshot!.Flavor, Is.EqualTo(QuotaBlockFlavor.PlanRequired), "Precondition: in-memory Flavor set");

            var repo2 = new UploadQueueRepository(tempDir);

            Assert.That(repo2.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded));
            Assert.That(repo2.CurrentBlock.Snapshot!.Flavor, Is.EqualTo(QuotaBlockFlavor.PlanRequired),
                "The PlanRequired flavor must survive a repository reload (simulated NINA restart) so the correct banner shows");
        }

        [Test]
        public async Task Block_StorageLimitExceededFlavor_SurvivesRepositoryReload()
        {
            var repo1 = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo1.EnqueueAsync(job);
            await repo1.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id,
                new UploadBlockSnapshot { UsedBytes = 111, LimitBytes = 222, Flavor = QuotaBlockFlavor.StorageLimitExceeded },
                new UploadBlockIdentityInput { EndpointUrl = "https://api.astrovault.io", ApiKey = "the-key" });

            var repo2 = new UploadQueueRepository(tempDir);

            Assert.That(repo2.CurrentBlock.Snapshot!.Flavor, Is.EqualTo(QuotaBlockFlavor.StorageLimitExceeded));
        }

        [Test]
        public async Task OldEnvelope_SnapshotWithoutFlavorField_DefaultsToStorageLimitExceeded()
        {
            // Simulates a v3 queue.dat written before the Flavor field existed: strip it from the
            // persisted Snapshot sub-object after a normal SetBlockAsync commit, exactly like the
            // existing RealV2EncryptedFixture_LoadsCleanlyUnderV3Code_BlockDefaultsToNone pattern strips
            // whole v3-additive keys to simulate an older file shape.
            var seedRepo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await seedRepo.EnqueueAsync(job);
            await seedRepo.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id,
                new UploadBlockSnapshot { UsedBytes = 5, LimitBytes = 10, Flavor = QuotaBlockFlavor.PlanRequired },
                new UploadBlockIdentityInput { EndpointUrl = "https://api.astrovault.io", ApiKey = "key-1" });

            var json = ReadDecryptedQueue();
            var token = JObject.Parse(json);
            ((JObject)token["Block"]!["Snapshot"]!).Remove("Flavor");
            WriteEncryptedQueue(token.ToString(Formatting.None));

            var repo = new UploadQueueRepository(tempDir);

            Assert.That(repo.HasCorruptQueueWarning, Is.False, "A Snapshot missing the additive Flavor field must load cleanly");
            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded));
            Assert.That(repo.CurrentBlock.Snapshot!.UsedBytes, Is.EqualTo(5), "The other snapshot fields must still load normally");
            Assert.That(repo.CurrentBlock.Snapshot!.Flavor, Is.EqualTo(QuotaBlockFlavor.StorageLimitExceeded),
                "A pre-existing v3 file predating this field must default to the safe StorageLimitExceeded flavor");
        }

        [Test]
        public async Task UnknownFlavorString_InPersistedEnvelope_NormalizedBeforeToObject_BlockSurvives_FlavorDefaultsToStorageLimitExceeded()
        {
            // Pass H P1: an unknown Flavor string (a future additive flavor the running plugin predates,
            // or a downgrade reading a newer file) must be normalized BEFORE ToObject<UploadBlockSnapshot>
            // -- letting it hit ToObject unnormalized would throw, and ParseBlock's catch-all converts the
            // WHOLE block to UploadBlockState.None on any exception, which would release the stop-queue
            // latch. That is the forbidden direction: an unrecognized flavor must degrade the DISPLAY
            // wording only, never silently unlatch the queue. Mirrors
            // OldEnvelope_SnapshotWithoutFlavorField_DefaultsToStorageLimitExceeded's tamper-after-commit
            // pattern, but rewrites Flavor to an unknown value instead of removing it.
            var seedRepo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await seedRepo.EnqueueAsync(job);
            await seedRepo.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id,
                new UploadBlockSnapshot { UsedBytes = 5, LimitBytes = 10, Flavor = QuotaBlockFlavor.PlanRequired },
                new UploadBlockIdentityInput { EndpointUrl = "https://api.astrovault.io", ApiKey = "key-1" });

            var json = ReadDecryptedQueue();
            var token = JObject.Parse(json);
            ((JObject)token["Block"]!["Snapshot"]!)["Flavor"] = "FUTURE_FLAVOR";
            WriteEncryptedQueue(token.ToString(Formatting.None));

            var repo = new UploadQueueRepository(tempDir);

            Assert.That(repo.HasCorruptQueueWarning, Is.False, "An unknown Flavor string alone must not quarantine the whole queue");
            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "The block must STILL be present after reload -- an unrecognized flavor must never release the latch");
            Assert.That(repo.CurrentBlock.Snapshot!.UsedBytes, Is.EqualTo(5), "The other snapshot fields must still load normally");
            Assert.That(repo.CurrentBlock.Snapshot!.Flavor, Is.EqualTo(QuotaBlockFlavor.StorageLimitExceeded),
                "An unknown flavor value must degrade to the safe default, exactly like the missing-field case");
        }

        [Test]
        public async Task PersistedFlavor_Int64BoundaryOverflow_4294967297_DoesNotWrapToPlanRequired_BlockRetained_DefaultsToStorageLimitExceeded()
        {
            // Pass I: an unchecked long->int cast wraps 4294967297 (2^32 + 1) down to its low 32 bits --
            // 1 -- which IS QuotaBlockFlavor.PlanRequired's ordinal. That would silently mis-display a
            // genuine storage-limit block as a plan-required one. The Int64-range check must reject the
            // value BEFORE the cast, defaulting to the safe flavor instead of wrapping.
            var seedRepo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await seedRepo.EnqueueAsync(job);
            await seedRepo.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id,
                new UploadBlockSnapshot { UsedBytes = 5, LimitBytes = 10, Flavor = QuotaBlockFlavor.StorageLimitExceeded },
                new UploadBlockIdentityInput { EndpointUrl = "https://api.astrovault.io", ApiKey = "key-1" });

            var json = ReadDecryptedQueue();
            var token = JObject.Parse(json);
            ((JObject)token["Block"]!["Snapshot"]!)["Flavor"] = 4294967297L;
            WriteEncryptedQueue(token.ToString(Formatting.None));

            var repo = new UploadQueueRepository(tempDir);

            Assert.That(repo.HasCorruptQueueWarning, Is.False, "A beyond-int32 Flavor integer alone must not quarantine the whole queue");
            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "The block must STILL be present after reload -- an unnormalizable flavor must never release the latch");
            Assert.That(repo.CurrentBlock.Snapshot!.Flavor, Is.EqualTo(QuotaBlockFlavor.StorageLimitExceeded),
                "4294967297 must NOT wrap to PlanRequired via an unchecked long->int cast -- it must degrade to the safe default");
        }

        [Test]
        public async Task PersistedFlavor_BeyondInt64Integer_DecodeThrowsButIsCaught_BlockRetained_DefaultsToStorageLimitExceeded()
        {
            // Pass I: Value<long>() itself throws (an overflow) when the persisted JSON integer exceeds
            // Int64 range (a JSON BigInteger literal). That throw must be caught LOCALLY and degrade to
            // the default flavor -- it must never escape into ParseBlock's outer catch-all, which would
            // convert the WHOLE block to UploadBlockState.None and release the stop-queue latch.
            var seedRepo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await seedRepo.EnqueueAsync(job);
            await seedRepo.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id,
                new UploadBlockSnapshot { UsedBytes = 5, LimitBytes = 10, Flavor = QuotaBlockFlavor.StorageLimitExceeded },
                new UploadBlockIdentityInput { EndpointUrl = "https://api.astrovault.io", ApiKey = "key-1" });

            var json = ReadDecryptedQueue();
            var token = JObject.Parse(json);
            ((JObject)token["Block"]!["Snapshot"]!)["Flavor"] = 0; // placeholder, rewritten below to a raw BigInteger literal
            var rewritten = token.ToString(Formatting.None).Replace("\"Flavor\":0", "\"Flavor\":99999999999999999999999999");
            WriteEncryptedQueue(rewritten);

            var repo = new UploadQueueRepository(tempDir);

            Assert.That(repo.HasCorruptQueueWarning, Is.False, "A beyond-Int64 Flavor integer alone must not quarantine the whole queue");
            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded),
                "The block must STILL be present after reload -- Value<long>() throwing must never escape into the outer catch-all that would unlatch");
            Assert.That(repo.CurrentBlock.Snapshot!.Flavor, Is.EqualTo(QuotaBlockFlavor.StorageLimitExceeded),
                "An undecodable (beyond-Int64) flavor value must degrade to the safe default, exactly like the unknown-string case");
        }

        [Test]
        public async Task PersistedFlavor_ValidIntegerForm_1_StillHonored_NormalizesToPlanRequired()
        {
            // Counterpart regression: the new Int64 range-check and try/catch must not become
            // over-eager -- a genuine in-range integer form of the enum must still normalize correctly,
            // exactly like before this pass's hardening.
            var seedRepo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await seedRepo.EnqueueAsync(job);
            await seedRepo.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id,
                new UploadBlockSnapshot { UsedBytes = 5, LimitBytes = 10, Flavor = QuotaBlockFlavor.StorageLimitExceeded },
                new UploadBlockIdentityInput { EndpointUrl = "https://api.astrovault.io", ApiKey = "key-1" });

            var json = ReadDecryptedQueue();
            var token = JObject.Parse(json);
            ((JObject)token["Block"]!["Snapshot"]!)["Flavor"] = 1;
            WriteEncryptedQueue(token.ToString(Formatting.None));

            var repo = new UploadQueueRepository(tempDir);

            Assert.That(repo.HasCorruptQueueWarning, Is.False, "A valid in-range integer Flavor alone must not quarantine the whole queue");
            Assert.That(repo.CurrentBlock.Kind, Is.EqualTo(UploadBlockKind.QuotaExceeded), "The block must still be present after reload");
            Assert.That(repo.CurrentBlock.Snapshot!.Flavor, Is.EqualTo(QuotaBlockFlavor.PlanRequired),
                "A valid in-range integer form (1) must still normalize to PlanRequired -- the fix must not regress the honored-integer path");
        }

        [Test]
        public async Task SchemaEnvelope_FlavorSerializedByName_NotInteger()
        {
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);
            await repo.SetBlockAsync(UploadBlockKind.QuotaExceeded, job.Id,
                new UploadBlockSnapshot { Flavor = QuotaBlockFlavor.PlanRequired },
                new UploadBlockIdentityInput { EndpointUrl = "https://x", ApiKey = "k" });

            var savedJson = ReadDecryptedQueue();
            Assert.That(savedJson, Does.Contain("\"PlanRequired\""),
                "Flavor should be serialized by name (StringEnumConverter), matching Block Kind / job Status");
        }

        [Test]
        public async Task SchemaEnvelope_SchemaVersionIsV3_WhenSaved()
        {
            var repo = new UploadQueueRepository(tempDir);
            await repo.EnqueueAsync(CreatePendingJob());

            var savedJson = ReadDecryptedQueue();
            var token = JToken.Parse(savedJson);
            Assert.That(token["SchemaVersion"]!.ToObject<int>(), Is.EqualTo(3));
        }

        // ---- helpers (mirror QueueHardeningTests' DPAPI seed helpers) ----

        private static UploadJob CreatePendingJob()
        {
            return new UploadJob
            {
                Id = Guid.NewGuid(),
                LocalPath = @"D:\Astro\M31\Light\" + Guid.NewGuid().ToString("N") + ".fits",
                RelativePath = "M31/Light/test.fits",
                FileSize = 10_485_760,
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
            var args = new object?[] { bytes, false };
            return (string)decryptMethod!.Invoke(null, args)!;
        }
    }
}
