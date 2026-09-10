using NINA.Core.Utility;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace NINA.Plugin.Livestack.Image {
    internal static class LiveStackPreview {
        internal readonly record struct Settings(double StretchFactor, double BlackClipping,
            bool EnableBackgroundExtraction, double BackgroundExtractionAmount, int Downsample);

        // One expensive renderer across all tabs bounds native bitmaps and managed scratch.
        private static readonly SemaphoreSlim renderLock = new(1, 1);

        internal static async Task RenderAsync(Action render, CancellationToken token) {
            try {
                await renderLock.WaitAsync(token);
                try {
                    await Task.Run(render, token);
                } finally {
                    renderLock.Release();
                }
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                // Canceling a preview keeps the previously published image.
            } catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        // The caller owns the returned bitmap. Source pixels are only read.
        internal static ImageMath.BitmapWithMedian CreateStretchedBitmap(float[] pixels, ImageProperties properties, double stretchFactor, double blackClipping, int downsample, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            ImageMath.BitmapWithMedian bitmap = LivestackMediator.GetImageMath().CreateGrayBitmap(pixels, properties.Width, properties.Height);
            try {
                var filter = ImageUtility.GetColorRemappingFilter(new MedianOnlyStatistics(bitmap.Median, bitmap.MedianAbsoluteDeviation, properties.BitDepth), stretchFactor, blackClipping, PixelFormats.Gray16);
                filter.ApplyInPlace(bitmap.Bitmap);
                token.ThrowIfCancellationRequested();
                if (downsample > 1) {
                    var smaller = LivestackMediator.GetImageMath().DownsampleGray16(bitmap.Bitmap, downsample);
                    bitmap.Dispose();
                    return new ImageMath.BitmapWithMedian(smaller, bitmap.Median, bitmap.MedianAbsoluteDeviation);
                }
                return bitmap;
            } catch {
                bitmap.Dispose();
                throw;
            }
        }
    }
}
