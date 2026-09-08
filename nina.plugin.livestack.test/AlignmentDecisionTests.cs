using Accord;
using NINA.Image.ImageData;
using NINA.Plugin.Livestack.Image;
using System.Diagnostics;
using System.Reflection;

namespace nina.plugin.livestack.test {
    public class AlignmentDecisionTests {
        private static readonly ImageProperties Properties = new(1000, 1000, 16, false, 100, 10);

        [TestCase(0.98, true)]
        [TestCase(1.02, true)]
        [TestCase(0.979, false)]
        [TestCase(1.021, false)]
        public void ScaleBoundsAreAppliedInBothDirections(double scale, bool accepted) {
            List<Point> reference = AlignmentSafetyTests.CreateCatalog(40, 81);
            List<Point> current = AlignmentSafetyTests.Project(reference, AlignmentSafetyTests.Matrix(a: scale, d: scale, tx: 500 * (1 - scale), ty: 500 * (1 - scale)));
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(current, reference, 1000, 1000);
            Assert.That(result.Success, Is.EqualTo(accepted), result.ToString());
        }

        [TestCase(-3.0, 0.99)]
        [TestCase(3.0, 1.01)]
        [TestCase(180.0, 1.0)]
        public void GoodFramesReturnSimilarityDiagnostics(double angle, double scale) {
            List<Point> reference = AlignmentSafetyTests.CreateCatalog(40, 39);
            double radians = angle * Math.PI / 180;
            double a = scale * Math.Cos(radians), b = -scale * Math.Sin(radians);
            double[,] expected = AlignmentSafetyTests.Matrix(a, b, 500 - 500 * a - 500 * b, -b, a, 500 + 500 * b - 500 * a);
            List<Point> current = AlignmentSafetyTests.Project(reference, expected);

            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(current, reference, 1000, 1000);

            Assert.Multiple(() => {
                Assert.That(result.Success, Is.True, result.ToString());
                Assert.That(result.UsesAffineModel, Is.False);
                Assert.That(result.InlierCount, Is.EqualTo(40));
                Assert.That(result.MatchFraction, Is.EqualTo(1));
                Assert.That(result.RmsErrorPixels, Is.LessThan(0.001));
                Assert.That(result.OverlapFraction, Is.GreaterThan(0.9));
            });
            Assert.That(AlignmentSafetyTests.MaxError(AlignmentSafetyTests.Project(reference, result.Matrix), current), Is.LessThan(0.001));
        }

        [Test]
        public void SmallAffineDistortionRequiresEvidenceFromTheFullField() {
            List<Point> reference = AlignmentSafetyTests.CreateCatalog(80, 22);
            double[,] expected = AlignmentSafetyTests.Matrix(0.9985, 0.0175, -12.75, -0.0145, 1.0015, 9.25);
            List<Point> current = AlignmentSafetyTests.Project(reference, expected);
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(current, reference, 1000, 1000);

            Assert.That(result.Success, Is.True, result.ToString());
            Assert.That(result.UsesAffineModel, Is.True);
            Assert.That(result.InlierCount, Is.EqualTo(80));
            Assert.That(result.RmsErrorPixels, Is.LessThan(0.001));
        }

        [TestCase(-800)]
        [TestCase(800)]
        public void MatchingStarsDoNotOverrideInsufficientFrameOverlap(int shift) {
            List<Point> reference = AlignmentSafetyTests.CreateCatalog(40, 69);
            List<Point> current = AlignmentSafetyTests.Project(reference, AlignmentSafetyTests.Matrix(tx: shift));
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(current, reference, 1000, 1000);
            Assert.That(result.Success, Is.False);
            Assert.That(result.Matrix, Is.Null);
            Assert.That(result.RejectionReason, Is.Not.Empty);
        }

        [Test]
        public void ManyNearbyReferencesCannotReuseFourTargetStarsAsIndependentSupport() {
            List<Point> reference = new();
            List<Point> current = new();
            foreach (Point center in new[] { new Point(100, 120), new Point(780, 150), new Point(300, 790), new Point(850, 880) }) {
                current.Add(center);
                for (int i = 0; i < 4; i++) {
                    reference.Add(new Point(center.X + 0.2f * i, center.Y + 0.1f * i));
                }
            }
            current.AddRange(AlignmentSafetyTests.CreateCatalog(12, 512));
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(current, reference, 1000, 1000);
            Assert.That(result.Success, Is.False);
        }

        [Test]
        public void SmallAccidentalConsensusCannotValidateAnOtherwiseUnrelatedField() {
            List<Point> reference = AlignmentSafetyTests.CreateCatalog(100, 85);
            List<Point> current = reference.Take(8).Concat(AlignmentSafetyTests.CreateCatalog(92, 86)).ToList();
            Assert.That(ImageTransformer2.Instance.ComputeAlignment(current, reference, 1000, 1000).Success, Is.False);
        }

        [Test]
        public void StarsConcentratedInOneCornerCannotEstablishAReference() {
            List<Point> stars = AlignmentSafetyTests.CreateCatalog(40, 81).Select(p => new Point(p.X * 0.03f + 100, p.Y * 0.03f + 100)).ToList();
            Assert.That(ImageTransformer2.Instance.ComputeAlignment(stars, stars, 1000, 1000).Success, Is.False);
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void NonfiniteStarPositionsAreRejected(float coordinate) {
            List<Point> reference = AlignmentSafetyTests.CreateCatalog(40, 32);
            reference[0] = new Point(coordinate, 100);
            Assert.That(ImageTransformer2.Instance.ComputeAlignment(reference, reference, 1000, 1000).Success, Is.False);
        }

        [Test]
        public void CancellationIsNotConvertedIntoAMatchingFallback() {
            List<Point> stars = AlignmentSafetyTests.CreateCatalog(1000, 32);
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => ImageTransformer2.Instance.ComputeAlignment(stars.ToList(), stars, 1000, 1000, cancellation.Token));
        }

        [Test]
        public void CollinearRansacProposalsExhaustTheAttemptBudget() {
            MethodInfo estimator = typeof(ImageTransformer2).GetMethod("EstimateAffineTransformation", BindingFlags.NonPublic | BindingFlags.Instance)!;
            List<(Point Ref, Point Src, int Votes)> pairs = Enumerable.Range(0, 8).Select(i => (new Point(100 + 20 * i, 200 + 40 * i), new Point(105 + 20 * i, 198 + 40 * i), 10)).ToList();
            using CancellationTokenSource watchdog = new(TimeSpan.FromSeconds(2));
            Stopwatch stopwatch = Stopwatch.StartNew();

            TargetInvocationException? error = Assert.Throws<TargetInvocationException>(() => estimator.Invoke(ImageTransformer2.Instance, new object[] { pairs, watchdog.Token }));

            Assert.That(error!.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RejectingAFramePreservesTheStackThenTheNextGoodFrameStillWorks(bool ushortInput) {
            List<Point> reference = AlignmentSafetyTests.CreateCatalog(40, 106);
            LiveStackBag bag = new("target", "L", Properties, new ImageMetaData(), reference);
            Assert.That(Add(bag, 0.2f, reference, Properties, ushortInput).Success, Is.True);
            float[] snapshot = (float[])bag.Stack.Clone();
            List<Point> savedReference = bag.ReferenceImageStars;

            AlignmentResult rejected = Add(bag, 0.9f, AlignmentSafetyTests.CreateCatalog(40, 107), Properties, ushortInput);
            Assert.Multiple(() => {
                Assert.That(rejected.Success, Is.False);
                Assert.That(bag.ImageCount, Is.EqualTo(1));
                Assert.That(bag.Stack, Is.EqualTo(snapshot));
                Assert.That(bag.ReferenceImageStars, Is.SameAs(savedReference));
                Assert.That(bag.Properties, Is.SameAs(Properties));
            });

            List<Point> current = AlignmentSafetyTests.Project(reference, AlignmentSafetyTests.Matrix(tx: 3, ty: 5));
            Assert.That(Add(bag, 0.6f, current, Properties, ushortInput).Success, Is.True);
            Assert.That(bag.Stack[500 * 1000 + 500], Is.EqualTo(0.4f).Within(2e-5));
            Assert.That(bag.Stack[^1], Is.EqualTo(0.2f).Within(2e-5));
            Assert.That(bag.ImageCount, Is.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AWeakFirstFrameDoesNotBecomeTheReference(bool ushortInput) {
            LiveStackBag bag = new("target", "L", Properties, new ImageMetaData(), null);
            AlignmentResult rejected = Add(bag, 0.2f, AlignmentSafetyTests.CreateCatalog(7, 107), Properties, ushortInput);
            Assert.That(rejected.Success, Is.False);
            Assert.That(bag.Stack, Is.Null);
            Assert.That(bag.ImageCount, Is.Zero);
            Assert.That(bag.ReferenceImageStars, Is.Null);
            Assert.That(Add(bag, 0.6f, AlignmentSafetyTests.CreateCatalog(40, 108), Properties, ushortInput).Success, Is.True);
            Assert.That(bag.ImageCount, Is.EqualTo(1));
            Assert.That(bag.Stack, Is.All.EqualTo(0.6f).Within(2e-5));
        }

        [TestCase(2000, 500, 16, false, 100, 10)]
        [TestCase(1000, 1000, 12, false, 100, 10)]
        [TestCase(1000, 1000, 16, true, 100, 10)]
        [TestCase(1000, 1000, 16, false, 101, 10)]
        [TestCase(1000, 1000, 16, false, 100, 11)]
        public void IncompatibleFramesCannotChangeAnExistingStack(int width, int height, int bitDepth, bool bayered, int gain, int offset) {
            List<Point> reference = AlignmentSafetyTests.CreateCatalog(40, 113);
            LiveStackBag bag = new("target", "L", Properties, new ImageMetaData(), reference);
            Add(bag, 0.2f, reference, Properties, false);
            ImageProperties incompatible = new(width, height, bitDepth, bayered, gain, offset);
            Assert.That(Add(bag, 0.6f, reference, incompatible, false).Success, Is.False);
            Assert.That(bag.Stack, Is.All.EqualTo(0.2f));
            Assert.That(bag.ImageCount, Is.EqualTo(1));
        }

        [Test]
        public void PrecanceledFrameDoesNotInitializeOrChangeAStack() {
            List<Point> stars = AlignmentSafetyTests.CreateCatalog(40, 72);
            LiveStackBag bag = new("target", "L", Properties, new ImageMetaData(), stars);
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => bag.AlignAndAdd(new float[1000000], Properties, stars, cancellation.Token));
            Assert.That(bag.Stack, Is.Null);
            Assert.That(bag.ImageCount, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DirectMeridianFlipPreservesTheReferenceImagePixels(bool ushortInput) {
            List<Point> reference = AlignmentSafetyTests.CreateCatalog(40, 311);
            float[] pixels = Enumerable.Range(0, 1000000).Select(i => (i % 1000 + i / 1000) / 2000f).ToArray();
            LiveStackBag bag = new("target", "L", Properties, new ImageMetaData(), reference);
            Assert.That(bag.AlignAndAdd((float[])pixels.Clone(), Properties, reference).Success, Is.True);
            List<Point> flippedStars = AlignmentSafetyTests.Project(reference, AlignmentSafetyTests.Matrix(a: -1, tx: 999, d: -1, ty: 999));
            float[] flippedPixels = pixels.Reverse().ToArray();
            AlignmentResult result = ushortInput
                ? bag.AlignAndAdd(flippedPixels.Select(v => (ushort)(v * ushort.MaxValue)).ToArray(), Properties, flippedStars)
                : bag.AlignAndAdd(flippedPixels, Properties, flippedStars);
            Assert.That(result.Success, Is.True, result.ToString());
            FloatAssert.AreEqual(pixels, bag.Stack, absTol: 2e-5f, relTol: 2e-5f);
            Assert.That(bag.ImageCount, Is.EqualTo(2));
        }

        [Test]
        public void ReturnedMatrixCannotMutateTheSavedDecision() {
            List<Point> stars = AlignmentSafetyTests.CreateCatalog(40, 32);
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(stars, stars, 1000, 1000);
            result.Matrix[0, 0] = 7;
            Assert.That(result.Matrix[0, 0], Is.EqualTo(1));
        }

        private static AlignmentResult Add(LiveStackBag bag, float value, List<Point> stars, ImageProperties properties, bool ushortInput) {
            return ushortInput
                ? bag.AlignAndAdd(Enumerable.Repeat((ushort)(value * ushort.MaxValue), properties.Width * properties.Height).ToArray(), properties, stars)
                : bag.AlignAndAdd(Enumerable.Repeat(value, properties.Width * properties.Height).ToArray(), properties, stars);
        }
    }
}
