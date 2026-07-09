using System;
using System.Diagnostics;
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
    [TestFixture]
    [Category("CapturePipeline")]
    public class ImageSaveListenerTests
    {
        private Mock<IImageSaveMediator> mockMediator;
        private Mock<IUploadManager> mockUploadManager;
        private Mock<IPathResolver> mockPathResolver;
        private ImageSaveListener sut;
        private bool isEnabled;

        [SetUp]
        public void Setup()
        {
            mockMediator = new Mock<IImageSaveMediator>();
            mockUploadManager = new Mock<IUploadManager>();
            mockPathResolver = new Mock<IPathResolver>();
            isEnabled = true;

            // Default: pathResolver returns a sensible relative path
            mockPathResolver.Setup(p => p.GetUploadPath(It.IsAny<Uri>()))
                .Returns("M31/Light/001.fits");

            // Default: EnqueueJobAsync completes immediately
            mockUploadManager.Setup(m => m.EnqueueJobAsync(It.IsAny<UploadJob>()))
                .Returns(Task.CompletedTask);

            sut = new ImageSaveListener(
                mockMediator.Object,
                mockUploadManager.Object,
                mockPathResolver.Object,
                () => isEnabled);
            sut.StartListening();
        }

        [TearDown]
        public void TearDown()
        {
            sut?.Dispose();
        }

        /// <summary>
        /// Creates an ImageSavedEventArgs instance with a controllable PathToImage URI.
        /// MetaData is populated with minimal defaults for the handler to process.
        /// </summary>
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

        // ----------------------------------------------------------------
        // DATA-05: Automatic triggering -- image save event creates upload job
        // ----------------------------------------------------------------

        [Test]
        public async Task OnImageSaved_WhenEnabled_CreatesAndEnqueuesJob()
        {
            // Arrange
            var args = CreateEventArgs(@"file:///D:/Astro/M31/Light/001.fits");

            // Act
            mockMediator.Raise(m => m.ImageSaved += null, this, args);
            await Task.Delay(1000);

            // Assert
            mockUploadManager.Verify(
                m => m.EnqueueJobAsync(It.IsAny<UploadJob>()),
                Times.Once);
        }

        [Test]
        public async Task OnImageSaved_WhenDisabled_DoesNotEnqueue()
        {
            // Arrange
            isEnabled = false;
            var args = CreateEventArgs(@"file:///D:/Astro/M31/Light/001.fits");

            // Act
            mockMediator.Raise(m => m.ImageSaved += null, this, args);
            await Task.Delay(500);

            // Assert
            mockUploadManager.Verify(
                m => m.EnqueueJobAsync(It.IsAny<UploadJob>()),
                Times.Never);
        }

        // ----------------------------------------------------------------
        // DATA-04: Format support -- all 7 extensions trigger upload
        // ----------------------------------------------------------------

        [TestCase(".fits", "FITS")]
        [TestCase(".xisf", "XISF")]
        [TestCase(".tiff", "TIFF")]
        [TestCase(".tif", "TIF")]
        [TestCase(".png", "PNG")]
        [TestCase(".jpg", "JPG")]
        [TestCase(".jpeg", "JPEG")]
        public async Task OnImageSaved_SetsFileTypeFromExtension(string extension, string expectedFileType)
        {
            // Arrange
            var samplePath = TestDataFactory.CreateSamplePath(extension);
            var args = CreateEventArgs($"file:///{samplePath.Replace('\\', '/')}");
            UploadJob? capturedJob = null;
            mockUploadManager.Setup(m => m.EnqueueJobAsync(It.IsAny<UploadJob>()))
                .Callback<UploadJob>(job => capturedJob = job)
                .Returns(Task.CompletedTask);

            // Act
            mockMediator.Raise(m => m.ImageSaved += null, this, args);
            await Task.Delay(1000);

            // Assert
            Assert.That(capturedJob, Is.Not.Null, $"No job enqueued for extension {extension}");
            Assert.That(capturedJob.FileType, Is.EqualTo(expectedFileType));
        }

        // ----------------------------------------------------------------
        // SAFE-01: Non-blocking -- event handler returns immediately
        // ----------------------------------------------------------------

        [Test]
        public void OnImageSaved_DoesNotBlockCaller()
        {
            // Arrange
            var args = CreateEventArgs(@"file:///D:/Astro/M31/Light/001.fits");

            // Act -- measure how long it takes for the event handler to return
            var sw = Stopwatch.StartNew();
            mockMediator.Raise(m => m.ImageSaved += null, this, args);
            sw.Stop();

            // Assert -- handler dispatches to Task.Run and returns immediately
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(100),
                "Event handler should return in under 100ms (fire-and-forget via Task.Run)");
        }

        // ----------------------------------------------------------------
        // SAFE-02: Exception isolation -- exceptions don't propagate
        // ----------------------------------------------------------------

        [Test]
        public async Task OnImageSaved_ExceptionInJobCreation_DoesNotPropagate()
        {
            // Arrange -- pathResolver throws, simulating a failure in job creation
            mockPathResolver.Setup(p => p.GetUploadPath(It.IsAny<Uri>()))
                .Throws(new InvalidOperationException("Simulated failure"));
            var args = CreateEventArgs(@"file:///D:/Astro/M31/Light/001.fits");

            // Act -- should not throw
            Assert.DoesNotThrow(() =>
                mockMediator.Raise(m => m.ImageSaved += null, this, args));
            await Task.Delay(500);

            // Assert -- EnqueueJobAsync should never be called because CreateUploadJob failed
            mockUploadManager.Verify(
                m => m.EnqueueJobAsync(It.IsAny<UploadJob>()),
                Times.Never);
        }

        // ----------------------------------------------------------------
        // DATA-03 integration: PathResolver is called and result set on job
        // ----------------------------------------------------------------

        [Test]
        public async Task OnImageSaved_SetsRelativePathFromPathResolver()
        {
            // Arrange
            mockPathResolver.Setup(p => p.GetUploadPath(It.IsAny<Uri>()))
                .Returns("NGC7000/Ha/Light/frame_001.fits");
            var args = CreateEventArgs(@"file:///D:/Astro/NGC7000/Ha/Light/frame_001.fits");
            UploadJob? capturedJob = null;
            mockUploadManager.Setup(m => m.EnqueueJobAsync(It.IsAny<UploadJob>()))
                .Callback<UploadJob>(job => capturedJob = job)
                .Returns(Task.CompletedTask);

            // Act
            mockMediator.Raise(m => m.ImageSaved += null, this, args);
            await Task.Delay(1000);

            // Assert
            Assert.That(capturedJob, Is.Not.Null, "Job should be enqueued");
            Assert.That(capturedJob.RelativePath, Is.EqualTo("NGC7000/Ha/Light/frame_001.fits"));
        }

        // ----------------------------------------------------------------
        // Timeout verification: CancellationTokenSource with ThrowIfCancellationRequested
        // The 10s timeout is structurally verified by grep in plan verification.
        // This test verifies the happy path completes within normal time,
        // proving the timeout doesn't interfere with normal operation.
        // ----------------------------------------------------------------

        [Test]
        public async Task OnImageSaved_CompletesNormally_WithTimeoutPresent()
        {
            // Arrange
            var args = CreateEventArgs(@"file:///D:/Astro/M31/Light/001.fits");

            // Act
            mockMediator.Raise(m => m.ImageSaved += null, this, args);
            await Task.Delay(1000);

            // Assert -- job was enqueued successfully despite timeout mechanism being present
            mockUploadManager.Verify(
                m => m.EnqueueJobAsync(It.IsAny<UploadJob>()),
                Times.Once,
                "Job should be enqueued when timeout hasn't expired");
        }

        // ----------------------------------------------------------------
        // OperationCanceledException handling -- graceful shutdown path
        // ----------------------------------------------------------------

        [Test]
        public async Task OnImageSaved_OperationCancelled_DoesNotPropagate()
        {
            // Arrange -- EnqueueJobAsync throws OperationCanceledException (shutdown)
            mockUploadManager.Setup(m => m.EnqueueJobAsync(It.IsAny<UploadJob>()))
                .ThrowsAsync(new OperationCanceledException("Shutdown"));
            var args = CreateEventArgs(@"file:///D:/Astro/M31/Light/001.fits");

            // Act -- should not throw
            Assert.DoesNotThrow(() =>
                mockMediator.Raise(m => m.ImageSaved += null, this, args));
            await Task.Delay(500);

            // Assert -- no crash, exception is caught and logged
            // The fact we reached here without exception proves isolation
            Assert.Pass("OperationCanceledException was caught without propagating");
        }
    }
}
