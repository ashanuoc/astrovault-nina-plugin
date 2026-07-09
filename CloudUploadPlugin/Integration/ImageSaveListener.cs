using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Astrovault.Core;
using Astrovault.Interfaces;
using Astrovault.Models;
using Newtonsoft.Json;
using NINA.Core.Utility;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.WPF.Base.Interfaces.Mediator;

namespace Astrovault.Integration
{
    /// <summary>
    /// Listens to NINA image save events and queues uploads with full metadata.
    /// </summary>
    public class ImageSaveListener : IDisposable
    {
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly IUploadManager uploadManager;
        private readonly IPathResolver pathResolver;
        private readonly Func<bool> isEnabledFunc;

        private bool isListening;

        // REL-03: tracks every in-flight fire-and-forget enqueue so a capture racing shutdown is
        // not lost. OnImageSaved registers its Task.Run task here; the task deregisters itself on
        // completion. FlushPendingAsync awaits the tracked set before shutdown cancels. Guarded by
        // pendingLock; shuttingDown stops new registrations once a flush has begun.
        private readonly HashSet<Task> pendingEnqueues = new HashSet<Task>();
        private readonly object pendingLock = new object();
        private volatile bool shuttingDown;

        public ImageSaveListener(
            IImageSaveMediator imageSaveMediator,
            IUploadManager uploadManager,
            IPathResolver pathResolver,
            Func<bool> isEnabledFunc)
        {
            this.imageSaveMediator = imageSaveMediator;
            this.uploadManager = uploadManager;
            this.pathResolver = pathResolver;
            this.isEnabledFunc = isEnabledFunc;
        }

        public void StartListening()
        {
            if (isListening) return;

            imageSaveMediator.ImageSaved += OnImageSaved;
            isListening = true;
            Logger.Info("[ImageSaveListener] Started listening for saved images");
        }

        public void StopListening()
        {
            if (!isListening) return;

            imageSaveMediator.ImageSaved -= OnImageSaved;
            isListening = false;
            Logger.Info("[ImageSaveListener] Stopped listening");
        }

        private void OnImageSaved(object sender, ImageSavedEventArgs e)
        {
            if (!isEnabledFunc())
            {
                return; // Upload disabled in settings
            }

            // REL-03: don't register a new enqueue once shutdown/flush has begun -- the capture
            // missed the flush window (an event that arrives during teardown is dropped rather than
            // tracked into a set that is no longer awaited).
            if (shuttingDown)
            {
                return;
            }

            // Fire-and-forget: don't block NINA's imaging pipeline. The task is TRACKED so that an
            // enqueue still in flight when NINA closes is awaited (flushed) before cancellation.
            Task task = null;
            task = Task.Run(async () =>
            {
                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var job = await CreateUploadJobAsync(e).ConfigureAwait(false);
                    // Cancellation checkpoint: timeout fires between job creation and enqueue
                    timeoutCts.Token.ThrowIfCancellationRequested();
                    await uploadManager.EnqueueJobAsync(job).ConfigureAwait(false);
                    Logger.Info($"[ImageSaveListener] Queued: {LogSanitizer.MaskPath(job.LocalPath)}");
                }
                catch (OperationCanceledException)
                {
                    Logger.Warning("[ImageSaveListener] Job creation timed out or cancelled");
                }
                catch (Exception ex)
                {
                    Logger.Error($"[ImageSaveListener] Failed to queue image: {ex.Message}");
                }
                finally
                {
                    Untrack(task);
                }
            });

            Track(task);
        }

        /// <summary>
        /// REL-03: registers an in-flight enqueue task so it can be flushed on shutdown. Skips
        /// registration once a flush has begun (the task would not be awaited).
        /// </summary>
        private void Track(Task task)
        {
            lock (pendingLock)
            {
                if (shuttingDown)
                {
                    return;
                }
                pendingEnqueues.Add(task);
            }
        }

        /// <summary>Deregisters a completed enqueue task.</summary>
        private void Untrack(Task task)
        {
            if (task == null)
            {
                return;
            }
            lock (pendingLock)
            {
                pendingEnqueues.Remove(task);
            }
        }

        /// <summary>
        /// REL-03: flushes (awaits) every in-flight fire-and-forget enqueue so a capture that was
        /// being queued when NINA started closing still reaches disk before the upload pipeline is
        /// cancelled. Sets <see cref="shuttingDown"/> so no new enqueues are tracked after the flush
        /// begins, snapshots the currently-tracked tasks, and awaits them bounded by
        /// <paramref name="timeout"/>. Individual enqueue tasks never throw (they swallow their own
        /// exceptions), so a faulted task cannot break the flush.
        /// </summary>
        public async Task FlushPendingAsync(TimeSpan timeout)
        {
            shuttingDown = true;

            Task[] snapshot;
            lock (pendingLock)
            {
                snapshot = pendingEnqueues.ToArray();
            }

            if (snapshot.Length == 0)
            {
                return;
            }

            Logger.Info($"[ImageSaveListener] Flushing {snapshot.Length} in-flight enqueue(s) before shutdown");
            try
            {
                await Task.WhenAll(snapshot).WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Logger.Warning($"[ImageSaveListener] Flush timed out after {timeout.TotalSeconds:F0}s; some enqueues may not have completed");
            }
            catch (Exception ex)
            {
                // Enqueue tasks swallow their own faults; this is defensive only.
                Logger.Warning($"[ImageSaveListener] Flush observed a fault: {ex.Message}");
            }
        }

        private async Task<UploadJob> CreateUploadJobAsync(ImageSavedEventArgs e)
        {
            var localPath = e.PathToImage.LocalPath;

            // REL-11: a freshly-saved image may be momentarily AV/sync-locked or briefly
            // zero-length. When the file is PRESENT but reads back as zero-length (a transient
            // write/sync race), read its size via NINA's bounded Retry.Do so the queued FileSize
            // reflects the real file rather than a transient 0. Do NOT hand-roll the retry.
            //
            // If the file is not present at all on the first check, this is NOT the REL-11
            // transient case — preserve the prior behavior (FileSize = 0, still enqueue) without
            // burning retry time; the upload pipeline re-checks File.Exists at upload start.
            long fileSize = 0;
            var fileInfo = new FileInfo(localPath);
            if (fileInfo.Exists)
            {
                try
                {
                    fileSize = await Retry.Do(() =>
                    {
                        var fi = new FileInfo(localPath);
                        if (fi.Exists && fi.Length == 0)
                        {
                            // Zero-length write/sync race — retry until the bytes land.
                            throw new IOException($"File is momentarily zero-length: {localPath}");
                        }
                        return fi.Exists ? fi.Length : 0;
                    }, TimeSpan.FromMilliseconds(200), 5).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[ImageSaveListener] File size still zero after retries for {localPath}: {ex.Message}");
                    fileSize = 0;
                }
            }

            var job = new UploadJob
            {
                Id = Guid.NewGuid(),
                LocalPath = localPath,
                RelativePath = pathResolver.GetUploadPath(e.PathToImage),
                FileSize = fileSize,
                CapturedAt = DateTime.UtcNow,
                Status = UploadStatus.Pending,
                RetryCount = 0
            };

            ExtractBasicMetadata(job, e);
            job.MetadataJson = SerializeFullMetadata(e);

            return job;
        }

        private void ExtractBasicMetadata(UploadJob job, ImageSavedEventArgs e)
        {
            var meta = e.MetaData;
            if (meta == null) return;

            // Image info
            job.Filter = meta.FilterWheel?.Filter ?? string.Empty;
            job.Duration = meta.Image?.ExposureTime ?? 0;
            job.FileType = Path.GetExtension(job.LocalPath)?.TrimStart('.').ToUpperInvariant();
        }

        /// <summary>
        /// Serializes full metadata to JSON for persistent storage.
        /// Called at queue time when ImageSavedEventArgs is available.
        /// </summary>
        private string SerializeFullMetadata(ImageSavedEventArgs e)
        {
            try
            {
                var metadata = ExtractFullMetadata(e);
                return JsonConvert.SerializeObject(metadata);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[ImageSaveListener] Metadata serialization failed: {ex.Message}");
                return "{}";
            }
        }

        /// <summary>
        /// Extracts comprehensive metadata for API upload.
        /// Called separately when building the upload request.
        /// </summary>
        public static ImageMetadataDto ExtractFullMetadata(ImageSavedEventArgs e)
        {
            var dto = new ImageMetadataDto();
            var meta = e.MetaData;

            if (meta == null) return dto;

            ExtractImageInfo(dto, meta, e);
            ExtractCameraInfo(dto, meta);
            ExtractTelescopeInfo(dto, meta);
            ExtractFocuserInfo(dto, meta);
            ExtractTargetInfo(dto, meta);
            ExtractObserverInfo(dto, meta);
            ExtractWeatherInfo(dto, meta);
            ExtractStatistics(dto, e);
            ExtractFilterWheelInfo(dto, meta);
            ExtractRotatorInfo(dto, meta);
            ExtractGuidingInfo(dto, meta);
            ExtractSequenceInfo(dto, meta);
            ExtractFitsHeaders(dto, meta, e);

            return dto;
        }

        private static void ExtractImageInfo(ImageMetadataDto dto, ImageMetaData meta, ImageSavedEventArgs e)
        {
            // D-04: guard ExposureStart's DateTime.MinValue to null, mirroring the ExposureMidPoint
            // guard below. The DTO field is already DateTime?, so this is no wire-format change.
            var exposureStart = meta.Image?.ExposureStart;
            dto.Image.ExposureStart = (exposureStart.HasValue && exposureStart.Value != DateTime.MinValue)
                ? exposureStart
                : null;
            dto.Image.ExposureTime = meta.Image?.ExposureTime ?? 0;
            dto.Image.ImageType = meta.Image?.ImageType ?? string.Empty;
            dto.Image.Binning = ResolveBinning(meta);
            dto.Image.Filter = meta.FilterWheel?.Filter ?? string.Empty;
            dto.Image.FilePath = e.PathToImage?.LocalPath ?? string.Empty;
            dto.Image.FileExtension = Path.GetExtension(dto.Image.FilePath);

            // File size from disk
            var fileInfo = new FileInfo(dto.Image.FilePath);
            if (fileInfo.Exists)
            {
                dto.Image.FileSizeBytes = fileInfo.Length;
            }

            dto.Image.ExposureNumber = meta.Image?.ExposureNumber ?? -1;
            var midPoint = meta.Image?.ExposureMidPoint;
            dto.Image.ExposureMidPoint = (midPoint.HasValue && midPoint.Value != DateTime.MinValue) ? midPoint : null;
        }

        /// <summary>
        /// MISS-03: Resolve the binning string. Image.Binning is populated for sequencer captures
        /// but is empty for non-sequencer (snapshot/manual) captures. In that case fall back to the
        /// hardware Camera binning members (CameraParameter.BinX/BinY, verified present on the
        /// stable NINA.Image 3.2.0.9001 SDK). Final fallback is "1x1" to preserve prior behavior.
        /// </summary>
        private static string ResolveBinning(ImageMetaData meta)
        {
            var imageBinning = meta.Image?.Binning;
            if (!string.IsNullOrWhiteSpace(imageBinning))
            {
                return imageBinning;
            }

            // Hardware fallback: Camera.BinX/BinY (ints). Only use when both are valid (>0).
            var binX = meta.Camera?.BinX ?? 0;
            var binY = meta.Camera?.BinY ?? 0;
            if (binX > 0 && binY > 0)
            {
                return $"{binX}x{binY}";
            }

            return "1x1";
        }

        private static void ExtractCameraInfo(ImageMetadataDto dto, ImageMetaData meta)
        {
            dto.Camera.Name = meta.Camera?.Name ?? string.Empty;
            dto.Camera.Gain = meta.Camera?.Gain ?? 0;
            dto.Camera.Offset = meta.Camera?.Offset ?? 0;
            dto.Camera.Temperature = meta.Camera?.Temperature ?? 0;
            dto.Camera.SetPoint = meta.Camera?.SetPoint ?? 0;
            dto.Camera.PixelSize = meta.Camera?.PixelSize ?? 0;
            dto.Camera.SensorType = meta.Camera?.SensorType.ToString() ?? string.Empty;
            dto.Camera.ReadoutMode = meta.Camera?.ReadoutModeIndex ?? 0;
            dto.Camera.ElectronsPerADU = meta.Camera?.ElectronsPerADU ?? 0;
            dto.Camera.BayerPattern = meta.Camera?.BayerPattern.ToString() ?? string.Empty;
            dto.Camera.ReadoutModeName = meta.Camera?.ReadoutModeName ?? string.Empty;
        }

        private static void ExtractTelescopeInfo(ImageMetadataDto dto, ImageMetaData meta)
        {
            dto.Telescope.Name = meta.Telescope?.Name ?? string.Empty;
            dto.Telescope.FocalLength = meta.Telescope?.FocalLength ?? 0;
            dto.Telescope.FocalRatio = meta.Telescope?.FocalRatio ?? 0;
            dto.Telescope.Altitude = meta.Telescope?.Altitude ?? 0;
            dto.Telescope.Azimuth = meta.Telescope?.Azimuth ?? 0;
            dto.Telescope.Airmass = meta.Telescope?.Airmass ?? 0;
            dto.Telescope.SideOfPier = meta.Telescope?.SideOfPier.ToString() ?? string.Empty;

            if (meta.Telescope?.Coordinates != null)
            {
                dto.Telescope.RaJ2000 = meta.Telescope.Coordinates.RA;
                dto.Telescope.DecJ2000 = meta.Telescope.Coordinates.Dec;
            }

            // D-18: Compute aperture from focal length and focal ratio when both are available.
            // Use the DTO values (which have already applied ?? 0, converting NaN to 0).
            var fl = dto.Telescope.FocalLength;
            var fr = dto.Telescope.FocalRatio;
            if (fl > 0 && fr > 0)
            {
                dto.Telescope.Aperture = fl / fr;
            }
        }

        private static void ExtractFocuserInfo(ImageMetadataDto dto, ImageMetaData meta)
        {
            dto.Focuser.Name = meta.Focuser?.Name ?? string.Empty;
            dto.Focuser.Position = meta.Focuser?.Position ?? 0;
            dto.Focuser.Temperature = meta.Focuser?.Temperature ?? 0;
            dto.Focuser.StepSize = (int)(meta.Focuser?.StepSize ?? 0);
        }

        private static void ExtractTargetInfo(ImageMetadataDto dto, ImageMetaData meta)
        {
            dto.Target.Name = meta.Target?.Name ?? string.Empty;
            dto.Target.Rotation = meta.Target?.PositionAngle ?? 0;

            if (meta.Target?.Coordinates != null)
            {
                dto.Target.RaJ2000 = meta.Target.Coordinates.RA;
                dto.Target.DecJ2000 = meta.Target.Coordinates.Dec;
            }
        }

        private static void ExtractObserverInfo(ImageMetadataDto dto, ImageMetaData meta)
        {
            dto.Observer.Latitude = meta.Observer?.Latitude ?? 0;
            dto.Observer.Longitude = meta.Observer?.Longitude ?? 0;
            dto.Observer.Elevation = meta.Observer?.Elevation ?? 0;
            // D-15, D-16, D-17 / MISS-01: Observer.Name, Observatory, Site are exposed by the
            // stable NINA.Image 3.2.0.9001 ObserverParameter (verified via reflection).
            // The SDK member is `Site`; the wire/DTO field name stays `SiteName` (RESEARCH A5 --
            // the DTO field is the documented wire name per BACKEND-API-SPEC SS6 and must not change).
            dto.Observer.Name = meta.Observer?.Name ?? string.Empty;
            dto.Observer.Observatory = meta.Observer?.Observatory ?? string.Empty;
            dto.Observer.SiteName = meta.Observer?.Site ?? string.Empty;
        }

        private static void ExtractWeatherInfo(ImageMetadataDto dto, ImageMetaData meta)
        {
            dto.Weather.Temperature = meta.WeatherData?.Temperature ?? 0;
            dto.Weather.Humidity = meta.WeatherData?.Humidity ?? 0;
            dto.Weather.DewPoint = meta.WeatherData?.DewPoint ?? 0;
            dto.Weather.Pressure = meta.WeatherData?.Pressure ?? 0;
            dto.Weather.SkyQuality = meta.WeatherData?.SkyQuality ?? 0;
            dto.Weather.CloudCover = meta.WeatherData?.CloudCover ?? 0;
            dto.Weather.WindSpeed = meta.WeatherData?.WindSpeed ?? 0;
            dto.Weather.WindDirection = (meta.WeatherData?.WindDirection ?? 0).ToString();
            dto.Weather.SkyTemperature = meta.WeatherData?.SkyTemperature ?? 0;
            // Note: NINA's WeatherDataParameter defaults these to double.NaN.
            // The ?? 0 only fires when WeatherData itself is null (via ?. operator).
            // When WeatherData exists but no weather station is connected, NaN flows through
            // and Newtonsoft.Json serializes it as "NaN" string -- matching existing field behavior.
            dto.Weather.WindGust = meta.WeatherData?.WindGust ?? 0;
            dto.Weather.SkyBrightness = meta.WeatherData?.SkyBrightness ?? 0;
            dto.Weather.StarFWHM = meta.WeatherData?.StarFWHM ?? 0;
        }

        private static void ExtractStatistics(ImageMetadataDto dto, ImageSavedEventArgs e)
        {
            var stats = e.Statistics;
            if (stats == null) return;

            dto.Statistics.Mean = stats.Mean;
            dto.Statistics.Median = stats.Median;
            dto.Statistics.StdDev = stats.StDev;
            dto.Statistics.MAD = stats.MedianAbsoluteDeviation;
            dto.Statistics.Min = stats.Min;
            dto.Statistics.Max = stats.Max;
            dto.Statistics.BitDepth = stats.BitDepth;

            var starInfo = e.StarDetectionAnalysis;
            if (starInfo != null)
            {
                dto.Statistics.HFR = starInfo.HFR;
                dto.Statistics.DetectedStars = starInfo.DetectedStars;
                dto.Statistics.HFRStDev = starInfo.HFRStDev;
                // MISS-01 / D-10, D-11: FWHM and Eccentricity are NOT exposed by the published
                // stable NINA.Plugin 3.2.0.9001 SDK. Verified via reflection over the entire SDK
                // surface: IStarDetectionAnalysis exposes only DetectedStars/HFR/HFRStDev/StarList,
                // and DetectedStar exposes only HFR/AverageBrightness/Background/BoundingBox/
                // MaxBrightness/Position -- no FWHM, no Eccentricity anywhere in the NuGet package.
                // (They exist in the NINA full source tree but are not part of the published API.)
                // Per BACKEND-API-SPEC SS6, both fields are documented as `0.0` when star detection
                // does not provide them, so leaving the DTO defaults at 0.0 is wire-compliant.
                // This is a Rule-4 architectural finding flagged in the plan SUMMARY: the plan's
                // premise that these members are available on stable 3.2 does not hold for the
                // published package. No live assignment is possible without a derived approximation
                // (e.g. FWHM ~= 2x median per-star HFR), which would not match NINA's own values and
                // is therefore deliberately NOT done to avoid emitting misleading metrics.
            }
        }

        private static void ExtractFilterWheelInfo(ImageMetadataDto dto, ImageMetaData meta)
        {
            dto.FilterWheel.Name = meta.FilterWheel?.Name ?? string.Empty;
            dto.FilterWheel.FilterName = meta.FilterWheel?.Filter ?? string.Empty;
        }

        private static void ExtractRotatorInfo(ImageMetadataDto dto, ImageMetaData meta)
        {
            dto.Rotator.Name = meta.Rotator?.Name ?? string.Empty;
            dto.Rotator.Position = meta.Rotator?.Position ?? 0;
            dto.Rotator.MechanicalPosition = meta.Rotator?.MechanicalPosition ?? 0;
            // NaN flows through when no rotator connected (same as Position/MechanicalPosition above)
            dto.Rotator.StepSize = meta.Rotator?.StepSize ?? 0;
        }

        private static void ExtractGuidingInfo(ImageMetadataDto dto, ImageMetaData meta)
        {
            var rms = meta.Image?.RecordedRMS;
            if (rms == null) return;

            dto.Guiding.RmsRA = rms.RA;
            dto.Guiding.RmsDec = rms.Dec;
            dto.Guiding.RmsTotal = rms.Total;
            dto.Guiding.PeakRA = rms.PeakRA;
            dto.Guiding.PeakDec = rms.PeakDec;
            dto.Guiding.DataPoints = rms.DataPoints;
        }

        private static void ExtractSequenceInfo(ImageMetadataDto dto, ImageMetaData meta)
        {
            dto.Sequence.Title = meta.Sequence?.Title ?? string.Empty;
        }

        private static void ExtractFitsHeaders(ImageMetadataDto dto, ImageMetaData meta, ImageSavedEventArgs e)
        {
            // MISS-02: capture the REAL image dimensions from the rendered bitmap that NINA hands
            // us on the event (BitmapSource). Fall back to 1 only when the image is unavailable
            // (e.g. some sub-5MB single-file paths). These feed NAXIS1/NAXIS2 directly.
            int width = e.Image?.PixelWidth ?? 1;
            int height = e.Image?.PixelHeight ?? 1;

            // Seed NAXIS1/NAXIS2 from the real dimensions FIRST so they are always present even if
            // PopulateFromMetaData throws (e.g. the native NOVAS31lib.dll astrometry dependency is
            // absent in a headless/test environment). PopulateFromMetaData below overwrites these
            // with NINA's own NAXIS cards when it succeeds (same value), so production is unchanged.
            dto.FitsHeaders["NAXIS1"] = new FitsHeaderEntry { Value = width, Comment = string.Empty };
            dto.FitsHeaders["NAXIS2"] = new FitsHeaderEntry { Value = height, Comment = string.Empty };

            try
            {
                // Use NINA's FITSHeader to generate standard headers
                var fitsHeader = new FITSHeader(width, height);
                fitsHeader.PopulateFromMetaData(meta);

                // Copy all header cards to our DTO
                foreach (var card in fitsHeader.HeaderCards)
                {
                    dto.FitsHeaders[card.Key] = new FitsHeaderEntry
                    {
                        Value = card.OriginalValue,
                        Comment = card.Comment ?? string.Empty
                    };
                }

                // Also include any GenericHeaders (custom/plugin headers)
                if (meta.GenericHeaders != null)
                {
                    foreach (var header in meta.GenericHeaders)
                    {
                        if (!dto.FitsHeaders.ContainsKey(header.Key))
                        {
                            dto.FitsHeaders[header.Key] = new FitsHeaderEntry
                            {
                                Value = GetHeaderValue(header),
                                Comment = header.Comment ?? string.Empty
                            };
                        }
                    }
                }

                Logger.Debug($"[ImageSaveListener] Extracted {dto.FitsHeaders.Count} FITS headers");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[ImageSaveListener] Failed to extract FITS headers: {ex.Message}");
            }
        }

        private static object GetHeaderValue(IGenericMetaDataHeader header)
        {
            return header switch
            {
                StringMetaDataHeader s => s.Value,
                IntMetaDataHeader i => i.Value,
                DoubleMetaDataHeader d => d.Value,
                BoolMetaDataHeader b => b.Value,
                DateTimeMetaDataHeader dt => dt.Value,
                _ => null
            };
        }

        public void Dispose()
        {
            StopListening();
        }
    }
}
