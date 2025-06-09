using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Drawing.Imaging;
using System.Drawing;
using MathNet.Numerics.Statistics;

namespace NINA.Plugin.Livestack.Image {

    public class ImageMath {

        public static void SequentialStack(float[] image, float[] stack, int stackImageCount) {
            for (int i = 0; i < stack.Length; i++) {
                stack[i] = (stackImageCount * stack[i] + image[i]) / (stackImageCount + 1);
            }
        }

        public static List<Accord.Point> Flip(List<Accord.Point> points, int width, int height) {
            var l = new List<Accord.Point>();
            foreach (var point in points) {
                l.Add(new Accord.Point(width - 1 - point.X, height - 1 - point.Y));
            }
            return l;
        }

        public static float[] PercentileClipping(List<CFitsioFITSReader> images, double lowerPercentile, double upperPercentile) {
            if (images.Count == 0) { return []; }

            float[] imageMedian = new float[images.Count];
            for (int i = 0; i < images.Count; i++) {
                var image = images[i];
                imageMedian[i] = image.ReadFloatHeader("MEDIAN");
            }
            float[] normalization = new float[images.Count];
            var reference = imageMedian[0];
            for (int i = 0; i < images.Count; i++) {
                normalization[i] = reference / imageMedian[i];
            }

            var first = images.First();
            var width = first.Width;
            var height = first.Height;

            int totalPixels = width * height;
            int numberOfImages = images.Count;

            float[] master = new float[totalPixels];

            for (int idxRow = 0; idxRow < height; idxRow++) {
                List<float[]> pixelRows = new List<float[]>();

                for (int i = 0; i < images.Count; i++) {
                    var pixel = images[i].ReadPixelRowAsFloat(idxRow);
                    pixelRows.Add(pixel);
                }

                for (int idxCol = 0; idxCol < width; idxCol++) {
                    var pixelIndex = idxRow * width + idxCol;

                    float[] pixelValues = new float[numberOfImages];
                    for (int i = 0; i < images.Count; i++) {
                        var pixel = pixelRows[i][idxCol];
                        pixel = pixel * normalization[i];
                        pixelValues[i] = pixel;
                    }

                    var median = pixelValues.Median();

                    if (lowerPercentile == 0.0 && upperPercentile == 0.0) {
                        // Simple average
                        float sum = 0;
                        for (int i = 0; i < pixelValues.Length; i++)
                            sum += pixelValues[i];
                        master[pixelIndex] = sum / pixelValues.Length;
                    } else {
                        float sum = 0;
                        float count = 0;
                        foreach (var pixel in pixelValues) {
                            if (median - (median * lowerPercentile) <= pixel && pixel <= median + (median * upperPercentile)) {
                                sum += pixel;
                                count++;
                            }
                        }
                        if (count == 0) {
                            master[pixelIndex] = median;
                        } else {
                            master[pixelIndex] = sum / count;
                        }
                    }
                }
            }
            return master;
        }

        public static Bitmap DownsampleGray16(Bitmap input, int factor) {
            if (factor <= 0) {
                throw new ArgumentException("Downsampling factor must be greater than 0.", nameof(factor));
            }

            int originalWidth = input.Width;
            int originalHeight = input.Height;

            int newWidth = (originalWidth + factor - 1) / factor; // Round up
            int newHeight = (originalHeight + factor - 1) / factor; // Round up

            Bitmap output = new Bitmap(newWidth, newHeight, System.Drawing.Imaging.PixelFormat.Format16bppGrayScale);

            BitmapData inputData = input.LockBits(new Rectangle(0, 0, originalWidth, originalHeight), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format16bppGrayScale);
            BitmapData outputData = output.LockBits(new Rectangle(0, 0, newWidth, newHeight), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format16bppGrayScale);

            int inputStride = inputData.Stride / 2;  // Stride in 16-bit pixels (not bytes)
            int outputStride = outputData.Stride / 2; // Stride in 16-bit pixels (not bytes)

            unsafe {
                ushort* ptrInput = (ushort*)inputData.Scan0;
                ushort* ptrOutput = (ushort*)outputData.Scan0;

                for (int y = 0; y < newHeight; y++) {
                    for (int x = 0; x < newWidth; x++) {
                        int sum = 0;
                        int count = 0;

                        // Calculate the starting coordinates in the original image
                        int startX = x * factor;
                        int startY = y * factor;

                        // Iterate over the block of pixels in the original image
                        for (int dy = 0; dy < factor; dy++) {
                            int originalY = startY + dy;
                            if (originalY >= originalHeight) break; // Skip out-of-bounds rows

                            for (int dx = 0; dx < factor; dx++) {
                                int originalX = startX + dx;
                                if (originalX >= originalWidth) break; // Skip out-of-bounds columns

                                // Correctly calculate the pixel index using the stride
                                sum += ptrInput[originalY * inputStride + originalX];
                                count++;
                            }
                        }

                        // Compute the average value for the downsampled pixel
                        ptrOutput[y * outputStride + x] = (ushort)(sum / count);
                    }
                }
            }

            input.UnlockBits(inputData);
            output.UnlockBits(outputData);

            return output;
        }

        public static void RemoveHotPixelOutliers(float[] imageData, int width, int height, int neighborSize = 1, double outlierFactor = 10.0) {
            int windowSize = (2 * neighborSize + 1) * (2 * neighborSize + 1) - 1; // Total neighbors excluding the center pixel
            float[] meanBuffer = new float[width * height];
            float[] stdDevBuffer = new float[width * height];

            // Precompute neighborhood statistics
            Parallel.For(0, height, y => {
                for (int x = 0; x < width; x++) {
                    int index = y * width + x;
                    float sum = 0;
                    float sumSquared = 0;
                    int count = 0;

                    for (int dy = -neighborSize; dy <= neighborSize; dy++) {
                        for (int dx = -neighborSize; dx <= neighborSize; dx++) {
                            int nx = x + dx;
                            int ny = y + dy;

                            if ((dx != 0 || dy != 0) && nx >= 0 && nx < width && ny >= 0 && ny < height) {
                                float neighborValue = imageData[ny * width + nx];
                                sum += neighborValue;
                                sumSquared += neighborValue * neighborValue;
                                count++;
                            }
                        }
                    }

                    float mean = sum / count;
                    float variance = sumSquared / count - mean * mean;
                    float stdDev = (float)Math.Sqrt(Math.Max(variance, 0f)); // Avoid negative variance due to floating-point precision

                    meanBuffer[index] = mean;
                    stdDevBuffer[index] = stdDev;
                }
            });

            // Identify and replace outliers
            Parallel.For(0, height, y => {
                for (int x = 0; x < width; x++) {
                    int index = y * width + x;
                    float mean = meanBuffer[index];
                    float stdDev = stdDevBuffer[index];

                    if (Math.Abs(imageData[index] - mean) > outlierFactor * stdDev) {
                        imageData[index] = mean; // Replace with mean
                    }
                }
            });
        }

        public static Bitmap MergeGray16ToRGB48(Bitmap red, Bitmap green, Bitmap blue) {
            // Ensure all input bitmaps are of the same size
            int width = red.Width;
            int height = red.Height;

            // Create a new Bitmap for RGB48 (Format48bppRgb)
            Bitmap rgb48Bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format48bppRgb);

            // Lock the bits for direct access to pixel data
            BitmapData redData = red.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format16bppGrayScale);
            BitmapData greenData = green.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format16bppGrayScale);
            BitmapData blueData = blue.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format16bppGrayScale);
            BitmapData rgb48Data = rgb48Bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format48bppRgb);

            unsafe {
                // Pointers to the image data as ushort*
                ushort* ptrGray1 = (ushort*)redData.Scan0;
                ushort* ptrGray2 = (ushort*)greenData.Scan0;
                ushort* ptrGray3 = (ushort*)blueData.Scan0;
                ushort* ptrRGB = (ushort*)rgb48Data.Scan0;

                // Strides in terms of ushort (divide by 2 since each ushort is 2 bytes)
                int strideGray = redData.Stride / 2;
                int strideRGB = rgb48Data.Stride / 2;

                // Process each pixel row by row
                for (int y = 0; y < height; y++) {
                    ushort* rowGray1 = ptrGray1 + y * strideGray;
                    ushort* rowGray2 = ptrGray2 + y * strideGray;
                    ushort* rowGray3 = ptrGray3 + y * strideGray;
                    ushort* rowRGB = ptrRGB + y * strideRGB;

                    for (int x = 0; x < width; x++) {
                        // Assign grayscale values to the RGB channels
                        rowRGB[x * 3] = rowGray1[x];     // Red channel
                        rowRGB[x * 3 + 1] = rowGray2[x]; // Green channel
                        rowRGB[x * 3 + 2] = rowGray3[x]; // Blue channel
                    }
                }
            }

            // Unlock the bits after manipulation
            red.UnlockBits(redData);
            green.UnlockBits(greenData);
            blue.UnlockBits(blueData);
            rgb48Bitmap.UnlockBits(rgb48Data);

            return rgb48Bitmap;
        }

        public static BitmapWithMedian CreateGrayBitmap(float[] data, int width, int height) {
            if (data.Length != width * height)
                throw new ArgumentException("Data length does not match width and height dimensions.");

            // Create a Bitmap with Format16bppGrayScale (assuming the platform supports it)
            Bitmap grayBitmap = new Bitmap(width, height, PixelFormat.Format16bppGrayScale);

            // Lock the bits for direct pixel manipulation
            var rect = new Rectangle(0, 0, width, height);
            BitmapData bitmapData = grayBitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format16bppGrayScale);

            int bytesPerPixel = 2; // 16 bits per pixel (2 bytes per pixel)
            int stride = bitmapData.Stride / bytesPerPixel; // Adjust for row alignment

            int[] pixelValueCounts = new int[ushort.MaxValue + 1];
            // Copy the data row by row to handle potential stride padding
            unsafe {
                ushort* ptr = (ushort*)bitmapData.Scan0;
                for (int y = 0; y < height; y++) {
                    for (int x = 0; x < width; x++) {
                        var pixel = (ushort)(data[y * width + x] * ushort.MaxValue);
                        pixelValueCounts[pixel]++;
                        ptr[y * stride + x] = pixel;
                    }
                }
            }

            // Unlock the bitmap
            grayBitmap.UnlockBits(bitmapData);

            var (median, mad) = CalculateMedianAndMAD(pixelValueCounts, width * height);

            return new(grayBitmap, median, mad);
        }

        public class BitmapWithMedian : IDisposable {

            public BitmapWithMedian(Bitmap bitmap, double median, double medianAbsoluteDeviation) {
                Bitmap = bitmap;
                Median = median;
                MedianAbsoluteDeviation = medianAbsoluteDeviation;
            }

            public Bitmap Bitmap { get; }
            public double Median { get; }
            public double MedianAbsoluteDeviation { get; }

            public void Dispose() {
                Bitmap.Dispose();
            }
        }

        public static (double Median, double MAD) CalculateMedianAndMAD(int[] pixelValueCounts, int originalDataLength) {
            int median1 = 0, median2 = 0;
            var occurrences = 0;
            var medianlength = originalDataLength / 2.0;
            for (ushort i = 0; i < ushort.MaxValue; i++) {
                occurrences += pixelValueCounts[i];
                if (occurrences > medianlength) {
                    median1 = i;
                    median2 = i;
                    break;
                } else if (occurrences == medianlength) {
                    median1 = i;
                    for (int j = i + 1; j <= ushort.MaxValue; j++) {
                        if (pixelValueCounts[j] > 0) {
                            median2 = j;
                            break;
                        }
                    }
                    break;
                }
            }
            var median = (median1 + median2) / 2.0;

            var medianAbsoluteDeviation = 0.0d;
            occurrences = 0;
            var idxDown = median1;
            var idxUp = median2;
            while (true) {
                if (idxDown >= 0 && idxDown != idxUp) {
                    occurrences += pixelValueCounts[idxDown] + pixelValueCounts[idxUp];
                } else {
                    occurrences += pixelValueCounts[idxUp];
                }

                if (occurrences > medianlength) {
                    medianAbsoluteDeviation = Math.Abs(idxUp - median);
                    break;
                }

                idxUp++;
                idxDown--;
                if (idxUp > ushort.MaxValue) {
                    break;
                }
            }

            return (median, medianAbsoluteDeviation);
        }

        public static void ApplyGreenDeNoiseInPlace(Bitmap colorBitmap, double amount) {
            Rectangle rect = new Rectangle(0, 0, colorBitmap.Width, colorBitmap.Height);
            BitmapData bmpData = colorBitmap.LockBits(rect, ImageLockMode.ReadWrite, colorBitmap.PixelFormat);

            int stride = bmpData.Stride;
            unsafe {
                byte* ptr = (byte*)bmpData.Scan0;

                for (int y = 0; y < colorBitmap.Height; y++) {
                    ushort* row = (ushort*)(ptr + y * stride);

                    for (int x = 0; x < colorBitmap.Width; x++) {
                        ushort* pixel = row + x * 3; // 3 channels (ushort each)

                        // Access RGB channels
                        ushort blue = pixel[0];  // Blue channel
                        ushort green = pixel[1]; // Green channel
                        ushort red = pixel[2];   // Red channel

                        // Compute average using bit-shifting
                        ushort m = (ushort)((red + blue) >> 1);

                        // Write back the value
                        pixel[1] = (ushort)(green * (1 - amount) + Math.Min(green, m) * amount);
                    }
                }
            }

            // Unlock the bits.
            colorBitmap.UnlockBits(bmpData);
        }
    }
}