using System;
using System.Linq;
using Astrovault.Integration;
using Astrovault.Models;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

namespace CloudUploadPlugin.Tests.Integration
{
    [TestFixture]
    [Category("FilePreparation")]
    public class MetadataCompletenessTests
    {
        /// <summary>
        /// Creates a fully populated ImageSavedEventArgs with realistic astrophotography data.
        /// Populates all metadata field groups required by DATA-01.
        /// </summary>
        private static ImageSavedEventArgs CreateFullyPopulatedEventArgs()
        {
            var metaData = new ImageMetaData();

            // Camera info
            metaData.Camera.Name = "ZWO ASI294MC Pro";
            metaData.Camera.Gain = 120;
            metaData.Camera.Offset = 30;
            metaData.Camera.Temperature = -10.0;
            metaData.Camera.SetPoint = -10.0;
            metaData.Camera.PixelSize = 4.63;
            metaData.Camera.ReadoutModeName = "Low Noise";

            // Filter wheel info
            metaData.FilterWheel.Filter = "Ha";
            metaData.FilterWheel.Name = "ZWO EFW";

            // Target info
            metaData.Target.Name = "M42";
            metaData.Target.Coordinates = new Coordinates(
                Angle.ByHours(5.588), Angle.ByDegree(-5.39), Epoch.J2000);

            // Image info
            metaData.Image.ExposureTime = 300.0;
            metaData.Image.ExposureStart = DateTime.UtcNow;
            metaData.Image.ImageType = "LIGHT";
            metaData.Image.ExposureNumber = 42;
            metaData.Image.ExposureMidPoint = new DateTime(2026, 3, 22, 4, 24, 11, DateTimeKind.Utc);

            // Guiding RMS
            var rms = new RMS();
            rms.AddDataPoint(0.5, 0.3);
            rms.AddDataPoint(1.2, 0.8);
            metaData.Image.RecordedRMS = rms;

            // Weather data
            metaData.WeatherData.Temperature = 15.0;
            metaData.WeatherData.Humidity = 65.0;
            metaData.WeatherData.DewPoint = 8.0;
            metaData.WeatherData.Pressure = 1013.25;
            metaData.WeatherData.WindGust = 12.5;
            metaData.WeatherData.SkyBrightness = 21.5;
            metaData.WeatherData.StarFWHM = 2.3;

            // Telescope info -- set to real values so ?? 0 doesn't matter and Aperture can be computed
            metaData.Telescope.FocalLength = 1000.0;
            metaData.Telescope.FocalRatio = 5.0;

            // Rotator info
            metaData.Rotator.StepSize = 0.5;

            // Observer info -- Name/Observatory/Site are now exposed by the stable
            // NINA.Image 3.2.0.9001 ObserverParameter (MISS-01). SDK member is `Site`;
            // the DTO/wire field is `SiteName`.
            metaData.Observer.Name = "Ada Lovelace";
            metaData.Observer.Observatory = "Backyard Remote Obs";
            metaData.Observer.Site = "Atacama Dark Site";

            // Sequence info
            metaData.Sequence.Title = "M42 LRGB Session";

            // Mocked IImageStatistics
            var mockStats = new Mock<IImageStatistics>();
            mockStats.Setup(s => s.Mean).Returns(5000.0);
            mockStats.Setup(s => s.Median).Returns(5001.0);
            mockStats.Setup(s => s.StDev).Returns(100.0);
            mockStats.Setup(s => s.MedianAbsoluteDeviation).Returns(67.0);
            mockStats.Setup(s => s.Min).Returns(4497);
            mockStats.Setup(s => s.Max).Returns(5533);
            mockStats.Setup(s => s.BitDepth).Returns(16);

            // Mocked IStarDetectionAnalysis
            var mockStarInfo = new Mock<IStarDetectionAnalysis>();
            mockStarInfo.Setup(s => s.HFR).Returns(3.2);
            mockStarInfo.Setup(s => s.DetectedStars).Returns(150);
            mockStarInfo.Setup(s => s.HFRStDev).Returns(0.45);
            // FWHM and Eccentricity exist in NINA source (ref_repos) but NOT in
            // installed NuGet NINA.Image 3.0.0.2017-beta IStarDetectionAnalysis.

            return new ImageSavedEventArgs
            {
                PathToImage = new Uri("file:///D:/Astro/M42/Light/001.fits"),
                MetaData = metaData,
                // MISS-02: provide a real rendered bitmap so FITS NAXIS1/NAXIS2 reflect actual
                // dimensions (4144x2822, a representative ASI294 frame) rather than 1x1.
                Image = CreateBitmap(4144, 2822),
                Statistics = mockStats.Object,
                StarDetectionAnalysis = mockStarInfo.Object
            };
        }

        /// <summary>
        /// Creates a minimal in-memory BitmapSource with the given dimensions for FITS-dimension
        /// tests. NINA hands a System.Windows.Media.Imaging.BitmapSource to ImageSavedEventArgs.Image.
        /// </summary>
        private static System.Windows.Media.Imaging.BitmapSource CreateBitmap(int width, int height)
        {
            // 1 byte-per-pixel Gray8 keeps the allocation tiny; only Pixel{Width,Height} are read.
            int stride = width;
            var pixels = new byte[stride * height];
            return System.Windows.Media.Imaging.BitmapSource.Create(
                width, height, 96, 96,
                System.Windows.Media.PixelFormats.Gray8, null, pixels, stride);
        }

        // ----------------------------------------------------------------
        // DATA-01: Camera info populated
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_PopulatesCamera()
        {
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Camera.Name, Is.Not.Empty);
            Assert.That(dto.Camera.Name, Is.EqualTo("ZWO ASI294MC Pro"));
            Assert.That(dto.Camera.Gain, Is.EqualTo(120));
            Assert.That(dto.Camera.Temperature, Is.EqualTo(-10.0));
        }

        // ----------------------------------------------------------------
        // DATA-01: Filter info populated
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_PopulatesFilter()
        {
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Image.Filter, Is.EqualTo("Ha"));
            Assert.That(dto.FilterWheel.FilterName, Is.EqualTo("Ha"));
        }

        // ----------------------------------------------------------------
        // DATA-01: Target info populated
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_PopulatesTarget()
        {
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Target.Name, Is.EqualTo("M42"));
        }

        // ----------------------------------------------------------------
        // DATA-01: Guiding stats populated
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_PopulatesGuidingStats()
        {
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Guiding.RmsRA, Is.GreaterThan(0));
            Assert.That(dto.Guiding.RmsDec, Is.GreaterThan(0));
            Assert.That(dto.Guiding.RmsTotal, Is.GreaterThan(0));
            Assert.That(dto.Guiding.PeakRA, Is.GreaterThan(0));
            Assert.That(dto.Guiding.PeakDec, Is.GreaterThan(0));
        }

        // ----------------------------------------------------------------
        // DATA-01: Weather data populated
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_PopulatesWeatherData()
        {
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Weather.Temperature, Is.EqualTo(15.0));
            Assert.That(dto.Weather.Humidity, Is.GreaterThan(0));
            Assert.That(dto.Weather.DewPoint, Is.EqualTo(8.0));
            Assert.That(dto.Weather.Pressure, Is.GreaterThan(0));
        }

        // ----------------------------------------------------------------
        // DATA-01: FITS headers populated
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_PopulatesFitsHeaders()
        {
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.FitsHeaders, Is.Not.Null);
            Assert.That(dto.FitsHeaders.Count, Is.GreaterThan(0),
                "FITSHeader.PopulateFromMetaData should generate standard FITS keywords");
        }

        // ----------------------------------------------------------------
        // DATA-01: Comprehensive proof test -- all six field groups
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_AllRequiredFieldGroupsPresent()
        {
            // This is the "proof test" for DATA-01: all six required field groups
            // must be populated when source metadata is present.
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            // 1. Camera info
            Assert.That(dto.Camera.Name, Is.Not.Empty,
                "DATA-01: Camera name must be populated");

            // 2. Filter info
            Assert.That(dto.Image.Filter, Is.Not.Empty,
                "DATA-01: Filter must be populated");

            // 3. Target info
            Assert.That(dto.Target.Name, Is.Not.Empty,
                "DATA-01: Target name must be populated");

            // 4. Guiding stats
            Assert.That(dto.Guiding.RmsTotal, Is.GreaterThan(0),
                "DATA-01: Guiding RMS total must be populated");

            // 5. Weather data
            Assert.That(dto.Weather.Temperature, Is.Not.EqualTo(0.0),
                "DATA-01: Weather temperature must be populated");

            // 6. FITS headers
            Assert.That(dto.FitsHeaders.Count, Is.GreaterThan(0),
                "DATA-01: FITS headers must be populated");

            // 7. Sequence info (new sub-object per D-13)
            Assert.That(dto.Sequence.Title, Is.Not.Empty,
                "D-13: Sequence title must be populated");
        }

        // ----------------------------------------------------------------
        // D-01, D-02: Image.ExposureNumber and ExposureMidPoint
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_PopulatesExposureNumberAndMidPoint()
        {
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Image.ExposureNumber, Is.EqualTo(42));
            Assert.That(dto.Image.ExposureMidPoint, Is.Not.Null);
            Assert.That(dto.Image.ExposureMidPoint.Value.Year, Is.EqualTo(2026));
        }

        [Test]
        public void ExtractFullMetadata_ExposureMidPoint_NullWhenMinValue()
        {
            var args = CreateFullyPopulatedEventArgs();
            args.MetaData.Image.ExposureMidPoint = DateTime.MinValue;

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Image.ExposureMidPoint, Is.Null);
        }

        // ----------------------------------------------------------------
        // D-03: Camera.ReadoutModeName
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_PopulatesReadoutModeName()
        {
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Camera.ReadoutModeName, Is.EqualTo("Low Noise"));
        }

        // ----------------------------------------------------------------
        // D-04, D-05, D-06: Weather new fields
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_PopulatesNewWeatherFields()
        {
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Weather.WindGust, Is.EqualTo(12.5));
            Assert.That(dto.Weather.SkyBrightness, Is.EqualTo(21.5));
            Assert.That(dto.Weather.StarFWHM, Is.EqualTo(2.3));
        }

        // ----------------------------------------------------------------
        // D-07: Rotator.StepSize
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_PopulatesRotatorStepSize()
        {
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Rotator.StepSize, Is.EqualTo(0.5));
        }

        // ----------------------------------------------------------------
        // D-08, D-09, D-10, D-11: Statistics new fields
        // (HARD REQUIREMENT -- no fallback per review concern #3)
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_PopulatesStatisticsNewFields()
        {
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Statistics.BitDepth, Is.EqualTo(16));
            Assert.That(dto.Statistics.HFRStDev, Is.EqualTo(0.45));
            // FWHM and Eccentricity are NOT exposed by the published stable NINA.Plugin 3.2.0.9001
            // SDK surface (verified via reflection: IStarDetectionAnalysis and DetectedStar expose
            // neither). They therefore remain at their BACKEND-API-SPEC SS6 documented 0.0 default
            // ("0.0 if star detection not run"). This is a Rule-4 finding flagged in the SUMMARY:
            // the stable NuGet does not publish these members, contrary to the plan's premise.
            Assert.That(dto.Statistics.FWHM, Is.EqualTo(0.0),
                "FWHM not available on published stable 3.2.0.9001 SDK; stays at spec 0.0 default");
            Assert.That(dto.Statistics.Eccentricity, Is.EqualTo(0.0),
                "Eccentricity not available on published stable 3.2.0.9001 SDK; stays at spec 0.0 default");
        }

        // ----------------------------------------------------------------
        // D-12: Guiding.DataPoints
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_PopulatesGuidingDataPoints()
        {
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Guiding.DataPoints, Is.GreaterThan(0));
        }

        // ----------------------------------------------------------------
        // D-13: Sequence.Title
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_PopulatesSequenceTitle()
        {
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Sequence, Is.Not.Null);
            Assert.That(dto.Sequence.Title, Is.EqualTo("M42 LRGB Session"));
        }

        // ----------------------------------------------------------------
        // D-15, D-16, D-17: Observer fields
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_PopulatesObserverFields()
        {
            var args = CreateFullyPopulatedEventArgs();

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            // MISS-01: Observer.Name/Observatory/SiteName are now populated from the stable
            // NINA.Image 3.2.0.9001 ObserverParameter (Name/Observatory/Site). The SDK member is
            // `Site`; the DTO/wire field is `SiteName` (RESEARCH A5).
            Assert.That(dto.Observer.SiteName, Is.EqualTo("Atacama Dark Site"),
                "MISS-01: SiteName must be sourced from ObserverParameter.Site");
            Assert.That(dto.Observer.Name, Is.EqualTo("Ada Lovelace"),
                "MISS-01: Observer.Name must be sourced from ObserverParameter.Name");
            Assert.That(dto.Observer.Observatory, Is.EqualTo("Backyard Remote Obs"),
                "MISS-01: Observatory must be sourced from ObserverParameter.Observatory");
            Assert.That(dto.Observer, Is.Not.Null, "Observer sub-object must exist");
        }

        // ----------------------------------------------------------------
        // D-18: Telescope.Aperture computed
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_ComputesTelescopeAperture()
        {
            var args = CreateFullyPopulatedEventArgs();
            // FocalLength=1000, FocalRatio=5 => Aperture=200

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Telescope.Aperture, Is.EqualTo(200.0).Within(0.01));
        }

        [Test]
        public void ExtractFullMetadata_TelescopeAperture_ZeroWhenFocalRatioZero()
        {
            var args = CreateFullyPopulatedEventArgs();
            args.MetaData.Telescope.FocalRatio = 0;

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Telescope.Aperture, Is.EqualTo(0.0));
        }

        // ----------------------------------------------------------------
        // D-19: StarCount removed (reflection test)
        // ----------------------------------------------------------------

        [Test]
        public void ImageStatistics_DoesNotHaveStarCountProperty()
        {
            var statsType = typeof(Astrovault.Models.ImageStatistics);
            var starCountProp = statsType.GetProperty("StarCount");

            Assert.That(starCountProp, Is.Null, "StarCount should be removed per D-19");
        }

        // ----------------------------------------------------------------
        // SERIALIZATION CONTRACT TEST (addresses review concern #2)
        // Verifies the actual JSON the backend will consume.
        // ----------------------------------------------------------------

        [Test]
        public void SerializedJson_ContainsNewKeys_ExcludesStarCount()
        {
            // Arrange: extract DTO from fully populated args
            var args = CreateFullyPopulatedEventArgs();
            var dto = ImageSaveListener.ExtractFullMetadata(args);

            // Act: serialize to JSON (same as production SerializeFullMetadata path)
            var json = JsonConvert.SerializeObject(dto);
            var jObj = JObject.Parse(json);

            // Assert: new keys exist in JSON
            Assert.That(jObj.SelectToken("Image.ExposureNumber"), Is.Not.Null, "ExposureNumber key must exist in JSON");
            Assert.That(jObj.SelectToken("Image.ExposureMidPoint"), Is.Not.Null, "ExposureMidPoint key must exist in JSON");
            Assert.That(jObj.SelectToken("Camera.ReadoutModeName"), Is.Not.Null, "ReadoutModeName key must exist in JSON");
            Assert.That(jObj.SelectToken("Weather.WindGust"), Is.Not.Null, "WindGust key must exist in JSON");
            Assert.That(jObj.SelectToken("Weather.SkyBrightness"), Is.Not.Null, "SkyBrightness key must exist in JSON");
            Assert.That(jObj.SelectToken("Weather.StarFWHM"), Is.Not.Null, "StarFWHM key must exist in JSON");
            Assert.That(jObj.SelectToken("Rotator.StepSize"), Is.Not.Null, "Rotator.StepSize key must exist in JSON");
            Assert.That(jObj.SelectToken("Statistics.BitDepth"), Is.Not.Null, "BitDepth key must exist in JSON");
            Assert.That(jObj.SelectToken("Statistics.HFRStDev"), Is.Not.Null, "HFRStDev key must exist in JSON");
            Assert.That(jObj.SelectToken("Statistics.FWHM"), Is.Not.Null, "FWHM key must exist in JSON");
            Assert.That(jObj.SelectToken("Statistics.Eccentricity"), Is.Not.Null, "Eccentricity key must exist in JSON");
            Assert.That(jObj.SelectToken("Guiding.DataPoints"), Is.Not.Null, "DataPoints key must exist in JSON");
            Assert.That(jObj.SelectToken("Observer.Name"), Is.Not.Null, "Observer.Name key must exist in JSON");
            Assert.That(jObj.SelectToken("Observer.Observatory"), Is.Not.Null, "Observer.Observatory key must exist in JSON");
            Assert.That(jObj.SelectToken("Sequence"), Is.Not.Null, "Sequence sub-object must exist in JSON");
            Assert.That(jObj.SelectToken("Sequence.Title"), Is.Not.Null, "Sequence.Title key must exist in JSON");

            // Assert: StarCount MUST NOT appear in JSON (per D-19)
            Assert.That(jObj.SelectToken("Statistics.StarCount"), Is.Null, "StarCount must not exist in JSON per D-19");

            // Assert: specific values are serialized correctly
            Assert.That(jObj.SelectToken("Image.ExposureNumber").Value<int>(), Is.EqualTo(42));
            Assert.That(jObj.SelectToken("Sequence.Title").Value<string>(), Is.EqualTo("M42 LRGB Session"));
            Assert.That(jObj.SelectToken("Statistics.HFRStDev").Value<double>(), Is.EqualTo(0.45));
        }

        [Test]
        public void SerializedJson_NaNValues_SerializeAsNaNString()
        {
            // Arrange: create DTO with NaN values to verify serialization behavior.
            // This tests the actual behavior that the backend must handle.
            var dto = new ImageMetadataDto();
            // Weather fields default to 0 in C# DTO, but set them to NaN to simulate
            // what happens when NINA's NaN flows through extraction.
            dto.Weather.WindGust = double.NaN;
            dto.Weather.SkyBrightness = double.NaN;
            dto.Rotator.StepSize = double.NaN;

            // Act: serialize using Newtonsoft.Json (same as production path)
            var json = JsonConvert.SerializeObject(dto);

            // Assert: NaN is serialized as the string "NaN", not as 0.0 or null
            Assert.That(json, Does.Contain("\"NaN\""),
                "Newtonsoft.Json must serialize double.NaN as the string \"NaN\"");

            // Verify specific field serialization via JObject
            var jObj = JObject.Parse(json);
            Assert.That(jObj.SelectToken("Weather.WindGust").Type, Is.EqualTo(JTokenType.String),
                "NaN WindGust must serialize as string type, not number");
            Assert.That(jObj.SelectToken("Weather.WindGust").Value<string>(), Is.EqualTo("NaN"),
                "NaN WindGust must serialize as \"NaN\" string");
        }

        // ----------------------------------------------------------------
        // MISS-02: FITS NAXIS1/NAXIS2 reflect real image dimensions, not 1x1
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_FitsNaxis_ReflectsRealImageDimensions()
        {
            var args = CreateFullyPopulatedEventArgs(); // Image is 4144x2822

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.FitsHeaders.ContainsKey("NAXIS1"), Is.True, "NAXIS1 card must be present");
            Assert.That(dto.FitsHeaders.ContainsKey("NAXIS2"), Is.True, "NAXIS2 card must be present");
            // Compare on string value to match the FITS-card wire representation (all values are strings).
            Assert.That(dto.FitsHeaders["NAXIS1"].Value.ToString(), Is.EqualTo("4144"),
                "MISS-02: NAXIS1 must equal e.Image.PixelWidth, not the hardcoded 1");
            Assert.That(dto.FitsHeaders["NAXIS2"].Value.ToString(), Is.EqualTo("2822"),
                "MISS-02: NAXIS2 must equal e.Image.PixelHeight, not the hardcoded 1");
        }

        [Test]
        public void ExtractFullMetadata_FitsNaxis_FallsBackToOne_WhenImageNull()
        {
            var args = CreateFullyPopulatedEventArgs();
            args.Image = null; // no rendered bitmap available

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.FitsHeaders["NAXIS1"].Value.ToString(), Is.EqualTo("1"),
                "NAXIS1 falls back to 1 when e.Image is null");
            Assert.That(dto.FitsHeaders["NAXIS2"].Value.ToString(), Is.EqualTo("1"),
                "NAXIS2 falls back to 1 when e.Image is null");
        }

        // ----------------------------------------------------------------
        // MISS-03: hardware binning fallback when Image.Binning is empty
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_Binning_UsesImageBinning_WhenPresent()
        {
            var args = CreateFullyPopulatedEventArgs();
            args.MetaData.Image.Binning = "2x2";
            args.MetaData.Camera.BinX = 3;
            args.MetaData.Camera.BinY = 3;

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Image.Binning, Is.EqualTo("2x2"),
                "Image.Binning takes precedence over hardware binning when present");
        }

        [Test]
        public void ExtractFullMetadata_Binning_FallsBackToHardware_WhenImageBinningEmpty()
        {
            var args = CreateFullyPopulatedEventArgs();
            args.MetaData.Image.Binning = null; // non-sequencer capture
            args.MetaData.Camera.BinX = 2;
            args.MetaData.Camera.BinY = 2;

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Image.Binning, Is.EqualTo("2x2"),
                "MISS-03: binning must fall back to Camera.BinX/BinY when Image.Binning is empty");
        }

        [Test]
        public void ExtractFullMetadata_Binning_FallsBackToOneByOne_WhenNoSource()
        {
            var args = CreateFullyPopulatedEventArgs();
            args.MetaData.Image.Binning = null;
            args.MetaData.Camera.BinX = 0;
            args.MetaData.Camera.BinY = 0;

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Image.Binning, Is.EqualTo("1x1"),
                "binning final fallback is 1x1 when neither Image nor Camera provides it");
        }

        // ----------------------------------------------------------------
        // D-04: ExposureStart DateTime.MinValue guarded to null
        // ----------------------------------------------------------------

        [Test]
        public void ExtractFullMetadata_ExposureStart_NullWhenMinValue()
        {
            var args = CreateFullyPopulatedEventArgs();
            args.MetaData.Image.ExposureStart = DateTime.MinValue;

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Image.ExposureStart, Is.Null,
                "D-04: ExposureStart of DateTime.MinValue must serialize as null");
        }

        [Test]
        public void ExtractFullMetadata_ExposureStart_PreservesRealValue()
        {
            var args = CreateFullyPopulatedEventArgs();
            var real = new DateTime(2026, 3, 22, 4, 23, 41, DateTimeKind.Utc);
            args.MetaData.Image.ExposureStart = real;

            var dto = ImageSaveListener.ExtractFullMetadata(args);

            Assert.That(dto.Image.ExposureStart, Is.EqualTo(real),
                "D-04: a real ExposureStart value must be preserved unchanged");
        }

        // ----------------------------------------------------------------
        // GOLDEN-PAYLOAD WIRE CONTRACT (T-18.1-01 mitigation / Task 2 wire-safety gate)
        //
        // Authored from BACKEND-API-SPEC SS6 / SS794 -- NOT regenerated from SDK output.
        // Proves the SDK 3.0->3.2 bump did not drift the wire format:
        //   (a) exact key set per SS6 (no added/renamed/dropped fields)
        //   (b) enum-typed fields serialize as their expected STRING values
        //   (c) null-vs-empty-string fields keep their documented representation
        //   (d) unavailable numerics serialize as the string "NaN" / -1 / MinValue per SS794
        //   (e) a stable FITS-header card subset (NAXIS1/NAXIS2) keeps card names + value types
        // ----------------------------------------------------------------

        [Test]
        public void GoldenPayload_WireContract_MatchesSpecAfterSdkBump()
        {
            // (a) KEY SET -- top-level objects + each sub-object's fields exactly per SS6.2.
            // Build a DTO that exercises both populated and unavailable (NaN/sentinel) paths.
            var args = CreateFullyPopulatedEventArgs();
            // Force unavailable-numeric paths so the "NaN" sentinels are exercised:
            args.MetaData.Camera.Temperature = double.NaN;   // -> "NaN"
            args.MetaData.Camera.SetPoint = double.NaN;      // -> "NaN"
            var dto = ImageSaveListener.ExtractFullMetadata(args);

            var json = JsonConvert.SerializeObject(dto);
            var root = JObject.Parse(json);

            // --- (a) Top-level key set ---
            var expectedTopLevel = new[]
            {
                "Image", "Camera", "Telescope", "Focuser", "Target", "Observer",
                "Weather", "Statistics", "Guiding", "Rotator", "FilterWheel",
                "Sequence", "FitsHeaders"
            };
            CollectionAssert.AreEquivalent(expectedTopLevel,
                ((JObject)root).Properties().Select(p => p.Name).ToArray(),
                "Top-level key set must match BACKEND-API-SPEC SS6 exactly");

            // Per-sub-object key sets (the wire schema -- no drift allowed).
            AssertKeys(root, "Image", "ExposureStart", "ExposureTime", "ImageType", "Binning",
                "Filter", "FilePath", "FileExtension", "FileSizeBytes", "ExposureNumber", "ExposureMidPoint");
            AssertKeys(root, "Camera", "Name", "Gain", "Offset", "Temperature", "SetPoint",
                "PixelSize", "SensorType", "ReadoutMode", "ElectronsPerADU", "BayerPattern", "ReadoutModeName");
            AssertKeys(root, "Telescope", "Name", "FocalLength", "FocalRatio", "Aperture", "RaJ2000",
                "DecJ2000", "Altitude", "Azimuth", "Airmass", "SideOfPier", "IsTracking");
            AssertKeys(root, "Focuser", "Name", "Position", "Temperature", "StepSize");
            AssertKeys(root, "Target", "Name", "RaJ2000", "DecJ2000", "Rotation");
            AssertKeys(root, "Observer", "Latitude", "Longitude", "Elevation", "SiteName", "Name", "Observatory");
            AssertKeys(root, "Weather", "Temperature", "Humidity", "DewPoint", "Pressure", "SkyQuality",
                "CloudCover", "WindSpeed", "WindDirection", "SkyTemperature", "WindGust", "SkyBrightness", "StarFWHM");
            AssertKeys(root, "Statistics", "Mean", "Median", "StdDev", "MAD", "Min", "Max", "HFR",
                "DetectedStars", "BitDepth", "HFRStDev", "FWHM", "Eccentricity");
            AssertKeys(root, "Guiding", "GuiderName", "RmsRA", "RmsDec", "RmsTotal", "PeakRA", "PeakDec", "DataPoints");
            AssertKeys(root, "Rotator", "Name", "Position", "MechanicalPosition", "StepSize");
            AssertKeys(root, "FilterWheel", "Name", "Position", "FilterName");
            AssertKeys(root, "Sequence", "Title");

            // --- (b) enum string values (SensorType, SideOfPier, BayerPattern serialize via ToString) ---
            Assert.That(root.SelectToken("Camera.SensorType").Type, Is.EqualTo(JTokenType.String),
                "SensorType must be a STRING, not a numeric enum");
            Assert.That(root.SelectToken("Telescope.SideOfPier").Type, Is.EqualTo(JTokenType.String),
                "SideOfPier must be a STRING, not a numeric enum");
            Assert.That(root.SelectToken("Telescope.SideOfPier").Value<string>(),
                Does.StartWith("pier"), "SideOfPier serializes as pierEast/pierWest/pierUnknown");
            Assert.That(root.SelectToken("Camera.BayerPattern").Type, Is.EqualTo(JTokenType.String),
                "BayerPattern must be a STRING, not a numeric enum");

            // --- (c) null-vs-empty-string behavior per SS6 ---
            // ImageType present -> non-null string. Empty device names -> "" (not null).
            Assert.That(root.SelectToken("Telescope.Name").Type, Is.EqualTo(JTokenType.String),
                "Empty Telescope.Name must stay an empty string, not null");
            Assert.That(root.SelectToken("Telescope.Name").Value<string>(), Is.EqualTo(string.Empty));
            // ExposureMidPoint is a populated real value here -> a string (ISO 8601), nullable type.
            Assert.That(root.SelectToken("Image.ExposureMidPoint").Type, Is.EqualTo(JTokenType.Date)
                .Or.EqualTo(JTokenType.String), "ExposureMidPoint serializes as an ISO-8601 string when set");

            // --- (d) sentinel serialization per SS794 ---
            // Unavailable double -> the STRING "NaN" (not JSON null, not numeric NaN token).
            Assert.That(root.SelectToken("Camera.Temperature").Type, Is.EqualTo(JTokenType.String),
                "Unavailable double must serialize as the string \"NaN\"");
            Assert.That(root.SelectToken("Camera.Temperature").Value<string>(), Is.EqualTo("NaN"));
            Assert.That(root.SelectToken("Camera.SetPoint").Value<string>(), Is.EqualTo("NaN"));
            // -1 sentinel for an unknown exposure number stays the integer -1 (here we set 42, so assert type).
            Assert.That(root.SelectToken("Image.ExposureNumber").Type, Is.EqualTo(JTokenType.Integer),
                "ExposureNumber is an integer sentinel field (-1 when unknown)");

            // --- (e) stable FITS-header card subset: NAXIS1/NAXIS2 names + string value type ---
            Assert.That(root.SelectToken("FitsHeaders.NAXIS1"), Is.Not.Null, "NAXIS1 card must exist");
            Assert.That(root.SelectToken("FitsHeaders.NAXIS2"), Is.Not.Null, "NAXIS2 card must exist");
            Assert.That(root.SelectToken("FitsHeaders.NAXIS1.Value"), Is.Not.Null,
                "NAXIS1 card must carry a Value field");
            Assert.That(root.SelectToken("FitsHeaders.NAXIS1.Comment"), Is.Not.Null,
                "NAXIS1 card must carry a Comment field");
            Assert.That(root.SelectToken("FitsHeaders.NAXIS1.Value").ToString(), Is.EqualTo("4144"),
                "NAXIS1 must carry the real width after the SDK bump");
            Assert.That(root.SelectToken("FitsHeaders.NAXIS2.Value").ToString(), Is.EqualTo("2822"),
                "NAXIS2 must carry the real height after the SDK bump");
        }

        /// <summary>Asserts the named sub-object's key set exactly equals the expected wire keys.</summary>
        private static void AssertKeys(JObject root, string objectName, params string[] expected)
        {
            var token = root.SelectToken(objectName) as JObject;
            Assert.That(token, Is.Not.Null, $"{objectName} sub-object must exist");
            CollectionAssert.AreEquivalent(expected, token.Properties().Select(p => p.Name).ToArray(),
                $"{objectName} key set must match BACKEND-API-SPEC SS6 exactly (no drift after SDK bump)");
        }
    }
}
