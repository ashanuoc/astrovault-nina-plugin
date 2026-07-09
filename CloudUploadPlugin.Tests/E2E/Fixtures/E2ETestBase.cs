using System;
using System.IO;
using System.Threading.Tasks;
using Astrovault.Models;
using NUnit.Framework;

namespace CloudUploadPlugin.Tests.E2E
{
    /// <summary>
    /// Shared base class for all E2E test fixtures.
    /// Provides fixture access, per-test temp directory lifecycle,
    /// server state reset, and common test helpers.
    /// </summary>
    public abstract class E2ETestBase
    {
        protected MockServerFixture Fixture { get; private set; }
        protected string TempDir { get; private set; }

        /// <summary>
        /// Returns the fixture for this test class. Override in B2 tests to return B2MockServerFixture.Instance.
        /// </summary>
        protected virtual MockServerFixture GetFixture() => MockServerFixture.Instance;

        [SetUp]
        public async Task BaseSetUp()
        {
            Fixture = GetFixture();
            Assert.That(Fixture, Is.Not.Null, "MockServerFixture.Instance is null -- did [SetUpFixture] run?");
            Assert.That(Fixture.IsServerRunning, Is.True, $"Mock server is not running. Logs:\n{Fixture.GetCapturedLogs()}");

            TempDir = Path.Combine(Path.GetTempPath(), $"astrovault-e2e-{Guid.NewGuid():N}");
            Directory.CreateDirectory(TempDir);

            await Fixture.ResetServerState();
        }

        [TearDown]
        public void BaseTearDown()
        {
            Fixture?.DumpLogsOnFailure();
            try
            {
                if (Directory.Exists(TempDir))
                    Directory.Delete(TempDir, recursive: true);
            }
            catch { /* best effort cleanup */ }
        }

        /// <summary>
        /// Creates an UploadJob pointing to the given file with standard test metadata.
        /// </summary>
        protected UploadJob CreateUploadJob(string filePath, long fileSize)
        {
            return new UploadJob
            {
                Id = Guid.NewGuid(),
                LocalPath = filePath,
                RelativePath = "TestTarget/" + Path.GetFileName(filePath),
                FileSize = fileSize,
                CapturedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow,
                Status = UploadStatus.Pending,
                RetryCount = 0,
                Filter = "L",
                Duration = 120.0,
                FileType = "BIN",
                MetadataJson = "{}"
            };
        }
    }
}
