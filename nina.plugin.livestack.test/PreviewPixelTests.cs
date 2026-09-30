using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.Image;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace nina.plugin.livestack.test {
    public class PreviewPixelTests {
        [TestCase(1, 1)]
        [TestCase(1, 5)]
        [TestCase(5, 1)]
        [TestCase(2, 2)]
        [TestCase(6, 4)]
        [TestCase(7, 5)]
        [TestCase(257, 129)]
        public void DownsamplingKeepsExactBlockAveragesIncludingPartialEdges(int width, int height) {
            using Bitmap source = new(width, height, PixelFormat.Format16bppGrayScale);
            ushort[] pixels = Enumerable.Range(0, width * height).Select(i => (ushort)((i * 7919L) % 65536)).ToArray();
            pixels[0] = ushort.MaxValue;
            BitmapData input = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format16bppGrayScale);
            try {
                for (int y = 0; y < height; y++) {
                    for (int x = 0; x < width; x++) {
                        Marshal.WriteInt16(input.Scan0 + y * input.Stride + x * 2, unchecked((short)pixels[y * width + x]));
                    }
                }
            } finally {
                source.UnlockBits(input);
            }
            foreach (int factor in new[] { 1, 2, 3, 4 }) {
                using Bitmap result = ImageMath.Instance.DownsampleGray16(source, factor);
                Assert.That(result.Width, Is.EqualTo((width + factor - 1) / factor));
                Assert.That(result.Height, Is.EqualTo((height + factor - 1) / factor));
                BitmapData output = result.LockBits(new Rectangle(0, 0, result.Width, result.Height), ImageLockMode.ReadOnly, PixelFormat.Format16bppGrayScale);
                try {
                    for (int y = 0; y < result.Height; y++) {
                        for (int x = 0; x < result.Width; x++) {
                            IEnumerable<int> samples = from yy in Enumerable.Range(y * factor, Math.Min(factor, height - y * factor))
                                                       from xx in Enumerable.Range(x * factor, Math.Min(factor, width - x * factor))
                                                       select (int)pixels[yy * width + xx];
                            ushort expected = (ushort)(samples.Sum() / samples.Count());
                            ushort actual = unchecked((ushort)Marshal.ReadInt16(output.Scan0 + y * output.Stride + x * 2));
                            Assert.That(actual, Is.EqualTo(expected), $"Factor {factor}, pixel ({x}, {y})");
                        }
                    }
                } finally {
                    result.UnlockBits(output);
                }
            }
        }

        [Test]
        public void SaturationPreservesEveryUshortLevelAndAdjacentFloatBoundaries() {
            for (int level = 0; level <= ushort.MaxValue; level++) {
                float normalized = level / (float)ushort.MaxValue;
                foreach (float value in new[] { normalized, MathF.BitDecrement(normalized), MathF.BitIncrement(normalized) }) {
                    ushort expected = (ushort)Math.Clamp(value * ushort.MaxValue, 0, ushort.MaxValue);
                    Assert.That(Extensions.ToUShort(value), Is.EqualTo(expected), $"Level {level}, value {value:R}");
                }
            }
        }

        [TestCase(0f, 0)]
        [TestCase(0.5f, 32767)]
        [TestCase(1f, 65535)]
        [TestCase(1.1f, 65535)]
        [TestCase(-0.1f, 0)]
        [TestCase(float.PositiveInfinity, 65535)]
        [TestCase(float.NegativeInfinity, 0)]
        [TestCase(float.NaN, 0)]
        public void BitmapAndOscConversionSaturateWithoutChangingSource(float value, int expected) {
            float[] pixels = { value };
            using ImageMath.BitmapWithMedian preview = ImageMath.Instance.CreateGrayBitmap(pixels, 1, 1);
            BitmapData data = preview.Bitmap.LockBits(new Rectangle(0, 0, 1, 1), ImageLockMode.ReadOnly, PixelFormat.Format16bppGrayScale);
            try {
                Assert.That(unchecked((ushort)Marshal.ReadInt16(data.Scan0)), Is.EqualTo(expected));
            } finally {
                preview.Bitmap.UnlockBits(data);
            }
            Assert.That(pixels.ToUShortArray()[0], Is.EqualTo(expected));
            Assert.That(pixels[0], Is.EqualTo(value));
        }
    }
}
