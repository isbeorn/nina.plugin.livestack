using NINA.Image.ImageData;
using NINA.Plugin.Livestack.Image;

namespace nina.plugin.livestack.test {
    public class MemoryEfficiencyTests {
        [TestCase(1)]
        [TestCase(2)]
        public void HotPixelCleanupPreservesNeighborsAndCorrectsCenterAndCornerOutliers(int radius) {
            const int width = 15, height = 11;
            float[] image = Enumerable.Repeat(0.1f, width * height).ToArray();
            image[0] = image[^1] = image[5 * width + 7] = 0.9f;
            ImageMath.Instance.RemoveHotPixelOutliers(image, width, height, radius);
            Assert.That(image, Is.All.EqualTo(0.1f).Within(1e-6));
        }

        [Test]
        public void HotPixelCleanupReusesScratchAfterWarmup() {
            float[] image = Enumerable.Repeat(0.1f, 257 * 129).ToArray();
            ImageMath.Instance.RemoveHotPixelOutliers(image, 257, 129);
            long start = GC.GetTotalAllocatedBytes(true);
            ImageMath.Instance.RemoveHotPixelOutliers(image, 257, 129);
            long allocated = GC.GetTotalAllocatedBytes(true) - start;
            Assert.That(allocated, Is.LessThan(image.Length * sizeof(float)));
        }

        [TestCase(255, false)]
        [TestCase(255, true)]
        [TestCase(65535, false)]
        [TestCase(65535, true)]
        public void CounterPromotionPreservesCountsAndMissingCoverage(int boundary, bool ushortInput) {
            LiveStackBag bag = new("target", "L", new ImageProperties(2, 1, 16, false, 100, 10), new ImageMetaData(), new());
            bag.Add(new[] { 0.2f, 0.2f });
            float[] source = { 0.6f, 0.6f };
            ushort[] source16 = { 39321, 39321 };
            double[,] shifted = AlignmentSafetyTests.Matrix(tx: 1);
            for (int frame = 1; frame < boundary; frame++) {
                if (ushortInput) {
                    bag.AddTransformed(source16, shifted, false);
                } else {
                    bag.AddTransformed(source, shifted, false);
                }
            }
            float[] before = (float[])bag.Stack.Clone();
            Assert.Throws<ArgumentException>(() => bag.AddTransformed(source, AlignmentSafetyTests.Matrix(a: 2), false));
            Assert.That(bag.Stack, Is.EqualTo(before));
            Assert.That(bag.ImageCount, Is.EqualTo(boundary));
            bag.Add(new[] { 0.8f, 0.8f });
            Assert.That(bag.ImageCount, Is.EqualTo(boundary + 1));
            Assert.That(bag.Stack[1], Is.EqualTo(0.5f).Within(1e-6));
            Assert.That(bag.Stack[0], Is.EqualTo((0.2 + 0.6 * (boundary - 1) + 0.8) / (boundary + 1)).Within(1e-4));

            bag.ForcePushReference(bag.Properties, bag.ReferenceImageStars, new[] { 0.2f, 0.2f });
            bag.Add(new[] { 0.6f, 0.6f });
            Assert.That(bag.ImageCount, Is.EqualTo(2));
            Assert.That(bag.Stack, Is.All.EqualTo(0.4f).Within(1e-6));
        }

        [TestCase(255, sizeof(ushort))]
        [TestCase(65535, sizeof(uint))]
        public void CountStorageGrowsOnlyAtPromotion(int boundary, int bytesPerCount) {
            const int width = 257;
            LiveStackBag bag = new("target", "L", new ImageProperties(width, 1, 16, false, 100, 10), new ImageMetaData(), new());
            bag.Add(new float[width]);
            float[] source = Enumerable.Repeat(0.6f, width).ToArray();
            for (int frame = 1; frame < boundary - 1; frame++) {
                bag.Add(source);
            }
            long ordinary = MeasureAddition();
            long promotion = MeasureAddition();
            long afterPromotion = MeasureAddition();

            Assert.That(promotion - ordinary, Is.InRange((long)width * bytesPerCount, (long)width * bytesPerCount + 1024));
            Assert.That(afterPromotion, Is.LessThanOrEqualTo(ordinary + 128), "Subsequent frames must reuse the promoted count buffer.");

            long MeasureAddition() {
                long start = GC.GetAllocatedBytesForCurrentThread();
                bag.Add(source);
                return GC.GetAllocatedBytesForCurrentThread() - start;
            }
        }

        [TestCase(0.0)]
        [TestCase(0.7)]
        [TestCase(1.0)]
        public void BackgroundPreviewCanReuseItsInputWithoutChangingTheResult(double amount) {
            const int width = 127, height = 91;
            float[] original = Enumerable.Range(0, width * height).Select(i => 0.1f + 0.2f * (i % width) / width + (i % 47 == 0 ? 0.3f : 0)).ToArray();
            float[] expected = ImageMath.Instance.CreateBackgroundExtractedPreview(original, width, height, amount);
            float[] inplace = (float[])original.Clone();
            ImageMath.Instance.CreateBackgroundExtractedPreviewInto(inplace, inplace, width, height, amount);
            FloatAssert.AreEqual(expected, inplace, absTol: 0, relTol: 0);
        }

        [Test]
        public void InitialContributionCountsUseAtMostOneBytePerPixel() {
            const int length = 1000000;
            float[] pixels = new float[length];
            LiveStackBag warmup = new("target", "L", new ImageProperties(1, 1, 16, false, 100, 10), new ImageMetaData(), new());
            warmup.Add(new float[1]);
            LiveStackBag bag = new("target", "L", new ImageProperties(1000, 1000, 16, false, 100, 10), new ImageMetaData(), new());
            long before = GC.GetAllocatedBytesForCurrentThread();
            bag.Add(pixels);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.LessThan(length + 4096));
            Assert.That(bag.Stack, Is.SameAs(pixels));
        }
    }
}
