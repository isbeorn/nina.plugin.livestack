using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.Image;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace nina.plugin.livestack.test {
    public class PreviewPixelTests {
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
