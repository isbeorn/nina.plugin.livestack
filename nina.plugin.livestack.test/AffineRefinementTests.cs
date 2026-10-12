using NINA.Plugin.Livestack.Image;
using System.Reflection;

namespace nina.plugin.livestack.test {
    public class AffineRefinementTests {
        private static readonly MethodInfo FitMethod = typeof(ImageTransformer2).GetMethod("ComputeAffineTransformationMatrix", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static IEnumerable<TestCaseData> CatalogSizes {
            get {
                foreach (int count in new[] { 3, 4, 8, 20, 21, 72, 300, 1000 }) {
                    yield return new TestCaseData(count, false);
                    yield return new TestCaseData(count, true);
                }
            }
        }

        [TestCaseSource(nameof(CatalogSizes))]
        public void FloatCatalogFitsPreserveTheTransformAtLargeFrameCorners(int count, bool reverse) {
            double[,] expected = { { 1.001, 0.003, 25.25 }, { -0.002, 0.999, -13.5 }, { 0, 0, 1 } };
            (double[,] source, double[,] target) = CreateCatalog(count, expected);
            if (reverse) {
                (source, target) = (target, source);
                expected = Invert(expected);
            }

            double[,] actual = Fit(source, target);

            Assert.That(MaximumCornerError(actual, expected), Is.LessThan(0.002));
            Assert.That(actual[2, 0], Is.Zero);
            Assert.That(actual[2, 1], Is.Zero);
            Assert.That(actual[2, 2], Is.EqualTo(1));
        }

        [TestCase(4, 0.000999, false)]
        [TestCase(4, 0.001001, true)]
        [TestCase(20, 0.000999, false)]
        [TestCase(20, 0.001001, true)]
        [TestCase(1000, 0.000999, false)]
        [TestCase(1000, 0.001001, true)]
        [TestCase(1000, 0.0, false)]
        public void SingularValueRatioRejectsBothSolverPathsAtTheSameBoundary(int count, double minorAxis, bool accepted) {
            double[,] source = new double[count, 2];
            for (int i = 0; i < count; i++) {
                source[i, 0] = (float)((i % 2 == 0 ? -1 : 1) * 1000);
                source[i, 1] = (float)((i % 4 < 2 ? -1 : 1) * minorAxis);
            }

            if (accepted) {
                Assert.That(MaximumCornerError(Fit(source, source), new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }), Is.LessThan(0.00001));
            } else {
                TargetInvocationException? error = Assert.Throws<TargetInvocationException>(() => Fit(source, source));
                Assert.That(error!.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(error.InnerException!.Message, Is.EqualTo("Nearly collinear affine correspondences."));
            }
        }

        [TestCase(0.0)]
        [TestCase(0.0000001)]
        [TestCase(double.NaN)]
        public void DegenerateOrNonfiniteSpreadCannotProduceAnAffineMatrix(double spread) {
            double[,] source = { { -spread, -spread }, { spread, -spread }, { 0, spread } };
            TargetInvocationException? error = Assert.Throws<TargetInvocationException>(() => Fit(source, source));
            Assert.That(error!.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(error.InnerException!.Message, Is.EqualTo("Degenerate affine correspondences."));
        }

        [Test]
        public void DenseRefinementDoesNotAllocateAQuadraticWorkspace() {
            (double[,] source, double[,] target) = CreateCatalog(1000, new double[,] { { 1, 0, 2 }, { 0, 1, -3 }, { 0, 0, 1 } });
            Fit(source, target);
            long before = GC.GetAllocatedBytesForCurrentThread();

            double[,] actual = Fit(source, target);

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.LessThan(1024 * 1024), "A 1000-pair affine refinement should not materialize a square left-singular-vector matrix.");
            Assert.That(MaximumCornerError(actual, new double[,] { { 1, 0, 2 }, { 0, 1, -3 }, { 0, 0, 1 } }), Is.LessThan(0.002));
        }

        private static double[,] Fit(double[,] source, double[,] target) {
            return (double[,])FitMethod.Invoke(ImageTransformer2.Instance, new object[] { source, target })!;
        }

        private static (double[,] Source, double[,] Target) CreateCatalog(int count, double[,] matrix) {
            double[,] source = new double[count, 2];
            double[,] target = new double[count, 2];
            Random random = new(105 + count);
            for (int i = 0; i < count; i++) {
                double x = (float)(100 + 11400 * random.NextDouble());
                double y = (float)(100 + 8500 * random.NextDouble());
                source[i, 0] = x;
                source[i, 1] = y;
                target[i, 0] = (float)(matrix[0, 0] * x + matrix[0, 1] * y + matrix[0, 2]);
                target[i, 1] = (float)(matrix[1, 0] * x + matrix[1, 1] * y + matrix[1, 2]);
            }
            return (source, target);
        }

        private static double MaximumCornerError(double[,] actual, double[,] expected) {
            double maximum = 0;
            foreach ((int x, int y) in new[] { (0, 0), (11655, 0), (0, 8749), (11655, 8749), (5828, 4375) }) {
                double dx = (actual[0, 0] - expected[0, 0]) * x + (actual[0, 1] - expected[0, 1]) * y + actual[0, 2] - expected[0, 2];
                double dy = (actual[1, 0] - expected[1, 0]) * x + (actual[1, 1] - expected[1, 1]) * y + actual[1, 2] - expected[1, 2];
                maximum = Math.Max(maximum, Math.Sqrt(dx * dx + dy * dy));
            }
            return maximum;
        }

        private static double[,] Invert(double[,] matrix) {
            double determinant = matrix[0, 0] * matrix[1, 1] - matrix[0, 1] * matrix[1, 0];
            return new double[,] {
                { matrix[1, 1] / determinant, -matrix[0, 1] / determinant, (matrix[0, 1] * matrix[1, 2] - matrix[1, 1] * matrix[0, 2]) / determinant },
                { -matrix[1, 0] / determinant, matrix[0, 0] / determinant, (matrix[1, 0] * matrix[0, 2] - matrix[0, 0] * matrix[1, 2]) / determinant },
                { 0, 0, 1 }
            };
        }
    }
}