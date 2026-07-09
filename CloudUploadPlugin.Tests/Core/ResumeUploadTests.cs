using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Core;
using Astrovault.Interfaces;
using Astrovault.Models;
using CloudUploadPlugin.Tests.Helpers;
using Moq;
using Newtonsoft.Json;

namespace CloudUploadPlugin.Tests.Core
{
    /// <summary>
    /// Tests for resume flow, session expiry handling, session restart limits,
    /// UploadManager chunk state persistence, and UploadJob serialization.
    /// </summary>
    [TestFixture]
    [Category("ChunkedUpload")]
    public class ResumeUploadTests
    {
        private MockHttpMessageHandler mockHandler;
        private Mock<IAuthManager> mockAuthManager;
        private Mock<IUploadQueueRepository> mockQueueRepo;
        private AstrovaultApiClient sut;
        private string? tempFilePath;

        [SetUp]
        public void SetUp()
        {
            mockHandler = new MockHttpMessageHandler();
            mockAuthManager = new Mock<IAuthManager>();
            mockAuthManager.Setup(a => a.GetApiKey()).Returns("test-api-key");

            mockQueueRepo = new Mock<IUploadQueueRepository>();
            mockQueueRepo.Setup(r => r.UpdateJobChunkStateAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<List<int>>(), It.IsAny<int>()))
                .Returns(Task.CompletedTask);

            sut = new AstrovaultApiClient(mockAuthManager.Object, "https://api.test.io", mockHandler, mockQueueRepo.Object);
        }

        [TearDown]
        public void TearDown()
        {
            sut?.Dispose();
            mockHandler?.Dispose();
            if (tempFilePath != null && File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }

        private string CreateTempFile(long sizeInBytes)
        {
            tempFilePath = Path.GetTempFileName();
            var random = new Random(42);
            var buffer = new byte[Math.Min(sizeInBytes, 65536)];
            using var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write);
            long written = 0;
            while (written < sizeInBytes)
            {
                var toWrite = (int)Math.Min(buffer.Length, sizeInBytes - written);
                random.NextBytes(buffer);
                fs.Write(buffer, 0, toWrite);
                written += toWrite;
            }
            return tempFilePath;
        }

        // ====================================================================
        // Resume Flow Tests
        // ====================================================================

        [Test]
        public async Task Resume_ValidSession_UploadsOnlyMissingChunks()
        {
            // Arrange: 25 MB file = 5 chunks, chunks 0-2 already uploaded
            long fileSize = 25 * 1024 * 1024;
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateResumeUploadJob(
                uploadSessionId: "sess-resume-01",
                chunksSent: new List<int> { 0, 1, 2 },
                totalChunks: 5,
                fileSize: fileSize);
            job.LocalPath = filePath;

            // Mock status response: server confirms chunks 0,1,2 received
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId = "sess-resume-01", status = "in_progress", receivedChunks = new[] { 0, 1, 2 }, totalChunks = 5, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });

            // Mock chunks 3 and 4
            for (int i = 3; i <= 4; i++)
            {
                mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { chunkIndex = i, received = true }),
                        Encoding.UTF8, "application/json")
                });
            }

            // Mock complete
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "M31/Light/001.fits", fileSize }),
                    Encoding.UTF8, "application/json")
            });

            // Act
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert
            Assert.That(result.Success, Is.True);
            // 1 (status) + 2 (chunks 3,4) + 1 (complete) = 4 requests (NOT 5 chunk uploads)
            Assert.That(mockHandler.Requests.Count, Is.EqualTo(4));

            // Verify only chunks 3,4 were PUT (not 0,1,2)
            var putRequests = mockHandler.Requests.Where(r => r.Method == HttpMethod.Put).ToList();
            Assert.That(putRequests.Count, Is.EqualTo(2));
            Assert.That(putRequests[0].RequestUri!.PathAndQuery, Does.Contain("/chunks/3"));
            Assert.That(putRequests[1].RequestUri!.PathAndQuery, Does.Contain("/chunks/4"));
        }

        [Test]
        public async Task Resume_ExpiredSession_StartsNewSession()
        {
            // Arrange: job has existing session, server returns 410
            long fileSize = 10 * 1024 * 1024; // 2 chunks
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateResumeUploadJob(
                uploadSessionId: "sess-expired-01",
                chunksSent: new List<int> { 0 },
                totalChunks: 2,
                fileSize: fileSize);
            job.LocalPath = filePath;
            job.SessionRestartCount = 0;

            // Mock status returns 410 (expired)
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.Gone)
            {
                Content = new StringContent("{\"error\":{\"code\":\"upload.expired\",\"message\":\"Session expired\"}}")
            });

            // Mock fresh initiate
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId = "sess-new-01", chunkSize = 5242880, totalChunks = 2, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });

            // Mock 2 chunks + complete
            for (int i = 0; i < 2; i++)
            {
                mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { chunkIndex = i, received = true }),
                        Encoding.UTF8, "application/json")
                });
            }
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "M31/Light/001.fits", fileSize }),
                    Encoding.UTF8, "application/json")
            });

            // Act
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(job.SessionRestartCount, Is.EqualTo(1));
            // Should have: 1 (status-410) + 1 (initiate) + 2 (chunks) + 1 (complete) = 5
            Assert.That(mockHandler.Requests.Count, Is.EqualTo(5));
            // New initiate was called
            Assert.That(mockHandler.Requests[1].RequestUri!.PathAndQuery, Does.Contain("v1/storage/nina/uploads/initiate"));
        }

        [Test]
        public async Task Resume_NotFoundSession_StartsNewSession()
        {
            // Arrange: job has existing session, server returns 404
            long fileSize = 10 * 1024 * 1024; // 2 chunks
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateResumeUploadJob(
                uploadSessionId: "sess-notfound-01",
                chunksSent: new List<int> { 0 },
                totalChunks: 2,
                fileSize: fileSize);
            job.LocalPath = filePath;
            job.SessionRestartCount = 0;

            // Mock status returns 404 (not found)
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":{\"code\":\"upload.not_found\",\"message\":\"Not found\"}}")
            });

            // Mock fresh initiate
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId = "sess-new-02", chunkSize = 5242880, totalChunks = 2, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });

            // Mock 2 chunks + complete
            for (int i = 0; i < 2; i++)
            {
                mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { chunkIndex = i, received = true }),
                        Encoding.UTF8, "application/json")
                });
            }
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "M31/Light/001.fits", fileSize }),
                    Encoding.UTF8, "application/json")
            });

            // Act
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(job.SessionRestartCount, Is.EqualTo(1));
        }

        [Test]
        public async Task Resume_ExceedsRestartLimit_ReturnsPermanentFailure()
        {
            // Arrange: SessionRestartCount already at max (D-10 raised the cap from 2 to 10)
            long fileSize = 10 * 1024 * 1024;
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateResumeUploadJob(
                uploadSessionId: "sess-limit-01",
                chunksSent: new List<int> { 0 },
                totalChunks: 2,
                fileSize: fileSize);
            job.LocalPath = filePath;
            job.SessionRestartCount = 10; // Already at the (D-10 raised) max

            // Mock status returns 410 (expired)
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.Gone)
            {
                Content = new StringContent("{\"error\":{\"code\":\"upload.expired\",\"message\":\"Session expired\"}}")
            });

            // Act
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert: permanent failure, not retried
            Assert.That(result.Success, Is.False);
            Assert.That(result.IsPermanent, Is.True);
            Assert.That(result.ErrorMessage, Does.Contain("expired too many times"));
            Assert.That(mockHandler.Requests.Count, Is.EqualTo(1)); // Only the status check
        }

        [Test]
        public async Task Resume_ClearsStateOnFreshSession()
        {
            // Arrange: job has existing session, server returns 410 -> fresh start.
            // File length is consistent with the persisted totalChunks (2 chunks) so the resume
            // TOCTOU guard passes and the 410-expiry restart path is exercised.
            long fileSize = 10 * 1024 * 1024; // 2 chunks
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateResumeUploadJob(
                uploadSessionId: "sess-stale-01",
                chunksSent: new List<int> { 0 },
                totalChunks: 2,
                fileSize: fileSize);
            job.LocalPath = filePath;
            job.SessionRestartCount = 0;

            // Mock status returns 410
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.Gone)
            {
                Content = new StringContent("{\"error\":{\"code\":\"upload.expired\",\"message\":\"Expired\"}}")
            });

            // Mock fresh initiate (2 chunks for the 10 MB file)
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId = "sess-fresh-01", chunkSize = 5242880, totalChunks = 2, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });

            // Mock 2 chunks + complete
            for (int i = 0; i < 2; i++)
            {
                mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { chunkIndex = i, received = true }),
                        Encoding.UTF8, "application/json")
                });
            }
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "test", fileSize }),
                    Encoding.UTF8, "application/json")
            });

            // Act
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert: state was cleared and new session established
            Assert.That(result.Success, Is.True);
            Assert.That(job.UploadSessionId, Is.EqualTo("sess-fresh-01"));
            Assert.That(job.TotalChunks, Is.EqualTo(2));
            Assert.That(job.ChunksSent, Does.Contain(0));
        }

        [Test]
        public async Task SessionExpiry_MidUpload_AutoRestarts()
        {
            // Arrange: 15 MB = 3 chunks. Chunk 1 returns 410 mid-upload
            long fileSize = 15 * 1024 * 1024;
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);
            job.SessionRestartCount = 0;

            // === First attempt ===
            // Initiate
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId = "sess-mid-01", chunkSize = 5242880, totalChunks = 3, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });
            // Chunk 0 succeeds
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { chunkIndex = 0, received = true }),
                    Encoding.UTF8, "application/json")
            });
            // Chunk 1 returns 410 (session expired mid-upload)
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.Gone)
            {
                Content = new StringContent("{\"error\":{\"code\":\"upload.expired\",\"message\":\"Session expired\"}}")
            });

            // === Second attempt (auto-restart) ===
            // Fresh initiate
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId = "sess-mid-02", chunkSize = 5242880, totalChunks = 3, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });
            // All 3 chunks succeed
            for (int i = 0; i < 3; i++)
            {
                mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { chunkIndex = i, received = true }),
                        Encoding.UTF8, "application/json")
                });
            }
            // Complete
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "test", fileSize }),
                    Encoding.UTF8, "application/json")
            });

            // Act
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert
            Assert.That(result.Success, Is.True);
            Assert.That(job.SessionRestartCount, Is.EqualTo(1));
        }

        [Test]
        public async Task SessionExpiry_DoesNotIncrementRetryCount()
        {
            // Arrange
            long fileSize = 5 * 1024 * 1024;
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateResumeUploadJob(
                uploadSessionId: "sess-retry-check",
                chunksSent: new List<int>(),
                totalChunks: 1,
                fileSize: fileSize);
            job.LocalPath = filePath;
            job.RetryCount = 0;
            job.SessionRestartCount = 0;

            // Mock status returns 410
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.Gone)
            {
                Content = new StringContent("{\"error\":{\"code\":\"upload.expired\",\"message\":\"Expired\"}}")
            });

            // Fresh initiate (1 chunk)
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId = "sess-retry-new", chunkSize = 5242880, totalChunks = 1, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });
            // Chunk + complete
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { chunkIndex = 0, received = true }),
                    Encoding.UTF8, "application/json")
            });
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "test", fileSize }),
                    Encoding.UTF8, "application/json")
            });

            // Act
            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert: RetryCount unchanged, only SessionRestartCount incremented
            Assert.That(result.Success, Is.True);
            Assert.That(job.RetryCount, Is.EqualTo(0));
            Assert.That(job.SessionRestartCount, Is.EqualTo(1));
        }

        // ====================================================================
        // D-11: resume reconciliation against the server's authoritative chunk set
        // ====================================================================

        /// <summary>
        /// D-11: on resume, ChunksSent is reconciled to the server's receivedChunks (the
        /// authoritative set) even when local state disagrees, and exactly the missing chunks are
        /// re-sent. Here the local job believes 0,1,2,3 were sent but the server only has 0,1 —
        /// the client must re-send 2,3,4 (NOT trust the stale local 0,1,2,3).
        /// </summary>
        [Test]
        [Category("Resume")]
        public async Task Resume_ReconcilesChunksSentToServerAuthoritativeSet()
        {
            long fileSize = 25 * 1024 * 1024; // 5 chunks
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateResumeUploadJob(
                uploadSessionId: "sess-reconcile",
                chunksSent: new List<int> { 0, 1, 2, 3 }, // stale local belief
                totalChunks: 5,
                fileSize: fileSize);
            job.LocalPath = filePath;

            // Server authoritative: only chunks 0,1 actually received.
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId = "sess-reconcile", status = "in_progress", receivedChunks = new[] { 0, 1 }, totalChunks = 5, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });

            // Expect chunks 2,3,4 to be re-sent.
            for (int i = 2; i <= 4; i++)
            {
                mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { chunkIndex = i, received = true }),
                        Encoding.UTF8, "application/json")
                });
            }
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "M31/Light/001.fits", fileSize }),
                    Encoding.UTF8, "application/json")
            });

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.True);
            // ChunksSent reconciled to server set + the newly-sent chunks = full 0..4.
            Assert.That(job.ChunksSent.OrderBy(i => i), Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));

            // Exactly chunks 2,3,4 were PUT (server-authoritative reconciliation; not the stale local set).
            var putIndices = mockHandler.Requests
                .Where(r => r.Method == HttpMethod.Put)
                .Select(r => int.Parse(r.RequestUri!.AbsolutePath.Split('/').Last()))
                .OrderBy(i => i)
                .ToList();
            Assert.That(putIndices, Is.EqualTo(new[] { 2, 3, 4 }));
        }

        // ====================================================================
        // Resume TOCTOU: file length no longer matches the persisted chunk map
        // ====================================================================

        /// <summary>
        /// Resume TOCTOU: if the file's current length implies a different chunk count than the
        /// persisted session (job.TotalChunks), the stale session is restarted with a fresh
        /// initiate rather than uploading against the stale chunk map.
        /// </summary>
        [Test]
        [Category("Resume")]
        public async Task Resume_FileLengthChanged_RestartsSessionInsteadOfStaleMap()
        {
            // File on disk is now 15 MB (3 chunks) but the persisted session says 2 chunks (10 MB).
            long currentFileSize = 15 * 1024 * 1024;
            var filePath = CreateTempFile(currentFileSize);
            var job = TestDataFactory.CreateResumeUploadJob(
                uploadSessionId: "sess-toctou",
                chunksSent: new List<int> { 0 },
                totalChunks: 2,            // stale: persisted when the file was ~10 MB
                fileSize: currentFileSize); // upload pipeline uses the current size
            job.LocalPath = filePath;
            job.SessionRestartCount = 0;

            // No status call expected — the TOCTOU guard restarts BEFORE querying the stale session.
            // Fresh initiate for the restarted session (3 chunks).
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId = "sess-toctou-fresh", chunkSize = 5242880, totalChunks = 3, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });
            for (int i = 0; i < 3; i++)
            {
                mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { chunkIndex = i, received = true }),
                        Encoding.UTF8, "application/json")
                });
            }
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "M31/Light/001.fits", fileSize = currentFileSize }),
                    Encoding.UTF8, "application/json")
            });

            var result = await sut.UploadFileAsync(job, null, CancellationToken.None);

            Assert.That(result.Success, Is.True);
            Assert.That(job.SessionRestartCount, Is.EqualTo(1), "the stale-map TOCTOU must restart the session");
            Assert.That(job.UploadSessionId, Is.EqualTo("sess-toctou-fresh"));
            Assert.That(job.TotalChunks, Is.EqualTo(3));
            // First request is the fresh initiate (no status call against the stale session).
            Assert.That(mockHandler.Requests[0].RequestUri!.PathAndQuery, Does.Contain("uploads/initiate"));
        }

        // ====================================================================
        // Chunk State Persistence Tests
        // ====================================================================

        [Test]
        public async Task ChunkStatePersistence_CalledAfterEachChunk()
        {
            // Arrange: 15 MB = 3 chunks
            long fileSize = 15 * 1024 * 1024;
            var filePath = CreateTempFile(fileSize);
            var job = TestDataFactory.CreateChunkedUploadJob(localPath: filePath, fileSize: fileSize);

            // Initiate
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { uploadId = "sess-persist", chunkSize = 5242880, totalChunks = 3, expiresAt = "2026-12-31T23:59:59Z" }),
                    Encoding.UTF8, "application/json")
            });
            // 3 chunks
            for (int i = 0; i < 3; i++)
            {
                mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(new { chunkIndex = i, received = true }),
                        Encoding.UTF8, "application/json")
                });
            }
            // Complete
            mockHandler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(new { remoteUrl = "https://cdn.test.io/file.fits", remotePath = "test", fileSize }),
                    Encoding.UTF8, "application/json")
            });

            // Act
            await sut.UploadFileAsync(job, null, CancellationToken.None);

            // Assert: UpdateJobChunkStateAsync called 3 times (once per chunk)
            mockQueueRepo.Verify(
                r => r.UpdateJobChunkStateAsync(
                    job.Id,
                    "sess-persist",
                    It.IsAny<List<int>>(),
                    3),
                Times.Exactly(3));
        }

        // ====================================================================
        // Serialization Tests
        // ====================================================================

        [Test]
        public void ResumeState_SurvivesJsonSerialization()
        {
            // Arrange
            var job = TestDataFactory.CreateResumeUploadJob(
                uploadSessionId: "sess-serialize",
                chunksSent: new List<int> { 0, 1, 2 },
                totalChunks: 5,
                fileSize: 26_214_400);
            job.SessionRestartCount = 1;

            // Act: serialize and deserialize
            var json = JsonConvert.SerializeObject(job);
            var deserialized = JsonConvert.DeserializeObject<UploadJob>(json);

            // Assert: all resume fields preserved
            Assert.That(deserialized, Is.Not.Null);
            Assert.That(deserialized!.UploadSessionId, Is.EqualTo("sess-serialize"));
            Assert.That(deserialized.ChunksSent, Is.EquivalentTo(new[] { 0, 1, 2 }));
            Assert.That(deserialized.TotalChunks, Is.EqualTo(5));
            Assert.That(deserialized.SessionRestartCount, Is.EqualTo(1));
        }
    }
}
