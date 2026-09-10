using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Drawing.Imaging;
using System.Drawing;

namespace NINA.Plugin.Livestack.Image {

    public class ImageMath : IImageMath {
        private const int AbeDefaultBoxSize = 5;
        private const int AbeDefaultBoxSeparation = 5;
        private const double AbeDefaultGlobalDeviation = 0.8d;
        private const double AbeDefaultGlobalUnbalance = 1.8d;
        private const int AbeDefaultFunctionDegree = 4;

        private static readonly Lazy<ImageMath> lazy = new Lazy<ImageMath>(() => new ImageMath());

        public static ImageMath Instance => lazy.Value;

        private ImageMath() {
        }

        public float[] PercentileClipping(List<CFitsioFITSReader> images, double lowerPercentile, double upperPercentile) {
            return PercentileClipping(images, lowerPercentile, upperPercentile, CancellationToken.None);
        }

        public float[] PercentileClipping(List<CFitsioFITSReader> images, double lowerPercentile, double upperPercentile, CancellationToken token) {
            token.ThrowIfCancellationRequested();
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

            float[][] rowBuffers = new float[numberOfImages][];
            float[] pixelValues = ArrayPool<float>.Shared.Rent(numberOfImages);
            float lowerPercentileSingle = (float)lowerPercentile;
            float upperPercentileSingle = (float)upperPercentile;

            try {
                for (int i = 0; i < numberOfImages; i++) {
                    rowBuffers[i] = ArrayPool<float>.Shared.Rent(width);
                }

                for (int idxRow = 0; idxRow < height; idxRow++) {
                    token.ThrowIfCancellationRequested();
                    for (int i = 0; i < numberOfImages; i++) {
                        images[i].ReadPixelRowAsFloat(idxRow, rowBuffers[i]);
                    }

                    int rowOffset = idxRow * width;
                    for (int idxCol = 0; idxCol < width; idxCol++) {
                        for (int i = 0; i < numberOfImages; i++) {
                            pixelValues[i] = rowBuffers[i][idxCol] * normalization[i];
                        }

                        Array.Sort(pixelValues, 0, numberOfImages);
                        float median = GetMedianFromSorted(pixelValues, numberOfImages);
                        float lowerLimit = median - (median * lowerPercentileSingle);
                        float upperLimit = median + (median * upperPercentileSingle);
                        float minAccepted = Math.Min(lowerLimit, upperLimit);
                        float maxAccepted = Math.Max(lowerLimit, upperLimit);

                        float sum = 0f;
                        int count = 0;
                        for (int i = 0; i < numberOfImages; i++) {
                            float pixel = pixelValues[i];
                            if (pixel >= minAccepted && pixel <= maxAccepted) {
                                sum += pixel;
                                count++;
                            }
                        }

                        master[rowOffset + idxCol] = count == 0 ? median : sum / count;
                    }
                }
            } finally {
                for (int i = 0; i < rowBuffers.Length; i++) {
                    if (rowBuffers[i] != null) {
                        ArrayPool<float>.Shared.Return(rowBuffers[i]);
                    }
                }

                ArrayPool<float>.Shared.Return(pixelValues);
            }
            return master;
        }

        private static float GetMedianFromSorted(float[] values, int length) {
            int middle = length / 2;
            if ((length & 1) == 1) {
                return values[middle];
            }

            return (values[middle - 1] + values[middle]) * 0.5f;
        }

        public Bitmap DownsampleGray16(Bitmap input, int factor) {
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

        public void RemoveHotPixelOutliers(float[] imageData, int width, int height, int neighborSize = 1, double outlierFactor = 10.0) {
            ArgumentNullException.ThrowIfNull(imageData);
            ArgumentOutOfRangeException.ThrowIfNegative(neighborSize);
            if (imageData.Length != AffineResampler.GetLength(width, height)) {
                throw new ArgumentException("Pixels must match the frame dimensions.", nameof(imageData));
            }
            using ImageBufferLease corrected = ImageBufferPool.Shared.Rent(imageData.Length);
            float[] output = corrected.Buffer;

            // Read only the original frame until every neighborhood has been evaluated.
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

                    output[index] = Math.Abs(imageData[index] - mean) > outlierFactor * stdDev ? mean : imageData[index];
                }
            });
            output.CopyTo(imageData, 0);
        }

        public Bitmap MergeGray16ToRGB48(Bitmap red, Bitmap green, Bitmap blue) {
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

        public BitmapWithMedian CreateGrayBitmap(float[] data, int width, int height) {
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
                        ushort pixel = Extensions.ToUShort(data[y * width + x]);
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

        public (double Median, double MAD) CalculateMedianAndMAD(int[] pixelValueCounts, int originalDataLength) {
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

        public float[] CreateBackgroundExtractedPreview(float[] data, int width, int height, double amount) {
            float[] output = GC.AllocateUninitializedArray<float>(AffineResampler.GetLength(width, height));
            CreateBackgroundExtractedPreviewInto(data, output, width, height, amount);
            return output;
        }

        public void CreateBackgroundExtractedPreviewInto(float[] data, float[] output, int width, int height, double amount) {
            if (width <= 0) {
                throw new ArgumentOutOfRangeException(nameof(width), "Width must be greater than zero.");
            }
            if (height <= 0) {
                throw new ArgumentOutOfRangeException(nameof(height), "Height must be greater than zero.");
            }
            if (data.Length != checked(width * height) || output.Length != data.Length) {
                throw new ArgumentException("Data length does not match width and height dimensions.", nameof(data));
            }

            float strength = (float)Math.Clamp(amount, 0d, 1d);
            if (strength <= 0f) {
                Array.Copy(data, output, data.Length);
                return;
            }

            List<BackgroundSample> samples = GenerateAutomaticBackgroundSamples(data, width, height);
            int degree = GetBestPolynomialDegree(samples.Count);
            if (degree == 0 || !TryFitAbePolynomial(samples, degree, out double[] coefficients, out float globalBackground)) {
                Array.Copy(data, output, data.Length);
                return;
            }

            ApplyPolynomialBackgroundCorrection(data, output, width, height, coefficients, degree, globalBackground, strength);

        }

        private readonly struct BackgroundSample {

            public BackgroundSample(double x, double y, double value) {
                X = x;
                Y = y;
                Value = value;
            }

            public double X { get; }
            public double Y { get; }
            public double Value { get; }
        }

        private static List<BackgroundSample> GenerateAutomaticBackgroundSamples(float[] data, int width, int height) {
            int boxSize = Math.Min(AbeDefaultBoxSize, Math.Min(width, height));
            int samplePitch = Math.Max(1, AbeDefaultBoxSize + AbeDefaultBoxSeparation);
            int columns = Math.Max(1, 1 + Math.Max(0, width - boxSize) / samplePitch);
            int rows = Math.Max(1, 1 + Math.Max(0, height - boxSize) / samplePitch);
            BackgroundSample[] candidates = new BackgroundSample[columns * rows];
            bool[] valid = new bool[candidates.Length];

            Parallel.For(0, rows, row => {
                int startY = Math.Min(row * samplePitch, Math.Max(0, height - boxSize));
                int endY = Math.Min(height, startY + boxSize);

                for (int column = 0; column < columns; column++) {
                    int startX = Math.Min(column * samplePitch, Math.Max(0, width - boxSize));
                    int endX = Math.Min(width, startX + boxSize);
                    int index = (row * columns) + column;

                    if (TryEstimateCellBackground(data, width, startX, endX, startY, endY, out float background)) {
                        double x = width <= 1 ? 0d : ((startX + endX - 1d) / (width - 1d)) - 1d;
                        double y = height <= 1 ? 0d : ((startY + endY - 1d) / (height - 1d)) - 1d;
                        candidates[index] = new BackgroundSample(x, y, background);
                        valid[index] = true;
                    }
                }
            });

            List<BackgroundSample> samples = new List<BackgroundSample>(candidates.Length);
            for (int i = 0; i < candidates.Length; i++) {
                if (valid[i]) {
                    samples.Add(candidates[i]);
                }
            }

            return samples;
        }

        private static int GetBestPolynomialDegree(int sampleCount) {
            for (int degree = AbeDefaultFunctionDegree; degree >= 1; degree--) {
                if (sampleCount >= GetPolynomialTermCount(degree)) {
                    return degree;
                }
            }

            return 0;
        }

        private static int GetPolynomialTermCount(int degree) {
            return ((degree + 1) * (degree + 2)) / 2;
        }

        private static bool TryFitAbePolynomial(List<BackgroundSample> samples, int degree, out double[] coefficients, out float globalBackground) {
            coefficients = [];
            globalBackground = 0f;

            if (samples.Count == 0) {
                return false;
            }

            double[] values = ArrayPool<double>.Shared.Rent(samples.Count);
            try {
                for (int i = 0; i < samples.Count; i++) {
                    values[i] = samples[i].Value;
                }

                double median = GetMedian(values, samples.Count);
                double mad = GetMedianAbsoluteDeviation(values, samples.Count, median);
                double sigma = mad * 1.4826d;
                globalBackground = (float)Math.Clamp(median, 0d, 1d);

                bool[] included = new bool[samples.Count];
                int includedCount = 0;
                double lowerLimit = sigma > 0.0000001d ? median - (AbeDefaultGlobalDeviation * AbeDefaultGlobalUnbalance * sigma) : double.NegativeInfinity;
                double upperLimit = sigma > 0.0000001d ? median + (AbeDefaultGlobalDeviation * sigma) : double.PositiveInfinity;
                for (int i = 0; i < samples.Count; i++) {
                    bool keep = samples[i].Value >= lowerLimit && samples[i].Value <= upperLimit;
                    included[i] = keep;
                    if (keep) {
                        includedCount++;
                    }
                }

                int termCount = GetPolynomialTermCount(degree);
                if (includedCount < termCount) {
                    Array.Fill(included, true);
                }

                return TryFitPolynomial(samples, included, degree, out coefficients);
            } finally {
                ArrayPool<double>.Shared.Return(values);
            }
        }

        private static bool TryFitPolynomial(List<BackgroundSample> samples, bool[] included, int degree, out double[] coefficients) {
            int termCount = GetPolynomialTermCount(degree);
            double[,] matrix = new double[termCount, termCount];
            double[] rhs = new double[termCount];
            double[] terms = new double[termCount];
            int includedCount = 0;

            for (int i = 0; i < samples.Count; i++) {
                if (!included[i]) {
                    continue;
                }

                includedCount++;
                FillPolynomialTerms(samples[i].X, samples[i].Y, degree, terms);
                for (int row = 0; row < termCount; row++) {
                    rhs[row] += terms[row] * samples[i].Value;
                    for (int column = 0; column < termCount; column++) {
                        matrix[row, column] += terms[row] * terms[column];
                    }
                }
            }

            if (includedCount < termCount) {
                coefficients = [];
                return false;
            }

            for (int i = 0; i < termCount; i++) {
                matrix[i, i] += 0.0000000001d;
            }

            return TrySolveLinearSystem(matrix, rhs, out coefficients);
        }

        private static bool TrySolveLinearSystem(double[,] matrix, double[] rhs, out double[] solution) {
            int size = rhs.Length;
            double[,] a = (double[,])matrix.Clone();
            double[] b = (double[])rhs.Clone();

            for (int pivot = 0; pivot < size; pivot++) {
                int bestRow = pivot;
                double bestValue = Math.Abs(a[pivot, pivot]);
                for (int row = pivot + 1; row < size; row++) {
                    double value = Math.Abs(a[row, pivot]);
                    if (value > bestValue) {
                        bestRow = row;
                        bestValue = value;
                    }
                }

                if (bestValue < 0.000000000001d) {
                    solution = [];
                    return false;
                }

                if (bestRow != pivot) {
                    for (int column = pivot; column < size; column++) {
                        (a[pivot, column], a[bestRow, column]) = (a[bestRow, column], a[pivot, column]);
                    }

                    (b[pivot], b[bestRow]) = (b[bestRow], b[pivot]);
                }

                double pivotValue = a[pivot, pivot];
                for (int column = pivot; column < size; column++) {
                    a[pivot, column] /= pivotValue;
                }
                b[pivot] /= pivotValue;

                for (int row = 0; row < size; row++) {
                    if (row == pivot) {
                        continue;
                    }

                    double factor = a[row, pivot];
                    if (factor == 0d) {
                        continue;
                    }

                    for (int column = pivot; column < size; column++) {
                        a[row, column] -= factor * a[pivot, column];
                    }
                    b[row] -= factor * b[pivot];
                }
            }

            solution = b;
            return true;
        }

        private static void FillPolynomialTerms(double x, double y, int degree, double[] terms) {
            int index = 0;
            terms[index++] = 1d;

            if (degree < 1) {
                return;
            }

            terms[index++] = x;
            terms[index++] = y;

            if (degree < 2) {
                return;
            }

            double x2 = x * x;
            double y2 = y * y;
            terms[index++] = x2;
            terms[index++] = x * y;
            terms[index++] = y2;

            if (degree < 3) {
                return;
            }

            double x3 = x2 * x;
            double y3 = y2 * y;
            terms[index++] = x3;
            terms[index++] = x2 * y;
            terms[index++] = x * y2;
            terms[index++] = y3;

            if (degree < 4) {
                return;
            }

            terms[index++] = x3 * x;
            terms[index++] = x3 * y;
            terms[index++] = x2 * y2;
            terms[index++] = x * y3;
            terms[index] = y3 * y;
        }

        private static double EvaluatePolynomial(double[] coefficients, int degree, double x, double y) {
            int index = 0;
            double model = coefficients[index++];

            if (degree >= 1) {
                model += (coefficients[index++] * x) + (coefficients[index++] * y);
            }

            if (degree >= 2) {
                double x2 = x * x;
                double y2 = y * y;
                model += (coefficients[index++] * x2) + (coefficients[index++] * x * y) + (coefficients[index++] * y2);

                if (degree >= 3) {
                    double x3 = x2 * x;
                    double y3 = y2 * y;
                    model += (coefficients[index++] * x3) + (coefficients[index++] * x2 * y) + (coefficients[index++] * x * y2) + (coefficients[index++] * y3);

                    if (degree >= 4) {
                        model += (coefficients[index++] * x3 * x) + (coefficients[index++] * x3 * y) + (coefficients[index++] * x2 * y2) + (coefficients[index++] * x * y3) + (coefficients[index] * y3 * y);
                    }
                }
            }

            return model;
        }

        private static void ApplyPolynomialBackgroundCorrection(float[] data, float[] output, int width, int height, double[] coefficients, int degree, float globalBackground, float strength) {
            float[] simdOffsets = CreateSimdOffsets();
            float xStep = width > 1 ? 2f / (width - 1) : 0f;
            float yStep = height > 1 ? 2f / (height - 1) : 0f;

            Parallel.For(0, height, y => {
                float yNormalized = height > 1 ? -1f + (y * yStep) : 0f;
                int rowOffset = y * width;
                int x = 0;

                if (System.Numerics.Vector.IsHardwareAccelerated) {
                    ApplyPolynomialBackgroundCorrectionVectorized(data, output, rowOffset, width, yNormalized, xStep, coefficients, degree, globalBackground, strength, simdOffsets, ref x);
                }

                for (; x < width; x++) {
                    float xNormalized = width > 1 ? -1f + (x * xStep) : 0f;
                    float model = (float)EvaluatePolynomial(coefficients, degree, xNormalized, yNormalized);
                    float value = data[rowOffset + x] - ((model - globalBackground) * strength);

                    if (float.IsNaN(value) || float.IsInfinity(value)) {
                        value = 0f;
                    }

                    output[rowOffset + x] = Math.Clamp(value, 0f, 1f);
                }
            });
        }

        private static void ApplyPolynomialBackgroundCorrectionVectorized(float[] data, float[] output, int rowOffset, int width, float yNormalized, float xStep, double[] coefficients, int degree, float globalBackground, float strength, float[] simdOffsets, ref int x) {
            int vectorEnd = width - (width % System.Numerics.Vector<float>.Count);
            var offsets = new System.Numerics.Vector<float>(simdOffsets);
            var xStepVector = new System.Numerics.Vector<float>(xStep);
            var y = new System.Numerics.Vector<float>(yNormalized);
            var global = new System.Numerics.Vector<float>(globalBackground);
            var strengthVector = new System.Numerics.Vector<float>(strength);
            var zero = System.Numerics.Vector<float>.Zero;
            var one = new System.Numerics.Vector<float>(1f);

            for (; x < vectorEnd; x += System.Numerics.Vector<float>.Count) {
                var xNormalized = new System.Numerics.Vector<float>(-1f + (x * xStep)) + (offsets * xStepVector);
                var model = EvaluatePolynomialVector(coefficients, degree, xNormalized, y);
                var value = new System.Numerics.Vector<float>(data, rowOffset + x) - ((model - global) * strengthVector);
                value = System.Numerics.Vector.Min(System.Numerics.Vector.Max(value, zero), one);
                value.CopyTo(output, rowOffset + x);
            }
        }

        private static System.Numerics.Vector<float> EvaluatePolynomialVector(double[] coefficients, int degree, System.Numerics.Vector<float> x, System.Numerics.Vector<float> y) {
            int index = 0;
            var model = new System.Numerics.Vector<float>((float)coefficients[index++]);

            if (degree >= 1) {
                model += (new System.Numerics.Vector<float>((float)coefficients[index++]) * x)
                    + (new System.Numerics.Vector<float>((float)coefficients[index++]) * y);
            }

            if (degree >= 2) {
                var x2 = x * x;
                var y2 = y * y;
                model += (new System.Numerics.Vector<float>((float)coefficients[index++]) * x2)
                    + (new System.Numerics.Vector<float>((float)coefficients[index++]) * x * y)
                    + (new System.Numerics.Vector<float>((float)coefficients[index++]) * y2);

                if (degree >= 3) {
                    var x3 = x2 * x;
                    var y3 = y2 * y;
                    model += (new System.Numerics.Vector<float>((float)coefficients[index++]) * x3)
                        + (new System.Numerics.Vector<float>((float)coefficients[index++]) * x2 * y)
                        + (new System.Numerics.Vector<float>((float)coefficients[index++]) * x * y2)
                        + (new System.Numerics.Vector<float>((float)coefficients[index++]) * y3);

                    if (degree >= 4) {
                        model += (new System.Numerics.Vector<float>((float)coefficients[index++]) * x3 * x)
                            + (new System.Numerics.Vector<float>((float)coefficients[index++]) * x3 * y)
                            + (new System.Numerics.Vector<float>((float)coefficients[index++]) * x2 * y2)
                            + (new System.Numerics.Vector<float>((float)coefficients[index++]) * x * y3)
                            + (new System.Numerics.Vector<float>((float)coefficients[index]) * y3 * y);
                    }
                }
            }

            return model;
        }

        private static float[] CreateSimdOffsets() {
            float[] offsets = new float[System.Numerics.Vector<float>.Count];
            for (int i = 0; i < offsets.Length; i++) {
                offsets[i] = i;
            }

            return offsets;
        }

        private static double GetMedian(double[] values, int count) {
            Array.Sort(values, 0, count);
            int middle = count / 2;
            if ((count & 1) == 1) {
                return values[middle];
            }

            return (values[middle - 1] + values[middle]) * 0.5d;
        }

        private static double GetMedianAbsoluteDeviation(double[] values, int count, double median) {
            for (int i = 0; i < count; i++) {
                values[i] = Math.Abs(values[i] - median);
            }

            return GetMedian(values, count);
        }

        private static bool TryEstimateCellBackground(float[] data, int width, int startX, int endX, int startY, int endY, out float background) {
            int cellWidth = endX - startX;
            int cellHeight = endY - startY;
            int cellPixels = cellWidth * cellHeight;
            if (cellPixels <= 0) {
                background = 0f;
                return false;
            }

            const int maxSamples = 256;
            int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(cellPixels / (double)maxSamples)));
            int estimatedSamples = ((cellWidth + step - 1) / step) * ((cellHeight + step - 1) / step);
            float[] samples = ArrayPool<float>.Shared.Rent(estimatedSamples);
            int count = 0;
            int minimumSampleCount = Math.Min(8, cellPixels);

            try {
                for (int y = startY; y < endY; y += step) {
                    int rowOffset = y * width;
                    for (int x = startX; x < endX; x += step) {
                        float value = data[rowOffset + x];
                        if (!float.IsNaN(value) && !float.IsInfinity(value)) {
                            samples[count] = value;
                            count++;
                        }
                    }
                }

                if (count < minimumSampleCount) {
                    background = 0f;
                    return false;
                }

                Array.Sort(samples, 0, count);

                int lower = 0;
                int upper = count;
                for (int iteration = 0; iteration < 5; iteration++) {
                    CalculateSortedRangeStatistics(samples, lower, upper, out double mean, out double median, out double sigma);
                    if (sigma <= 0d) {
                        break;
                    }

                    int nextLower = LowerBound(samples, lower, upper, median - (3d * sigma));
                    int nextUpper = UpperBound(samples, lower, upper, median + (3d * sigma));
                    if (nextUpper - nextLower < minimumSampleCount || (nextLower == lower && nextUpper == upper)) {
                        break;
                    }

                    lower = nextLower;
                    upper = nextUpper;
                }

                CalculateSortedRangeStatistics(samples, lower, upper, out double clippedMean, out double clippedMedian, out double clippedSigma);
                double mode = (2.5d * clippedMedian) - (1.5d * clippedMean);
                if (clippedSigma > 0d && ((clippedMean - clippedMedian) / clippedSigma) > 0.3d) {
                    mode = clippedMedian;
                }

                background = Math.Clamp((float)mode, 0f, 1f);
                return true;
            } finally {
                ArrayPool<float>.Shared.Return(samples);
            }
        }

        private static void CalculateSortedRangeStatistics(float[] sortedValues, int lower, int upper, out double mean, out double median, out double sigma) {
            int count = upper - lower;
            if (count <= 0) {
                mean = 0d;
                median = 0d;
                sigma = 0d;
                return;
            }

            double sum = 0d;
            double sumSquares = 0d;
            for (int i = lower; i < upper; i++) {
                double value = sortedValues[i];
                sum += value;
                sumSquares += value * value;
            }

            mean = sum / count;
            int middle = lower + (count / 2);
            median = (count & 1) == 1 ? sortedValues[middle] : (sortedValues[middle - 1] + sortedValues[middle]) * 0.5d;
            double variance = (sumSquares / count) - (mean * mean);
            sigma = Math.Sqrt(Math.Max(variance, 0d));
        }

        private static int LowerBound(float[] sortedValues, int lower, int upper, double value) {
            int left = lower;
            int right = upper;
            while (left < right) {
                int middle = left + ((right - left) / 2);
                if (sortedValues[middle] < value) {
                    left = middle + 1;
                } else {
                    right = middle;
                }
            }

            return left;
        }

        private static int UpperBound(float[] sortedValues, int lower, int upper, double value) {
            int left = lower;
            int right = upper;
            while (left < right) {
                int middle = left + ((right - left) / 2);
                if (sortedValues[middle] <= value) {
                    left = middle + 1;
                } else {
                    right = middle;
                }
            }

            return left;
        }

        public void ApplyGreenDeNoiseInPlace(Bitmap colorBitmap, double amount) {
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
