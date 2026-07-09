using System;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Integration;
using Astrovault.Interfaces;
using Astrovault.Models;
using CloudUploadPlugin.Tests.Helpers;
using Moq;
using NINA.Image.ImageData;
using NINA.WPF.Base.Interfaces.Mediator;

namespace CloudUploadPlugin.Tests.Integration
{
    /// <summary>
    /// REL-03 / REL-13 lifecycle tests: an in-flight fire-and-forget enqueue is tracked and
    /// flushed on shutdown so a capture racing NINA's close still reaches disk, and the teardown
    /// stop chain is awaited end-to-end (non-blocking, no .Wait() on the UI thread).
    /// </summary>
    [TestFixture]
    [Category("Lifecycle")]
    public class ShutdownLifecycleTests
    {
        private Mock<IImageSaveMediator> mockMediator;
        private Mock<IUploadManager> mockUploadManager;
        private Mock<IPathResolver> mockPathResolver;

        [SetUp]
        public void Setup()
        {
            mockMediator = new Mock<IImageSaveMediator>();
            mockUploadManager = new Mock<IUploadManager>();
            mockPathResolver = new Mock<IPathResolver>();

            mockPathResolver.Setup(p => p.GetUploadPath(It.IsAny<Uri>()))
                .Returns("M31/Light/001.fits");
        }

        private static ImageSavedEventArgs CreateEventArgs(string filePath)
        {
            var uri = new Uri(filePath.Replace('\\', '/'), UriKind.Absolute);
            var metaData = new ImageMetaData();
            metaData.FilterWheel.Filter = "L";
            metaData.Image.ExposureTime = 300.0;

            return new ImageSavedEventArgs
            {
                PathToImage = uri,
                MetaData = metaData,
                Image = null,
                Statistics = null,
                StarDetectionAnalysis = null
            };
        }

        [Test]
        public async Task FlushPendingAsync_AwaitsInFlightEnqueue_SoCaptureReachesDisk()
        {
            // REL-03: an enqueue that is still running when shutdown begins must be flushed
            // (awaited) before cancellation, so the job actually reaches disk and is not lost.
            var enqueueGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var enqueueEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var enqueueCompleted = false;

            mockUploadManager
                .Setup(m => m.EnqueueJobAsync(It.IsAny<UploadJob>()))
                .Returns(async () =>
                {
                    enqueueEntered.TrySetResult(true);
                    await enqueueGate.Task;
                    enqueueCompleted = true;
                });

            var sut = new ImageSaveListener(
                mockMediator.Object,
                mockUploadManager.Object,
                mockPathResolver.Object,
                () => true);
            sut.StartListening();

            // Fire the capture; the enqueue blocks inside the gate (still in flight).
            mockMediator.Raise(m => m.ImageSaved += null, this, CreateEventArgs(@"file:///D:/Astro/M31/Light/001.fits"));

            // Wait until the enqueue is actually in flight before beginning shutdown.
            await enqueueEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            sut.StopListening();

            // Begin the flush concurrently, then release the in-flight enqueue.
            var flushTask = sut.FlushPendingAsync(TimeSpan.FromSeconds(5));
            enqueueGate.SetResult(true);

            await flushTask;

            Assert.That(enqueueCompleted, Is.True,
                "FlushPendingAsync must await the in-flight enqueue so the capture reaches disk before shutdown cancels.");
            mockUploadManager.Verify(m => m.EnqueueJobAsync(It.IsAny<UploadJob>()), Times.Once);

            sut.Dispose();
        }

        [Test]
        public async Task FlushPendingAsync_NoInFlightWork_CompletesImmediately()
        {
            var sut = new ImageSaveListener(
                mockMediator.Object,
                mockUploadManager.Object,
                mockPathResolver.Object,
                () => true);
            sut.StartListening();
            sut.StopListening();

            // No captures fired -- flush must return promptly without hanging.
            await sut.FlushPendingAsync(TimeSpan.FromSeconds(5)).WaitAsync(TimeSpan.FromSeconds(5));

            sut.Dispose();
        }

        [Test]
        public async Task UploadManagerStopAsync_IsAwaitable_AndCompletesWithoutBlocking()
        {
            // REL-13: the stop chain is awaited end-to-end. The manager's StopAsync returns a Task
            // that can be awaited (no .Wait() needed). Starting and stopping must not deadlock and
            // must leave the manager not-running.
            var repo = new Mock<IUploadQueueRepository>();
            repo.Setup(r => r.GetJobCountAsync(It.IsAny<UploadStatus>())).ReturnsAsync(0);
            repo.Setup(r => r.PeekAsync()).ReturnsAsync((UploadJob)null);
            repo.Setup(r => r.GetTotalPendingSizeAsync()).ReturnsAsync(0L);

            var apiClient = new Mock<ICloudApiClient>();
            var auth = new Mock<IAuthManager>();
            auth.Setup(a => a.GetApiKey()).Returns((string)null);

            var manager = new Astrovault.Core.UploadManager(repo.Object, apiClient.Object, auth.Object);

            using var cts = new CancellationTokenSource();
            await manager.StartAsync(cts.Token);
            Assert.That(manager.IsRunning, Is.True, "Manager should be running after StartAsync");

            // Awaiting StopAsync (not .Wait()) must complete and stop the loop.
            await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(manager.IsRunning, Is.False, "Manager must not be running after an awaited StopAsync");
        }

        // ====================================================================
        // REL-15 / REL-16: endpoint validation and bad-endpoint-cannot-brick-load
        // ====================================================================

        [TestCase("http://localhost:5000")]
        [TestCase("https://api.astrovault.io")]
        [TestCase("http://staging.example.com:8087/")]
        public void IsValidEndpoint_AcceptsAbsoluteHttpUrls(string endpoint)
        {
            Assert.That(Astrovault.AstrovaultPlugin.IsValidEndpoint(endpoint), Is.True,
                $"'{endpoint}' is an absolute http/https URL and must be accepted.");
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase(null)]
        [TestCase("not a url")]
        [TestCase("ftp://example.com")]
        [TestCase("localhost:5000")]      // missing scheme -> relative
        [TestCase("/relative/path")]
        public void IsValidEndpoint_RejectsMalformedOrNonHttp(string? endpoint)
        {
            // REL-15: an invalid value must be rejected by the setter BEFORE it is persisted, so it can
            // never brick the next plugin load.
            Assert.That(Astrovault.AstrovaultPlugin.IsValidEndpoint(endpoint), Is.False,
                $"'{endpoint}' is not an absolute http/https URL and must be rejected.");
        }

        [Test]
        public void MalformedStoredEndpoint_WouldThrowDuringConstruction_ButValidFallbackDoesNot()
        {
            // REL-16 rationale: a malformed endpoint thrown into AuthManager's ctor (which builds a
            // Uri) WOULD throw during construction -- this is exactly what would brick MEF plugin load.
            // The plugin guards this by validating the stored endpoint and falling back to a valid one,
            // so the fallback constructs cleanly. This test pins both halves of that contract.
            var dataFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AstrovaultTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.That(Astrovault.AstrovaultPlugin.IsValidEndpoint("not a url"), Is.False);

                // A malformed endpoint reaching the ctor would throw (the failure the guard prevents).
                Assert.That(() => new Astrovault.Core.AuthManager("not a url", dataFolder),
                    Throws.InstanceOf<UriFormatException>(),
                    "A malformed endpoint reaching AuthManager construction throws -- the guard must prevent this from ever happening on load.");

                // The valid fallback the guard substitutes constructs without throwing.
                Assert.That(() =>
                {
                    using var auth = new Astrovault.Core.AuthManager("https://vault.astrospherehub.com/", dataFolder);
                }, Throws.Nothing,
                    "The valid fallback endpoint must construct cleanly so plugin load is never bricked.");
            }
            finally
            {
                try { System.IO.Directory.Delete(dataFolder, true); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
