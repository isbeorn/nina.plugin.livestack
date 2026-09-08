using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Image.FileFormat;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack.Image;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugin.Livestack.LivestackDockables {
    public partial class LivestackDockable {
        private readonly SemaphoreSlim frameProcessing = new(1, 1);

        /// <summary>
        /// Processes a decoded capture through the same preparation, quality gates, calibration,
        /// alignment and preview path as the capture queue. Owns only its temporary FITS copy.
        /// </summary>
        public async Task<bool> ProcessImageAsync(IImageData image, Guid correlation, CancellationToken token) {
            ArgumentNullException.ThrowIfNull(image);
            token.ThrowIfCancellationRequested();
            if (image.MetaData.Image.ImageType != NINA.Equipment.Model.CaptureSequence.ImageTypes.LIGHT
                && image.MetaData.Image.ImageType != NINA.Equipment.Model.CaptureSequence.ImageTypes.SNAPSHOT) {
                Logger.Info($"Skipping {image.MetaData.Image.ImageType} frame; live stacking accepts LIGHT and SNAPSHOT frames.");
                return false;
            }
            LiveStackItem item = await PrepareFrameAsync(image, Array.Empty<ImagePattern>(), token);
            try {
                return await ProcessFrameAsync(item, correlation, token);
            } finally {
                try {
                    File.Delete(item.Path);
                } finally {
                    LiveStackMemoryPressure.CollectIfNeeded("frame completed");
                }
            }
        }

        private async Task<bool> ProcessFrameAsync(LiveStackItem item, Guid correlation, CancellationToken token) {
            await frameProcessing.WaitAsync(token);
            try {
                if (item.StarList.Count < 8) {
                    Logger.Info($"Skipping frame as not enough stars have been detected ({item.StarList.Count})");
                    return false;
                }
                return ItemPassesQuality(item) && await StackItem(item, correlation, token);
            } finally {
                frameProcessing.Release();
            }
        }

        private async Task<LiveStackItem> PrepareFrameAsync(IImageData image, IList<ImagePattern> patterns, CancellationToken token) {
            await image.Statistics;
            token.ThrowIfCancellationRequested();
            IStarDetectionAnalysis analysis = image.StarDetectionAnalysis;
            if (NeedsStarDetection(analysis)) {
                IRenderedImage render = image.RenderImage();
                render = await render.Stretch(profileService.ActiveProfile.ImageSettings.AutoStretchFactor,
                    profileService.ActiveProfile.ImageSettings.BlackClipping, profileService.ActiveProfile.ImageSettings.UnlinkedStretch);
                render = await render.DetectStars(false, profileService.ActiveProfile.ImageSettings.StarSensitivity,
                    profileService.ActiveProfile.ImageSettings.NoiseReduction, token, default);
                analysis = render.RawImageData.StarDetectionAnalysis;
            }
            string pattern = Path.GetFileName(profileService.ActiveProfile.ImageFileSettings.GetFilePattern(image.MetaData.Image.ImageType));
            string path = await image.SaveToDisk(new FileSaveInfo {
                FilePath = Path.Combine(LivestackMediator.Plugin.WorkingDirectory, "temp"),
                FilePattern = pattern,
                FileType = NINA.Core.Enum.FileTypeEnum.FITS
            }, token, true, patterns);
            try {
                return new LiveStackItem(path, image.MetaData.Target.Name, image.MetaData.FilterWheel.Filter,
                    image.MetaData.Image.ExposureTime, image.MetaData.Camera.Gain, image.MetaData.Camera.Offset,
                    image.Properties.Width, image.Properties.Height, image.Properties.BitDepth,
                    image.Properties.IsBayered, analysis, image.MetaData);
            } catch {
                File.Delete(path);
                throw;
            }
        }
    }
}
