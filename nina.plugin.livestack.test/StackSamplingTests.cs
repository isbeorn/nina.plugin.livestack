using NINA.Image.ImageData;
using NINA.Plugin.Livestack.Image;

namespace nina.plugin.livestack.test {
    public class StackSamplingTests {
        [TestCase(-0.5, false)]
        [TestCase(0.5, false)]
        [TestCase(-0.5, true)]
        [TestCase(0.5, true)]
        public void FractionalSamplingInterpolatesAnAnalyticalRamp(double shift, bool flipped) {
            const int width = 7, height = 5;
            ushort[] source = Enumerable.Range(0, width * height).Select(i => (ushort)(1000 * (i % width) + 100 * (i / width))).ToArray();
            float[] normalized = source.Select(v => v / (float)ushort.MaxValue).ToArray();
            double[,] matrix = AlignmentSafetyTests.Matrix(tx: shift, ty: shift);
            double sampleX = flipped ? width - 1 - (2 + shift) : 2 + shift;
            double sampleY = flipped ? height - 1 - (2 + shift) : 2 + shift;
            double expected = 1000 * sampleX + 100 * sampleY;
            ImageTransformer2 transformer = ImageTransformer2.Instance;

            Assert.Multiple(() => {
                Assert.That(transformer.ApplyAffineTransformation(normalized, width, height, matrix, flipped)[2 * width + 2], Is.EqualTo(expected / ushort.MaxValue).Within(1e-7));
                Assert.That(transformer.ApplyAffineTransformation(source, width, height, matrix, flipped)[2 * width + 2], Is.EqualTo(expected / ushort.MaxValue).Within(1e-7));
                Assert.That(transformer.ApplyAffineTransformationAsUshort(normalized, width, height, matrix, flipped)[2 * width + 2], Is.EqualTo(expected).Within(1));
            });
        }

        [TestCase(-0.25, false)]
        [TestCase(0.25, false)]
        [TestCase(-0.25, true)]
        [TestCase(0.25, true)]
        public void FractionalCoordinatesOutsideTheFrameAreMissing(double shift, bool flipped) {
            const int width = 4, height = 4;
            double[,] matrix = AlignmentSafetyTests.Matrix(tx: shift);
            int edge = shift < 0 ? 0 : width - 1;
            float[] source = Enumerable.Repeat(1f, width * height).ToArray();
            ushort[] ushortSource = Enumerable.Repeat(ushort.MaxValue, source.Length).ToArray();
            Assert.Multiple(() => {
                Assert.That(ImageTransformer2.Instance.ApplyAffineTransformation(source, width, height, matrix, flipped)[edge], Is.Zero);
                Assert.That(ImageTransformer2.Instance.ApplyAffineTransformation(ushortSource, width, height, matrix, flipped)[edge], Is.Zero);
                Assert.That(ImageTransformer2.Instance.ApplyAffineTransformationAsUshort(source, width, height, matrix, flipped)[edge], Is.Zero);
            });
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void AlternatingMissingCoverageUsesEachPixelsOwnCount(bool ushortInput, bool reverse) {
            LiveStackBag bag = CreateBag();
            bag.Add(Enumerable.Repeat(0.2f, 16).ToArray());
            int shift = reverse ? -2 : 2;
            Add(bag, 0.6f, AlignmentSafetyTests.Matrix(tx: shift), ushortInput);
            Add(bag, 1f, AlignmentSafetyTests.Matrix(tx: -shift), ushortInput);
            bag.Add(Enumerable.Repeat(0.4f, 16).ToArray());

            for (int y = 0; y < 4; y++) {
                for (int x = 0; x < 4; x++) {
                    double expected = (x < 2) != reverse ? 0.4 : (0.2 + 1.0 + 0.4) / 3;
                    Assert.That(bag.Stack[y * 4 + x], Is.EqualTo(expected).Within(2e-5), $"pixel {x},{y}");
                }
            }
            Assert.That(bag.ImageCount, Is.EqualTo(4));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReferenceReplacementResetsContributionCounts(bool ushortInput) {
            LiveStackBag bag = CreateBag();
            bag.Add(Enumerable.Repeat(0.2f, 16).ToArray());
            Add(bag, 0.6f, AlignmentSafetyTests.Matrix(tx: 2), ushortInput);
            bag.ForcePushReference(bag.Properties, bag.ReferenceImageStars, Enumerable.Repeat(0.8f, 16).ToArray());
            Add(bag, 0.4f, AlignmentSafetyTests.Matrix(), ushortInput);

            Assert.That(bag.Stack, Is.All.EqualTo(0.6f).Within(2e-5));
            Assert.That(bag.ImageCount, Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InvalidTransformDoesNotPartiallyModifyStack(bool ushortInput) {
            LiveStackBag bag = CreateBag();
            bag.Add(Enumerable.Repeat(0.2f, 16).ToArray());
            double[,] invalid = AlignmentSafetyTests.Matrix(b: 0.7);

            Assert.Throws<ArgumentException>(() => Add(bag, 0.8f, invalid, ushortInput));
            Assert.That(bag.ImageCount, Is.EqualTo(1));
            Assert.That(bag.Stack, Is.All.EqualTo(0.2f));
            Add(bag, 0.8f, AlignmentSafetyTests.Matrix(), ushortInput);
            Assert.That(bag.Stack, Is.All.EqualTo(0.5f).Within(2e-5));
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        public void InvalidMatrixIsRejectedBeforeWritingCallerBuffer(double value) {
            float[] destination = Enumerable.Repeat(0.7f, 16).ToArray();
            Assert.Throws<ArgumentException>(() => ImageTransformer2.Instance.ApplyAffineTransformationInto(new float[16], destination, 4, 4, AlignmentSafetyTests.Matrix(tx: value)));
            Assert.That(destination, Is.All.EqualTo(0.7f));
        }

        [TestCase(1, 1, false)]
        [TestCase(1, 5, true)]
        [TestCase(5, 1, true)]
        [TestCase(7, 5, false)]
        [TestCase(7, 5, true)]
        public void IdentitySamplingIncludesEveryBorderPixel(int width, int height, bool flipped) {
            float[] source = Enumerable.Range(0, width * height).Select(i => (i + 1) / 100f).ToArray();
            float[] expected = flipped ? source.Reverse().ToArray() : source;
            Assert.That(ImageTransformer2.Instance.ApplyAffineTransformation(source, width, height, AlignmentSafetyTests.Matrix(), flipped), Is.EqualTo(expected));
        }

        [Test]
        public void InvalidPixelsDoNotPoisonTheAverageOrGainWeight() {
            LiveStackBag bag = CreateBag();
            float[] reference = Enumerable.Repeat(0.2f, 16).ToArray();
            reference[0] = float.NaN;
            bag.Add(reference);
            bag.Add(Enumerable.Repeat(0.6f, 16).ToArray());
            Assert.That(bag.Stack[0], Is.EqualTo(0.6f));
            Assert.That(bag.Stack[1], Is.EqualTo(0.4f).Within(1e-6));

            float[] third = Enumerable.Repeat(0.8f, 16).ToArray();
            third[0] = float.PositiveInfinity;
            bag.Add(third);
            Assert.That(bag.Stack[0], Is.EqualTo(0.6f));
            bag.Add(Enumerable.Repeat(0.4f, 16).ToArray());
            Assert.That(bag.Stack[0], Is.EqualTo(0.5f).Within(1e-6));
            Assert.That(bag.Stack[1], Is.EqualTo(0.5f).Within(1e-6));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ValidBlackSamplesContributeWhileMissingSamplesDoNot(bool ushortInput) {
            float[] stack = Enumerable.Repeat(1f, 16).ToArray();
            uint[] counts = Enumerable.Repeat(1u, 16).ToArray();
            double[,] matrix = AlignmentSafetyTests.Matrix(tx: 2);
            if (ushortInput) {
                ImageTransformer2.Instance.ApplyAffineTransformationAndStack(new ushort[16], stack, counts, 4, 4, matrix);
            } else {
                ImageTransformer2.Instance.ApplyAffineTransformationAndStack(new float[16], stack, counts, 4, 4, matrix);
            }
            Assert.That(stack.Take(4), Is.EqualTo(new[] { 0.5f, 0.5f, 1f, 1f }));
            Assert.That(counts.Take(4), Is.EqualTo(new[] { 2u, 2u, 1u, 1u }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CounterOverflowIsRejectedBeforeAnyPixelChanges(bool ushortInput) {
            float[] stack = Enumerable.Repeat(0.7f, 16).ToArray();
            uint[] counts = Enumerable.Repeat(1u, 16).ToArray();
            counts[^1] = uint.MaxValue;
            Assert.Throws<ArgumentException>(() => {
                if (ushortInput) {
                    ImageTransformer2.Instance.ApplyAffineTransformationAndStack(new ushort[16], stack, counts, 4, 4, AlignmentSafetyTests.Matrix());
                } else {
                    ImageTransformer2.Instance.ApplyAffineTransformationAndStack(new float[16], stack, counts, 4, 4, AlignmentSafetyTests.Matrix());
                }
            });
            Assert.That(stack, Is.All.EqualTo(0.7f));
            Assert.That(counts[0], Is.EqualTo(1));
            Assert.That(counts[^1], Is.EqualTo(uint.MaxValue));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FrameCountLimitRejectsFurtherImagesAndReferenceReplacementRestartsCounting(bool ushortInput) {
            LiveStackBag bag = CreateBag();
            bag.Add(Enumerable.Repeat(0.2f, 16).ToArray());
            // Simulate the session limit without processing billions of frames.
            typeof(LiveStackBag).GetProperty(nameof(LiveStackBag.ImageCount))!.SetValue(bag, int.MaxValue - 1);
            Add(bag, 0.6f, AlignmentSafetyTests.Matrix(), ushortInput);
            Assert.That(bag.ImageCount, Is.EqualTo(int.MaxValue));
            Assert.That(bag.Stack, Is.All.EqualTo(0.4f).Within(2e-5));

            Assert.Throws<InvalidOperationException>(() => Add(bag, 0.8f, AlignmentSafetyTests.Matrix(), ushortInput));
            Assert.That(bag.ImageCount, Is.EqualTo(int.MaxValue));
            Assert.That(bag.Stack, Is.All.EqualTo(0.4f).Within(2e-5));

            bag.ForcePushReference(bag.Properties, bag.ReferenceImageStars, Enumerable.Repeat(0.8f, 16).ToArray());
            Add(bag, 0.4f, AlignmentSafetyTests.Matrix(), ushortInput);
            Assert.That(bag.ImageCount, Is.EqualTo(2));
            Assert.That(bag.Stack, Is.All.EqualTo(0.6f).Within(2e-5));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void InvalidDimensionsDoNotChangeTheDestination(int width) {
            float[] destination = Enumerable.Repeat(0.7f, 16).ToArray();
            Assert.Throws<ArgumentOutOfRangeException>(() => ImageTransformer2.Instance.ApplyAffineTransformationInto(new float[16], destination, width, 4, AlignmentSafetyTests.Matrix()));
            Assert.That(destination, Is.All.EqualTo(0.7f));
        }

        [Test]
        public void WrongMatrixShapeOrAliasedBuffersAreRejectedWithoutWriting() {
            float[] buffer = Enumerable.Repeat(0.7f, 16).ToArray();
            Assert.Throws<ArgumentException>(() => ImageTransformer2.Instance.ApplyAffineTransformationInto(new float[16], buffer, 4, 4, new double[2, 3]));
            Assert.Throws<ArgumentException>(() => ImageTransformer2.Instance.ApplyAffineTransformationInto(buffer, buffer, 4, 4, AlignmentSafetyTests.Matrix()));
            Assert.That(buffer, Is.All.EqualTo(0.7f));
        }

        [Test]
        public void CoordinateOverflowCannotCauseAnOutOfRangePixelRead() {
            double[,] matrix = AlignmentSafetyTests.Matrix(a: double.MaxValue, b: -double.MaxValue);
            Assert.DoesNotThrow(() => ImageTransformer2.Instance.ApplyAffineTransformation(new float[16], 4, 4, matrix));
        }

        [TestCase(-4)]
        [TestCase(4)]
        public void TransformWithNoOverlapCannotChangeStack(int shift) {
            LiveStackBag bag = CreateBag();
            bag.Add(Enumerable.Repeat(0.4f, 16).ToArray());
            Assert.Throws<ArgumentException>(() => bag.AddTransformed(new float[16], AlignmentSafetyTests.Matrix(tx: shift), false));
            Assert.That(bag.Stack, Is.All.EqualTo(0.4f));
            Assert.That(bag.ImageCount, Is.EqualTo(1));
        }

        private static LiveStackBag CreateBag() {
            return new LiveStackBag("target", "L", new ImageProperties(4, 4, 16, false, 100, 10), new ImageMetaData(), new List<Accord.Point>());
        }

        private static void Add(LiveStackBag bag, float value, double[,] matrix, bool ushortInput) {
            if (ushortInput) {
                bag.AddTransformed(Enumerable.Repeat((ushort)(value * ushort.MaxValue), 16).ToArray(), matrix, false);
            } else {
                bag.AddTransformed(Enumerable.Repeat(value, 16).ToArray(), matrix, false);
            }
        }
    }
}
