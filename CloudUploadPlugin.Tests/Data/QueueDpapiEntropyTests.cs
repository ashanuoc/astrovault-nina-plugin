using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Astrovault.Data;
using Astrovault.Models;

namespace Astrovault.Tests.Data
{
    /// <summary>
    /// DPAPI app-entropy hardening tests for queue.dat (SC2 / DPAPI-entropy, plan 19-04):
    /// <list type="bullet">
    /// <item>DPAPI-1: write-with-entropy → read-with-entropy round-trips through a second repository.</item>
    /// <item>DPAPI-2: a legacy null-entropy queue.dat (built by re-protecting the repository's OWN
    /// entropy-decrypted plaintext with null entropy — exact serializer output, no hand-rolled JSON)
    /// loads on read and is rewritten so it is no longer null-entropy (migration-on-read).</item>
    /// <item>DPAPI-3: a garbage/foreign blob yields an empty live queue + a queue.dat.corrupt-* quarantine
    /// file, with no throw — proving migration adds no new silent behavior (the existing quarantine path).</item>
    /// </list>
    /// The DPAPI-2 legacy fixture reuses the repository's exact serializer/entropy via the narrow internal
    /// seam <see cref="UploadQueueRepository.DpapiEntropyForTests"/> (InternalsVisibleTo), per review MED-6.
    /// </summary>
    [TestFixture]
    [Category("Security")]
    [Category("Queue")]
    public class QueueDpapiEntropyTests
    {
        private string tempDir = null!;

        [SetUp]
        public void Setup()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "queue_dpapi_entropy_" + Guid.NewGuid().ToString("N"));
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
        // DPAPI-1: entropy round-trip
        // ====================================================================

        [Test]
        public async Task QueueDpapiEntropy_WriteThenRead_RoundTripsWithEntropy()
        {
            // Write-with-entropy: a real repository persists an enqueued job to queue.dat.
            var repo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await repo.EnqueueAsync(job);

            // Read-with-entropy: a SECOND repository over the SAME folder must load that job back.
            // (If the write used entropy but the read used null — or vice versa — Unprotect would throw
            // and the queue would quarantine to empty, failing this assertion.)
            var reloaded = new UploadQueueRepository(tempDir);
            Assert.That(reloaded.HasCorruptQueueWarning, Is.False,
                "An entropy-written queue.dat must read back cleanly, not quarantine");

            var pending = (await reloaded.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
            Assert.That(pending, Has.Count.EqualTo(1),
                "The enqueued job must survive the entropy write→read round-trip");
            Assert.That(pending[0].Id, Is.EqualTo(job.Id),
                "The reloaded job must be the same job that was enqueued");
        }

        [Test]
        public void QueueDpapiEntropy_OnDiskFile_IsNotReadableWithNullEntropy()
        {
            // A freshly written queue.dat must require the app entropy: decrypting it with NULL entropy
            // (the legacy scheme) must throw. This is the direct proof that entropy is actually applied.
            var repo = new UploadQueueRepository(tempDir);
            repo.EnqueueAsync(CreatePendingJob()).GetAwaiter().GetResult();

            var bytes = File.ReadAllBytes(Path.Combine(tempDir, "queue.dat"));
            Assert.That(
                () => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser),
                Throws.TypeOf<CryptographicException>(),
                "queue.dat written with app entropy must NOT be decryptable with null entropy");

            // ...but it IS decryptable with the app entropy (sanity check on the seam).
            Assert.That(
                () => ProtectedData.Unprotect(bytes, UploadQueueRepository.DpapiEntropyForTests, DataProtectionScope.CurrentUser),
                Throws.Nothing,
                "queue.dat must be decryptable with the repository's app entropy");
        }

        // ====================================================================
        // DPAPI-2: legacy null-entropy migration via exact-serializer reconstruction (MED-6)
        // ====================================================================

        [Test]
        public async Task QueueDpapiEntropy_LegacyNullEntropyFile_MigratesOnReadAndIsRewrittenWithEntropy()
        {
            var queueDat = Path.Combine(tempDir, "queue.dat");

            // 1. Arrange a REAL queue.dat first: a repo enqueues a job and persists it WITH entropy.
            var seedRepo = new UploadQueueRepository(tempDir);
            var job = CreatePendingJob();
            await seedRepo.EnqueueAsync(job);

            // 2. Recover the EXACT plaintext (QueueEnvelope + jobs, the repo's own serializer output) by
            //    decrypting the produced file with the repository's app entropy via the internal seam.
            var entropyBytes = File.ReadAllBytes(queueDat);
            var json = Encoding.UTF8.GetString(
                ProtectedData.Unprotect(entropyBytes, UploadQueueRepository.DpapiEntropyForTests, DataProtectionScope.CurrentUser));

            // 3. Re-protect that EXACT plaintext with NULL entropy and overwrite queue.dat. This produces a
            //    byte-for-byte legacy fixture (the repo's own serializer output, just null-entropy) — no
            //    hand-rolled JSON, no guessed casing/options.
            File.WriteAllBytes(queueDat,
                ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser));

            // Precondition: the fixture really IS null-entropy (entropy decrypt fails, null decrypt works).
            var legacyBytes = File.ReadAllBytes(queueDat);
            Assert.That(
                () => ProtectedData.Unprotect(legacyBytes, UploadQueueRepository.DpapiEntropyForTests, DataProtectionScope.CurrentUser),
                Throws.TypeOf<CryptographicException>(),
                "Precondition: the legacy fixture must NOT decrypt with app entropy");
            Assert.That(
                () => ProtectedData.Unprotect(legacyBytes, null, DataProtectionScope.CurrentUser),
                Throws.Nothing,
                "Precondition: the legacy fixture must decrypt with null entropy");

            // 4. A FRESH repository over the folder must load the job via migration-on-read (null fallback)
            //    and NOT quarantine.
            var migratingRepo = new UploadQueueRepository(tempDir);
            Assert.That(migratingRepo.HasCorruptQueueWarning, Is.False,
                "A legacy null-entropy file is valid, not corrupt — it must migrate, not quarantine");

            var pending = (await migratingRepo.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
            Assert.That(pending, Has.Count.EqualTo(1), "The legacy job must load via migration-on-read");
            Assert.That(pending[0].Id, Is.EqualTo(job.Id), "The migrated job must be the original job");

            // 5. The on-disk file must now be entropy-protected (no longer null-entropy): null decrypt
            //    throws, while a fresh repo over it still loads the job.
            var migratedBytes = File.ReadAllBytes(queueDat);
            Assert.That(
                () => ProtectedData.Unprotect(migratedBytes, null, DataProtectionScope.CurrentUser),
                Throws.TypeOf<CryptographicException>(),
                "After migration, queue.dat must NO LONGER be decryptable with null entropy");

            var afterMigration = new UploadQueueRepository(tempDir);
            Assert.That(afterMigration.HasCorruptQueueWarning, Is.False,
                "The rewritten (entropy) queue.dat must still read cleanly");
            var stillThere = (await afterMigration.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
            Assert.That(stillThere, Has.Count.EqualTo(1),
                "The job must still load from the entropy-rewritten file");
            Assert.That(stillThere[0].Id, Is.EqualTo(job.Id));
        }

        // ====================================================================
        // DPAPI-3: garbage/foreign blob falls through to the existing quarantine path
        // ====================================================================

        [Test]
        public async Task QueueDpapiEntropy_GarbageBlob_QuarantinesToEmptyQueue_NoThrow()
        {
            var queueDat = Path.Combine(tempDir, "queue.dat");

            // A garbage/foreign blob is decryptable with NEITHER the app entropy NOR null entropy, so both
            // Unprotect attempts in DecryptData throw -> it must fall through to the EXISTING
            // QuarantineCorruptQueue path (no new silent behavior).
            File.WriteAllBytes(queueDat, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            // Constructing the repo must NOT throw.
            UploadQueueRepository repo = null!;
            Assert.That(() => repo = new UploadQueueRepository(tempDir), Throws.Nothing,
                "A garbage queue.dat must be handled gracefully, not throw");

            // The live queue must be EMPTY (no auto-restore).
            var pending = (await repo.GetJobsByStatusAsync(UploadStatus.Pending)).ToList();
            var failed = (await repo.GetJobsByStatusAsync(UploadStatus.Failed)).ToList();
            Assert.That(pending, Is.Empty, "Live queue must be empty after a garbage blob is quarantined");
            Assert.That(failed, Is.Empty, "Live queue must be empty after a garbage blob is quarantined");

            // A queue.dat.corrupt-* quarantine file must exist (the existing failure path).
            var corruptFiles = Directory.GetFiles(tempDir, "queue.dat.corrupt-*");
            Assert.That(corruptFiles, Has.Length.EqualTo(1),
                "A garbage blob must be quarantined to queue.dat.corrupt-<timestamp>, proving no new silent behavior");
            Assert.That(repo.HasCorruptQueueWarning, Is.True,
                "The corrupt-queue warning must be surfaced after quarantine");
        }

        /// <summary>
        /// Helper to create a minimal pending job for testing (mirrors QueueHardeningTests.CreatePendingJob).
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
