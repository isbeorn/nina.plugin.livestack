using NINA.Core.Enum;
using NINA.Image.ImageData;
using System;
using System.Threading.Tasks;

namespace NINA.Plugin.Livestack.Image {
    /// <summary>Extracts OSC channels without constructing an unused RGB display bitmap.</summary>
    internal static class BayerChannelExtractor {
        internal static bool TryExtract(float[] source, int width, int height, SensorType pattern, out LRGBArrays channels) {
            channels = null;
            // Keep NINA's bitmap path for other layouts, including padded odd-width rows.
            int redPhase = pattern switch {
                SensorType.RGGB => 0,
                SensorType.GRBG => 1,
                SensorType.GBRG => 2,
                SensorType.BGGR => 3,
                _ => -1
            };
            if (redPhase < 0 || width < 2 || height < 2 || (width & 1) != 0) {
                return false;
            }
            ArgumentNullException.ThrowIfNull(source);
            int length = checked(width * height);
            if (source.Length != length) {
                throw new ArgumentException("Source length must match the frame dimensions.", nameof(source));
            }

            ushort[] red = GC.AllocateUninitializedArray<ushort>(length);
            ushort[] green = GC.AllocateUninitializedArray<ushort>(length);
            ushort[] blue = GC.AllocateUninitializedArray<ushort>(length);
            void ProcessRow(int y) {
                int row = y * width;
                for (int x = 0; x < width; x++) {
                    int index = row + x;
                    if (x == 0 || x == width - 1 || y == 0 || y == height - 1) {
                        ExtractBorder(source, width, height, x, y, redPhase, out red[index], out green[index], out blue[index]);
                        continue;
                    }

                    // Quantize each sensor sample before averaging, exactly as the bitmap path does.
                    int center = Extensions.ToUShort(source[index]);
                    int horizontal = Extensions.ToUShort(source[index - 1]) + Extensions.ToUShort(source[index + 1]);
                    int vertical = Extensions.ToUShort(source[index - width]) + Extensions.ToUShort(source[index + width]);
                    int diagonal = Extensions.ToUShort(source[index - width - 1]) + Extensions.ToUShort(source[index - width + 1])
                        + Extensions.ToUShort(source[index + width - 1]) + Extensions.ToUShort(source[index + width + 1]);
                    int phase = (x & 1) | ((y & 1) << 1);
                    if (phase == redPhase) {
                        red[index] = (ushort)center;
                        green[index] = (ushort)((horizontal + vertical) / 4);
                        blue[index] = (ushort)(diagonal / 4);
                    } else if (phase == (redPhase ^ 3)) {
                        red[index] = (ushort)(diagonal / 4);
                        green[index] = (ushort)((horizontal + vertical) / 4);
                        blue[index] = (ushort)center;
                    } else {
                        bool redHorizontal = (y & 1) == (redPhase >> 1);
                        red[index] = (ushort)((redHorizontal ? horizontal : vertical) / 2);
                        blue[index] = (ushort)((redHorizontal ? vertical : horizontal) / 2);
                        // NINA averages all five green sites in the 3x3 neighborhood, including the center.
                        green[index] = (ushort)((center + diagonal) / 5);
                    }
                }
            }
            if (length < 262144) {
                for (int y = 0; y < height; y++) {
                    ProcessRow(y);
                }
            } else {
                Parallel.For(0, height, ProcessRow);
            }
            channels = new LRGBArrays(Array.Empty<ushort>(), red, green, blue);
            return true;
        }

        private static void ExtractBorder(float[] source, int width, int height, int x, int y, int redPhase,
                out ushort red, out ushort green, out ushort blue) {
            int redSum = 0, greenSum = 0, blueSum = 0;
            int redCount = 0, greenCount = 0, blueCount = 0;
            for (int yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1); yy++) {
                for (int xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++) {
                    int value = Extensions.ToUShort(source[yy * width + xx]);
                    int phase = (xx & 1) | ((yy & 1) << 1);
                    if (phase == redPhase) {
                        redSum += value;
                        redCount++;
                    } else if (phase == (redPhase ^ 3)) {
                        blueSum += value;
                        blueCount++;
                    } else {
                        greenSum += value;
                        greenCount++;
                    }
                }
            }
            red = (ushort)(redSum / redCount);
            green = (ushort)(greenSum / greenCount);
            blue = (ushort)(blueSum / blueCount);
        }
    }
}
