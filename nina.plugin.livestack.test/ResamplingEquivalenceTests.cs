using NINA.Plugin.Livestack.Image;

namespace nina.plugin.livestack.test {
    public class ResamplingEquivalenceTests {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void RotatedAndShiftedFramesMatchIndependentBilinearWeights(bool ushortInput, bool flipped) {
            // Cross the parallel-row threshold and include non-square dimensions and all borders.
            const int width = 521, height = 509;
            ushort[] raw = Enumerable.Range(0, width * height).Select(i => (ushort)((i * 7919L) % 65536)).ToArray();
            float[] pixels = raw.Select(value => value / (float)ushort.MaxValue).ToArray();
            foreach (double angle in new[] { -0.017, 0.017, Math.PI }) {
                double a = Math.Cos(angle), b = -Math.Sin(angle);
                foreach (double shift in new[] { -0.75, 0.75 }) {
                    double[,] matrix = AlignmentSafetyTests.Matrix(a, b, shift + 260 - a * 260 - b * 254,
                        -b, a, -shift + 254 + b * 260 - a * 254);
                    float[] actual = ushortInput
                        ? ImageTransformer2.Instance.ApplyAffineTransformation(raw, width, height, matrix, flipped)
                        : ImageTransformer2.Instance.ApplyAffineTransformation(pixels, width, height, matrix, flipped);
                    for (int y = 0; y < height; y++) {
                        for (int x = 0; x < width; x++) {
                            double sx = matrix[0, 0] * x + matrix[0, 1] * y + matrix[0, 2];
                            double sy = matrix[1, 0] * x + matrix[1, 1] * y + matrix[1, 2];
                            if (flipped) {
                                sx = width - 1 - sx;
                                sy = height - 1 - sy;
                            }
                            double expected = 0;
                            if (sx >= -1e-7 && sx <= width - 1 + 1e-7 && sy >= -1e-7 && sy <= height - 1 + 1e-7) {
                                sx = Math.Clamp(sx, 0, width - 1);
                                sy = Math.Clamp(sy, 0, height - 1);
                                int left = (int)sx, top = (int)sy;
                                double fx = sx - left, fy = sy - top;
                                int right = Math.Min(left + 1, width - 1), bottom = Math.Min(top + 1, height - 1);
                                double Pixel(int xx, int yy) => ushortInput ? raw[yy * width + xx] / (double)ushort.MaxValue : pixels[yy * width + xx];
                                expected = Pixel(left, top) * (1 - fx) * (1 - fy) + Pixel(right, top) * fx * (1 - fy)
                                    + Pixel(left, bottom) * (1 - fx) * fy + Pixel(right, bottom) * fx * fy;
                            }
                            if (!float.IsFinite(actual[y * width + x]) || Math.Abs(actual[y * width + x] - expected) > 6e-8) {
                                Assert.Fail($"Bilinear value changed at ({x}, {y}), angle {angle}, shift {shift}: {actual[y * width + x]} vs {expected}.");
                            }
                        }
                    }
                }
            }
        }

        [TestCase(-1e-8)]
        [TestCase(1e-8)]
        [TestCase(-1e-6)]
        [TestCase(1e-6)]
        public void BorderRoundoffToleranceIsPreservedInBothDirections(double shift) {
            float[] source = Enumerable.Range(0, 35).Select(i => (i + 1) / 100f).ToArray();
            float[] actual = ImageTransformer2.Instance.ApplyAffineTransformation(source, 7, 5, AlignmentSafetyTests.Matrix(tx: shift));
            int edge = shift < 0 ? 0 : 6;
            Assert.That(actual[edge], Math.Abs(shift) < 1e-7 ? Is.EqualTo(source[edge]) : Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AnUnusedNonfiniteNeighborDoesNotInvalidateAnExactSample(bool flipped) {
            float[] source = { 0.25f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };
            float[] actual = ImageTransformer2.Instance.ApplyAffineTransformation(source, 2, 2, AlignmentSafetyTests.Matrix(), flipped);
            Assert.That(actual, Is.EqualTo(flipped ? new[] { 0f, 0f, 0f, 0.25f } : new[] { 0.25f, 0f, 0f, 0f }));
        }
    }
}
