using NINA.Plugin.Livestack.Image;
using NINA.Image.ImageData;

namespace nina.plugin.livestack.test {
    public class VectorResamplingTests {
        [TestCase(3, 7, false, false)]
        [TestCase(3, 7, false, true)]
        [TestCase(3, 7, true, false)]
        [TestCase(3, 7, true, true)]
        [TestCase(4, 9, false, false)]
        [TestCase(4, 9, false, true)]
        [TestCase(4, 9, true, false)]
        [TestCase(4, 9, true, true)]
        [TestCase(5, 2, false, false)]
        [TestCase(5, 2, false, true)]
        [TestCase(5, 2, true, false)]
        [TestCase(5, 2, true, true)]
        [TestCase(17, 11, false, false)]
        [TestCase(17, 11, false, true)]
        [TestCase(17, 11, true, false)]
        [TestCase(17, 11, true, true)]
        [TestCase(521, 509, false, false)]
        [TestCase(521, 509, false, true)]
        [TestCase(521, 509, true, false)]
        [TestCase(521, 509, true, true)]
        public void TransformAndWeightedStackMatchScalarBits(int width, int height, bool ushortInput, bool flipped) {
            Random random = new(347);
            ushort[] raw = Enumerable.Range(0, width * height).Select(_ => (ushort)random.Next(65536)).ToArray();
            float[] source = raw.Select(value => value / 65535f).ToArray();
            if (source.Length > 25) {
                source[5] = float.NaN;
                source[9] = float.PositiveInfinity;
                source[13] = float.NegativeInfinity;
                source[17] = float.MaxValue;
                source[21] = -float.MaxValue;
            }
            foreach (double angle in new[] { 0d, 0.013, -0.013, Math.PI }) {
                foreach (double shift in new[] { 0d, -0.75, 0.75, -1e-8, 1e-8 }) {
                    double a = Math.Cos(angle), b = -Math.Sin(angle);
                    double[,] matrix = AlignmentSafetyTests.Matrix(a, b, shift + width / 2d - a * width / 2d - b * height / 2d,
                        -b, a, -shift + height / 2d + b * width / 2d - a * height / 2d);
                    float[] initial = Enumerable.Range(0, source.Length).Select(i => i % 4 == 0 ? float.NaN : source[i]).ToArray();
                    uint[] counts = Enumerable.Range(0, source.Length).Select(i => i % 4 == 0 ? 0u : i % 4 == 1 ? 255u : i % 4 == 2 ? 65535u : uint.MaxValue - 1).ToArray();
                    float[] expectedStack = (float[])initial.Clone();
                    uint[] expectedCounts = (uint[])counts.Clone();
                    float[] expected = new float[source.Length];
                    for (int y = 0; y < height; y++) {
                        for (int x = 0; x < width; x++) {
                            int i = y * width + x;
                            double sx = matrix[0, 0] * x + matrix[0, 1] * y + matrix[0, 2];
                            double sy = matrix[1, 0] * x + matrix[1, 1] * y + matrix[1, 2];
                            if (flipped) {
                                sx = width - 1 - sx;
                                sy = height - 1 - sy;
                            }
                            if (sx < -1e-7 || sx > width - 1 + 1e-7 || sy < -1e-7 || sy > height - 1 + 1e-7) { continue; }
                            sx = Math.Clamp(sx, 0, width - 1);
                            sy = Math.Clamp(sy, 0, height - 1);
                            int left = (int)Math.Floor(sx), top = (int)Math.Floor(sy);
                            int right = Math.Min(left + 1, width - 1), bottom = Math.Min(top + 1, height - 1);
                            double Pixel(int xx, int yy) => ushortInput ? raw[yy * width + xx] : source[yy * width + xx];
                            double upper = Lerp(Pixel(left, top), Pixel(right, top), sx - left);
                            double lower = Lerp(Pixel(left, bottom), Pixel(right, bottom), sx - left);
                            double sampled = Lerp(upper, lower, sy - top);
                            float value = (float)(ushortInput ? sampled / ushort.MaxValue : sampled);
                            if (!float.IsFinite(value)) { continue; }
                            expected[i] = value;
                            uint count = counts[i];
                            expectedStack[i] = count == 0 ? value : (float)(initial[i] + ((double)value - initial[i]) / (count + 1d));
                            expectedCounts[i] = count + 1;
                        }
                    }
                    float[] transformed = ushortInput
                        ? ImageTransformer2.Instance.ApplyAffineTransformation(raw, width, height, matrix, flipped)
                        : ImageTransformer2.Instance.ApplyAffineTransformation(source, width, height, matrix, flipped);
                    if (ushortInput) {
                        ImageTransformer2.Instance.ApplyAffineTransformationAndStack(raw, initial, counts, width, height, matrix, flipped);
                    } else {
                        ImageTransformer2.Instance.ApplyAffineTransformationAndStack(source, initial, counts, width, height, matrix, flipped);
                    }
                    AssertBits(expected, transformed);
                    AssertBits(expectedStack, initial);
                    Assert.That(counts, Is.EqualTo(expectedCounts));
                }
            }
        }

        [TestCase(1)]
        [TestCase(255)]
        [TestCase(65535)]
        [TestCase(int.MaxValue)]
        public void UniformWeightsMatchScalarBits(int count) {
            const int width = 13, height = 5;
            float[] source = Enumerable.Range(0, width * height).Select(i => i / 65f).ToArray();
            source[7] = float.NaN;
            float[] stack = Enumerable.Repeat(0.7f, source.Length).ToArray();
            float[] expected = source.Select(value => float.IsFinite(value) ? (float)(0.7f + ((double)value - 0.7f) / (count + 1d)) : 0.7f).ToArray();
            ImageTransformer2.Instance.ApplyAffineTransformationAndStack(source, stack, count, width, height, AlignmentSafetyTests.Matrix());
            AssertBits(expected, stack);
        }

        [TestCase(255, false)]
        [TestCase(255, true)]
        [TestCase(65535, false)]
        [TestCase(65535, true)]
        public void VectorBlocksKeepExactWeightsThroughPromotionAndReset(int boundary, bool ushortInput) {
            const int width = 9, height = 3;
            LiveStackBag bag = new("target", "L", new ImageProperties(width, height, 16, false, 100, 10), new ImageMetaData(), new());
            bag.Add(Enumerable.Repeat(0.2f, width * height).ToArray());
            float[] source = Enumerable.Repeat(0.6f, width * height).ToArray();
            ushort[] raw = Enumerable.Repeat((ushort)39321, width * height).ToArray();
            float expected = 0.2f;
            double[,] shifted = AlignmentSafetyTests.Matrix(tx: 1);
            for (int count = 1; count <= boundary; count++) {
                if (ushortInput) {
                    bag.AddTransformed(raw, shifted, false);
                } else {
                    bag.AddTransformed(source, shifted, false);
                }
                expected = (float)(expected + ((double)0.6f - expected) / (count + 1d));
                if (count >= boundary - 1) {
                    Assert.That(bag.ImageCount, Is.EqualTo(count + 1));
                    AssertBits(Enumerable.Range(0, width * height).Select(i => i % width == width - 1 ? 0.2f : expected).ToArray(), bag.Stack);
                }
            }
            bag.ForcePushReference(bag.Properties, bag.ReferenceImageStars, Enumerable.Repeat(0.2f, width * height).ToArray());
            bag.Add(source);
            Assert.That(bag.ImageCount, Is.EqualTo(2));
            AssertBits(Enumerable.Repeat((float)(0.2f + ((double)0.6f - 0.2f) / 2), width * height).ToArray(), bag.Stack);
        }

        private static double Lerp(double first, double second, double fraction) {
            return fraction == 0 ? first : first + (second - first) * fraction;
        }

        private static void AssertBits(float[] expected, float[] actual) {
            for (int i = 0; i < expected.Length; i++) {
                if (float.IsNaN(expected[i]) && float.IsNaN(actual[i])) { continue; }
                if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(actual[i])) {
                    Assert.Fail($"Pixel {i}: expected {expected[i]:R}, actual {actual[i]:R}.");
                }
            }
        }
    }
}
