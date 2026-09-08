using Accord;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Plugin.Livestack.Image;

namespace nina.plugin.livestack.test {
    public class AlignmentSafetyTests {
        private static readonly ImageTransformer2 Transformer = ImageTransformer2.Instance;

        [TestCase(3)]
        [TestCase(7)]
        public void BrightnessFallbackCanSupplyTheRequiredEightStars(int normalBrightnessCount) {
            List<DetectedStar> detections = CreateCatalog(40, 107).Select((p, i) => new DetectedStar {
                Position = p,
                BoundingBox = new System.Drawing.Rectangle((int)p.X - 4, (int)p.Y - 4, 8, 8),
                MaxBrightness = i < normalBrightnessCount ? 20000 : double.NaN,
                AverageBrightness = 18000,
                Background = 500,
                HFR = 3
            }).ToList();
            List<Point> selected = Transformer.GetStars(detections, 1000, 1000);
            Assert.That(selected, Has.Count.EqualTo(40));
            Assert.That(Transformer.ComputeAlignment(selected, selected, 1000, 1000).Success, Is.True);
        }

        [TestCase(8, 100)]
        [TestCase(20, 100)]
        [TestCase(40, 106)]
        [TestCase(72, 114)]
        public void UnrelatedFieldsAreRejected(int count, int seed) {
            List<Point> reference = CreateCatalog(count, seed);
            List<Point> current = CreateCatalog(count, seed + 1);

            Assert.Throws<InvalidOperationException>(() => Transformer.ComputeAffineTransformation(current, reference));
        }

        [Test]
        public void FailedAlignmentAfterStarSelectionLeavesStackAndReferenceUntouched() {
            List<Point> reference = SelectStars(CreateCatalog(40, 106));
            List<Point> current = SelectStars(CreateCatalog(40, 107));
            LiveStackBag bag = new("target", "L", new ImageProperties(1000, 1000, 16, false, 100, 10), new ImageMetaData(), reference);
            float[] original = Enumerable.Repeat(0.4f, 1000000).ToArray();
            bag.Add((float[])original.Clone());

            Assert.Throws<InvalidOperationException>(() => {
                double[,] matrix = Transformer.ComputeAffineTransformation(current, reference);
                bag.AddTransformed(Enumerable.Repeat(0.8f, original.Length).ToArray(), matrix, false);
            });

            Assert.Multiple(() => {
                Assert.That(bag.ImageCount, Is.EqualTo(1));
                Assert.That(bag.Stack, Is.EqualTo(original));
                Assert.That(bag.ReferenceImageStars, Is.SameAs(reference));
            });
        }

        [TestCase(0.7, 1.0, 1.0)]
        [TestCase(-0.7, 1.0, 1.0)]
        [TestCase(0.0, 1.08, 1.08)]
        [TestCase(0.0, 0.92, 0.92)]
        [TestCase(0.0, -1.0, 1.0)]
        public void ImplausibleGeometryIsRejected(double shear, double scaleX, double scaleY) {
            List<Point> reference = CreateCatalog(40, 341);
            double[,] matrix = Matrix(scaleX, shear, scaleX < 0 ? 999 : 0, 0, scaleY, 0);
            List<Point> current = Project(reference, matrix);

            Assert.Throws<InvalidOperationException>(() => Transformer.ComputeAffineTransformation(current, reference));
        }

        [TestCase(3)]
        [TestCase(7)]
        public void TooFewIndependentStarsAreRejected(int count) {
            List<Point> stars = CreateCatalog(count, 87);
            Assert.Throws<InvalidOperationException>(() => Transformer.ComputeAffineTransformation(stars, stars));
        }

        [TestCase(-3.0)]
        [TestCase(0.0)]
        [TestCase(3.0)]
        [TestCase(90.0)]
        [TestCase(177.0)]
        [TestCase(180.0)]
        [TestCase(183.0)]
        public void RecoversRotationsWithMissingStarsAndFalseDetections(double angle) {
            List<Point> reference = CreateCatalog(60, 572);
            double radians = angle * Math.PI / 180;
            double a = Math.Cos(radians), b = -Math.Sin(radians);
            double[,] expected = Matrix(a, b, 500 - 500 * a - 500 * b + 12.25, -b, a, 500 + 500 * b - 500 * a - 7.75);
            List<Point> truth = Project(reference, expected);
            List<Point> current = truth.Skip(10).Concat(CreateCatalog(15, 989)).Reverse().ToList();

            double[,] actual = Transformer.ComputeAffineTransformation(current, reference);
            Assert.That(MaxError(Project(reference, actual), truth), Is.LessThan(0.1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CollinearOrRepeatedCatalogsAreRejected(bool repeated) {
            List<Point> stars = Enumerable.Range(0, 20).Select(i => repeated ? new Point(500, 500) : new Point(100 + 20 * i, 100 + 20 * i)).ToList();
            Assert.Throws<InvalidOperationException>(() => Transformer.ComputeAffineTransformation(stars, stars));
        }

        internal static List<Point> CreateCatalog(int count, int seed) {
            Random random = new(seed);
            return Enumerable.Range(0, count).Select(_ => new Point(40f + (float)random.NextDouble() * 920f, 40f + (float)random.NextDouble() * 920f)).ToList();
        }

        internal static List<Point> SelectStars(List<Point> points) {
            return Transformer.GetStars(points.Select((p, i) => new DetectedStar {
                Position = p,
                BoundingBox = new System.Drawing.Rectangle((int)p.X - 4, (int)p.Y - 4, 8, 8),
                MaxBrightness = 20000 + i * 20,
                Background = 500,
                HFR = 3
            }).ToList(), 1000, 1000);
        }

        internal static double[,] Matrix(double a = 1, double b = 0, double tx = 0, double c = 0, double d = 1, double ty = 0) {
            return new double[,] { { a, b, tx }, { c, d, ty }, { 0, 0, 1 } };
        }

        internal static List<Point> Project(List<Point> points, double[,] matrix) {
            return points.Select(p => new Point((float)(matrix[0, 0] * p.X + matrix[0, 1] * p.Y + matrix[0, 2]), (float)(matrix[1, 0] * p.X + matrix[1, 1] * p.Y + matrix[1, 2]))).ToList();
        }

        internal static double MaxError(List<Point> actual, List<Point> expected) {
            return actual.Zip(expected, (p, q) => Math.Sqrt(Math.Pow(p.X - q.X, 2) + Math.Pow(p.Y - q.Y, 2))).Max();
        }
    }
}
