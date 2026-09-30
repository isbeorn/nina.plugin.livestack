using NINA.Core.Enum;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.Image;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace nina.plugin.livestack.test {
    [Apartment(ApartmentState.STA)]
    public class BayerChannelExtractorTests {
        [Test]
        public void ChannelsMatchNinaIncludingBordersAndQuantization(
                [Values(SensorType.RGGB, SensorType.BGGR, SensorType.GRBG, SensorType.GBRG)] SensorType pattern,
                [Values(2, 4, 16, 258, 514)] int width,
                [Values(2, 3, 17, 515)] int height) {
            Random random = new(713);
            float[] source = Enumerable.Range(0, width * height).Select(_ => (float)(random.NextDouble() * 1.4 - 0.2)).ToArray();
            float[] boundaries = { 0f, 1f, float.NaN, float.NegativeInfinity, float.PositiveInfinity,
                MathF.BitDecrement(0.5f), 0.5f, MathF.BitIncrement(0.5f), -0.001f, 1.001f };
            for (int i = 0; i < source.Length; i += 7) {
                source[i] = boundaries[(i / 7) % boundaries.Length];
            }
            float[] original = (float[])source.Clone();
            ushort[] pixels = source.ToUShortArray();
            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray16, null, pixels, width * 2);
            bitmap.Freeze();
            LRGBArrays expected = ImageUtility.Debayer(bitmap, System.Drawing.Imaging.PixelFormat.Format16bppGrayScale, true, false, pattern).Data;

            Assert.That(BayerChannelExtractor.TryExtract(source, width, height, pattern, out LRGBArrays actual), Is.True);
            Assert.Multiple(() => {
                Assert.That(actual.Red, Is.EqualTo(expected.Red), "Red");
                Assert.That(actual.Green, Is.EqualTo(expected.Green), "Green");
                Assert.That(actual.Blue, Is.EqualTo(expected.Blue), "Blue");
                Assert.That(actual.Lum, Is.Empty);
                Assert.That(source, Is.EqualTo(original), "Source must remain unchanged");
            });
        }

        [TestCase(1, 4, SensorType.RGGB)]
        [TestCase(4, 1, SensorType.RGGB)]
        [TestCase(5, 4, SensorType.RGGB)]
        [TestCase(4, 4, SensorType.RGBG)]
        [TestCase(4, 4, SensorType.GRGB)]
        [TestCase(4, 4, SensorType.GBGR)]
        [TestCase(4, 4, SensorType.BGRG)]
        public void OtherLayoutsUseExistingDebayerPath(int width, int height, SensorType pattern) {
            Assert.That(BayerChannelExtractor.TryExtract(new float[width * height], width, height, pattern, out LRGBArrays channels), Is.False);
            Assert.That(channels, Is.Null);
        }
    }
}
