using Astrovault.Core;
using Moq;
using NINA.Profile.Interfaces;

namespace CloudUploadPlugin.Tests.Core
{
    [TestFixture]
    [Category("CapturePipeline")]
    public class PathResolverTests
    {
        private Mock<IProfileService> mockProfileService;
        private Mock<IProfile> mockProfile;
        private Mock<IImageFileSettings> mockImageFileSettings;
        private PathResolver sut;

        [SetUp]
        public void Setup()
        {
            // Arrange: mock the IProfileService -> IProfile -> IImageFileSettings chain
            mockProfileService = new Mock<IProfileService>();
            mockProfile = new Mock<IProfile>();
            mockImageFileSettings = new Mock<IImageFileSettings>();

            mockImageFileSettings.Setup(s => s.FilePath).Returns(@"D:\Astro");
            mockProfile.Setup(p => p.ImageFileSettings).Returns(mockImageFileSettings.Object);
            mockProfileService.Setup(ps => ps.ActiveProfile).Returns(mockProfile.Object);

            sut = new PathResolver(mockProfileService.Object);
        }

        [Test]
        public void GetRelativePath_WithValidBasePath_ReturnsRelative()
        {
            // Arrange
            var absolutePath = @"D:\Astro\M31\Light\001.fits";
            var basePath = @"D:\Astro";

            // Act
            var result = sut.GetRelativePath(absolutePath, basePath);

            // Assert
            Assert.That(result, Is.EqualTo("M31/Light/001.fits"));
        }

        [Test]
        public void GetRelativePath_PreservesSubdirectories()
        {
            // Arrange -- deeply nested path
            var absolutePath = @"D:\Astro\2026\March\M31\Ha\Light\session2\001.fits";
            var basePath = @"D:\Astro";

            // Act
            var result = sut.GetRelativePath(absolutePath, basePath);

            // Assert
            Assert.That(result, Is.EqualTo("2026/March/M31/Ha/Light/session2/001.fits"));
        }

        [Test]
        public void GetRelativePath_NormalizesBackslashes()
        {
            // Arrange -- Windows-style path with backslashes
            var absolutePath = @"D:\Astro\M31\Light\001.fits";
            var basePath = @"D:\Astro";

            // Act
            var result = sut.GetRelativePath(absolutePath, basePath);

            // Assert -- should use forward slashes for cloud compatibility
            Assert.That(result, Does.Not.Contain(@"\"));
            Assert.That(result, Does.Contain("/"));
        }

        [Test]
        public void GetRelativePath_WithNullAbsolutePath_ReturnsEmpty()
        {
            // Arrange / Act
            var result = sut.GetRelativePath(null, @"D:\Astro");

            // Assert
            Assert.That(result, Is.EqualTo(string.Empty));
        }

        [Test]
        public void GetRelativePath_WithEmptyAbsolutePath_ReturnsEmpty()
        {
            // Arrange / Act
            var result = sut.GetRelativePath("", @"D:\Astro");

            // Assert
            Assert.That(result, Is.EqualTo(string.Empty));
        }

        [Test]
        public void GetRelativePath_OutsideBasePath_ReturnsFilenameOnly()
        {
            // Arrange -- file saved to secondary drive, not under configured base path
            var absolutePath = @"E:\OtherDrive\session\image.fits";
            var basePath = @"D:\Astro";

            // Act
            var result = sut.GetRelativePath(absolutePath, basePath);

            // Assert -- falls back to filename only when path is outside base
            Assert.That(result, Is.EqualTo("image.fits"));
        }

        [Test]
        public void GetUploadPath_UsesProfileBasePath()
        {
            // Arrange -- profile base path is D:\Astro (set in Setup)
            var imageUri = new Uri("file:///D:/Astro/M42/Light/001.xisf");

            // Act
            var result = sut.GetUploadPath(imageUri);

            // Assert
            Assert.That(result, Is.EqualTo("M42/Light/001.xisf"));
        }

        [Test]
        public void GetUploadPath_WithNullUri_ReturnsEmpty()
        {
            // Act
            var result = sut.GetUploadPath(null);

            // Assert
            Assert.That(result, Is.EqualTo(string.Empty));
        }

        [Test]
        public void GetRelativePath_WithNullBasePath_ReturnsNormalizedFullPath()
        {
            // Arrange -- null base path should return normalized full path
            var absolutePath = @"D:\Astro\M31\001.fits";

            // Act
            var result = sut.GetRelativePath(absolutePath, null);

            // Assert -- returns full path with forward slashes
            Assert.That(result, Does.Contain("Astro/M31/001.fits"));
        }
    }
}
