using System;
using System.Collections.Generic;

namespace Astrovault.Models
{
    /// <summary>
    /// Complete image metadata extracted from N.I.N.A.
    /// Sent to BFF as JSON with each upload - BFF doesn't need to parse FITS.
    /// </summary>
    public class ImageMetadataDto
    {
        public ImageInfo Image { get; set; } = new ImageInfo();
        public CameraInfo Camera { get; set; } = new CameraInfo();
        public TelescopeInfo Telescope { get; set; } = new TelescopeInfo();
        public FocuserInfo Focuser { get; set; } = new FocuserInfo();
        public TargetInfo Target { get; set; } = new TargetInfo();
        public ObserverInfo Observer { get; set; } = new ObserverInfo();
        public WeatherInfo Weather { get; set; } = new WeatherInfo();
        public ImageStatistics Statistics { get; set; } = new ImageStatistics();
        public GuidingInfo Guiding { get; set; } = new GuidingInfo();
        public RotatorInfo Rotator { get; set; } = new RotatorInfo();
        public FilterWheelInfo FilterWheel { get; set; } = new FilterWheelInfo();
        public SequenceInfo Sequence { get; set; } = new SequenceInfo();

        /// <summary>
        /// Raw FITS header keywords from GenericHeaders.
        /// Key = FITS keyword, Value = header entry with value and comment.
        /// </summary>
        public Dictionary<string, FitsHeaderEntry> FitsHeaders { get; set; } = new Dictionary<string, FitsHeaderEntry>();
    }

    /// <summary>
    /// Core image capture information.
    /// </summary>
    public class ImageInfo
    {
        /// <summary>UTC timestamp when exposure started.</summary>
        public DateTime? ExposureStart { get; set; }

        /// <summary>Exposure duration in seconds.</summary>
        public double ExposureTime { get; set; }

        /// <summary>Frame type: LIGHT, DARK, FLAT, BIAS, SNAPSHOT.</summary>
        public string ImageType { get; set; }

        /// <summary>Binning setting, e.g., "1x1", "2x2".</summary>
        public string Binning { get; set; }

        /// <summary>Filter name: Ha, OIII, SII, L, R, G, B, etc.</summary>
        public string Filter { get; set; }

        /// <summary>Original file path on capture machine.</summary>
        public string FilePath { get; set; }

        /// <summary>File extension: .fits, .xisf, .png, etc.</summary>
        public string FileExtension { get; set; }

        /// <summary>File size in bytes.</summary>
        public long FileSizeBytes { get; set; }

        /// <summary>Sequential exposure number in the imaging sequence.</summary>
        public int ExposureNumber { get; set; } = -1;

        /// <summary>Midpoint timestamp of exposure (UTC). Null if not calculated.</summary>
        public DateTime? ExposureMidPoint { get; set; }
    }

    /// <summary>
    /// Camera hardware and settings information.
    /// </summary>
    public class CameraInfo
    {
        /// <summary>Camera model name.</summary>
        public string Name { get; set; }

        /// <summary>Gain setting (camera-specific units).</summary>
        public int Gain { get; set; }

        /// <summary>Offset/brightness setting.</summary>
        public int Offset { get; set; }

        /// <summary>Sensor temperature in Celsius.</summary>
        public double Temperature { get; set; }

        /// <summary>Target cooling temperature in Celsius.</summary>
        public double SetPoint { get; set; }

        /// <summary>Pixel size in microns.</summary>
        public double PixelSize { get; set; }

        /// <summary>Sensor type: Monochrome, Color.</summary>
        public string SensorType { get; set; }

        /// <summary>Readout mode index.</summary>
        public int ReadoutMode { get; set; }

        /// <summary>Electrons per ADU (e-/ADU).</summary>
        public double ElectronsPerADU { get; set; }

        /// <summary>Bayer pattern for color sensors: RGGB, GRBG, etc.</summary>
        public string BayerPattern { get; set; }

        /// <summary>Human-readable readout mode name.</summary>
        public string ReadoutModeName { get; set; }
    }

    /// <summary>
    /// Telescope and mount information.
    /// </summary>
    public class TelescopeInfo
    {
        /// <summary>Telescope/mount name.</summary>
        public string Name { get; set; }

        /// <summary>Focal length in millimeters.</summary>
        public double FocalLength { get; set; }

        /// <summary>Focal ratio (e.g., 5.0 for f/5).</summary>
        public double FocalRatio { get; set; }

        /// <summary>Aperture diameter in millimeters.</summary>
        public double Aperture { get; set; }

        /// <summary>Right Ascension J2000 in hours (0-24).</summary>
        public double RaJ2000 { get; set; }

        /// <summary>Declination J2000 in degrees (-90 to +90).</summary>
        public double DecJ2000 { get; set; }

        /// <summary>Altitude above horizon in degrees.</summary>
        public double Altitude { get; set; }

        /// <summary>Azimuth in degrees (0-360).</summary>
        public double Azimuth { get; set; }

        /// <summary>Atmospheric airmass (1.0 at zenith).</summary>
        public double Airmass { get; set; }

        /// <summary>Side of pier: East, West, Unknown.</summary>
        public string SideOfPier { get; set; }

        /// <summary>Whether mount is actively tracking.</summary>
        public bool IsTracking { get; set; }
    }

    /// <summary>
    /// Focuser position and temperature.
    /// </summary>
    public class FocuserInfo
    {
        /// <summary>Focuser device name.</summary>
        public string Name { get; set; }

        /// <summary>Current focuser position in steps.</summary>
        public int Position { get; set; }

        /// <summary>Focuser temperature sensor reading in Celsius.</summary>
        public double Temperature { get; set; }

        /// <summary>Step size in microns (if known).</summary>
        public int StepSize { get; set; }
    }

    /// <summary>
    /// Target object information.
    /// </summary>
    public class TargetInfo
    {
        /// <summary>Target name: M42, NGC 7000, IC 1396, etc.</summary>
        public string Name { get; set; }

        /// <summary>Target Right Ascension J2000 in hours.</summary>
        public double RaJ2000 { get; set; }

        /// <summary>Target Declination J2000 in degrees.</summary>
        public double DecJ2000 { get; set; }

        /// <summary>Target rotation angle in degrees.</summary>
        public double Rotation { get; set; }
    }

    /// <summary>
    /// Observer location information.
    /// </summary>
    public class ObserverInfo
    {
        /// <summary>Observer latitude in degrees (-90 to +90).</summary>
        public double Latitude { get; set; }

        /// <summary>Observer longitude in degrees (-180 to +180).</summary>
        public double Longitude { get; set; }

        /// <summary>Observer elevation in meters above sea level.</summary>
        public double Elevation { get; set; }

        /// <summary>Observation site name.</summary>
        public string SiteName { get; set; } = string.Empty;

        /// <summary>Observer's name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Observatory name.</summary>
        public string Observatory { get; set; } = string.Empty;
    }

    /// <summary>
    /// Weather and environmental conditions.
    /// </summary>
    public class WeatherInfo
    {
        /// <summary>Ambient temperature in Celsius.</summary>
        public double Temperature { get; set; }

        /// <summary>Relative humidity percentage (0-100).</summary>
        public double Humidity { get; set; }

        /// <summary>Dew point temperature in Celsius.</summary>
        public double DewPoint { get; set; }

        /// <summary>Atmospheric pressure in hPa.</summary>
        public double Pressure { get; set; }

        /// <summary>Sky quality in mag/arcsec² (from SQM).</summary>
        public double SkyQuality { get; set; }

        /// <summary>Cloud cover percentage (0-100).</summary>
        public double CloudCover { get; set; }

        /// <summary>Wind speed in m/s.</summary>
        public double WindSpeed { get; set; }

        /// <summary>Wind direction: N, NE, E, SE, S, SW, W, NW.</summary>
        public string WindDirection { get; set; }

        /// <summary>Sky temperature in Celsius (from cloud sensor).</summary>
        public double SkyTemperature { get; set; }

        /// <summary>Wind gust speed in m/s.</summary>
        public double WindGust { get; set; }

        /// <summary>Sky brightness in mag/arcsec squared.</summary>
        public double SkyBrightness { get; set; }

        /// <summary>Average FWHM from weather station seeing monitor.</summary>
        public double StarFWHM { get; set; }
    }

    /// <summary>
    /// Image analysis statistics.
    /// </summary>
    public class ImageStatistics
    {
        /// <summary>Mean pixel value.</summary>
        public double Mean { get; set; }

        /// <summary>Median pixel value.</summary>
        public double Median { get; set; }

        /// <summary>Standard deviation of pixel values.</summary>
        public double StdDev { get; set; }

        /// <summary>Median Absolute Deviation.</summary>
        public double MAD { get; set; }

        /// <summary>Minimum pixel value.</summary>
        public int Min { get; set; }

        /// <summary>Maximum pixel value.</summary>
        public int Max { get; set; }

        /// <summary>Half-Flux Radius in pixels (star quality metric).</summary>
        public double HFR { get; set; }

        /// <summary>Number of stars used for analysis.</summary>
        public int DetectedStars { get; set; }

        /// <summary>Image bit depth (8, 12, 14, or 16).</summary>
        public int BitDepth { get; set; }

        /// <summary>Standard deviation of HFR across detected stars.</summary>
        public double HFRStDev { get; set; }

        /// <summary>Average Full Width at Half Maximum in pixels (industry standard star quality metric).</summary>
        public double FWHM { get; set; }

        /// <summary>Average star eccentricity (0=round, 1=elongated). Detects tracking errors and optical aberrations.</summary>
        public double Eccentricity { get; set; }
    }

    /// <summary>
    /// Autoguiding performance metrics.
    /// </summary>
    public class GuidingInfo
    {
        /// <summary>Guiding software name: PHD2, MetaGuide, etc.</summary>
        public string GuiderName { get; set; }

        /// <summary>RMS error in RA in arcseconds.</summary>
        public double RmsRA { get; set; }

        /// <summary>RMS error in Dec in arcseconds.</summary>
        public double RmsDec { get; set; }

        /// <summary>Total RMS error in arcseconds.</summary>
        public double RmsTotal { get; set; }

        /// <summary>Peak RA error in arcseconds.</summary>
        public double PeakRA { get; set; }

        /// <summary>Peak Dec error in arcseconds.</summary>
        public double PeakDec { get; set; }

        /// <summary>Number of guiding data points used to compute RMS values. Indicates confidence in guiding metrics.</summary>
        public int DataPoints { get; set; }
    }

    /// <summary>
    /// Camera rotator information.
    /// </summary>
    public class RotatorInfo
    {
        /// <summary>Rotator device name.</summary>
        public string Name { get; set; }

        /// <summary>Sky position angle in degrees.</summary>
        public double Position { get; set; }

        /// <summary>Mechanical rotator position in degrees.</summary>
        public double MechanicalPosition { get; set; }

        /// <summary>Degrees per step.</summary>
        public double StepSize { get; set; }
    }

    /// <summary>
    /// Filter wheel information.
    /// </summary>
    public class FilterWheelInfo
    {
        /// <summary>Filter wheel device name.</summary>
        public string Name { get; set; }

        /// <summary>Current filter slot position (1-based).</summary>
        public int Position { get; set; }

        /// <summary>Name of the current filter.</summary>
        public string FilterName { get; set; }
    }

    /// <summary>
    /// Sequence information from NINA's advanced sequencer.
    /// </summary>
    public class SequenceInfo
    {
        /// <summary>Sequence title from NINA's advanced sequencer.</summary>
        public string Title { get; set; }
    }

    /// <summary>
    /// Single FITS header entry with value and comment.
    /// </summary>
    public class FitsHeaderEntry
    {
        /// <summary>Header value (can be string, int, double, bool, or DateTime).</summary>
        public object Value { get; set; }

        /// <summary>FITS header comment describing the keyword.</summary>
        public string Comment { get; set; }
    }
}
