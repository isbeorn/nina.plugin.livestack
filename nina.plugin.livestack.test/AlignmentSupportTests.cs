using Accord;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using System.Reflection;
using NINA.Plugin.Livestack.Image;

namespace nina.plugin.livestack.test {
    public class AlignmentSupportTests {
        [Test]
        public void IndependentlyTruncatedCatalogsStillAlignAgainstAllAvailableSupport() {
            List<Point> field = AlignmentSafetyTests.CreateCatalog(3000, 914);
            List<Point> reference = field.Take(1000).ToList();
            List<Point> current = field.Skip(900).Take(1000).ToList();
            AlignmentStarCatalog referenceCatalog = new(reference, field);
            AlignmentStarCatalog currentCatalog = new(current, field);
            Assert.That(ImageTransformer2.Instance.ComputeAlignment(current, reference, 1000, 1000).Success, Is.False, "The independently truncated search catalogs cannot supply enough evidence.");
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(currentCatalog, referenceCatalog, 1000, 1000);
            Assert.That(result.Success, Is.True, result.ToString());
        }

        [TestCase(16384)]
        [TestCase(16385)]
        [TestCase(22000)]
        public void PreparedSupportIsDistinctBoundedAndContainsEverySearchStar(int count) {
            List<DetectedStar> detections = Detections(AlignmentSafetyTests.CreateCatalog(count, 307));
            detections.Add(detections[0]);
            detections.Add(new DetectedStar { Position = new Point(500, 500), HFR = 3, MaxBrightness = 65001 });
            detections.Add(new DetectedStar { Position = new Point(float.NaN, 500), HFR = 3, MaxBrightness = 20000 });
            List<Point> legacy = ImageTransformer2.Instance.GetStars(detections, 1000, 1000);
            AlignmentStarCatalog catalog = ImageTransformer2.Instance.PrepareAlignmentStars(detections, 1000, 1000);
            Assert.Multiple(() => {
                Assert.That(catalog.MatchStars, Is.EqualTo(legacy));
                Assert.That(catalog.MatchStars.Count, Is.LessThanOrEqualTo(1000));
                Assert.That(catalog.SupportStars.Count, Is.EqualTo(Math.Min(count, 16384)));
                Assert.That(catalog.SupportStars.Distinct().Count(), Is.EqualTo(catalog.SupportStars.Count));
                Assert.That(catalog.SupportStars, Is.SupersetOf(catalog.MatchStars));
                Assert.That(catalog.SupportStars, Does.Not.Contain(new Point(500, 500)));
                Assert.That(catalog.SupportStars.All(point => float.IsFinite(point.X) && float.IsFinite(point.Y)), Is.True);
            });
            Assert.Throws<NotSupportedException>(() => ((IList<Point>)catalog.MatchStars)[0] = new Point());
            Assert.Throws<NotSupportedException>(() => ((IList<Point>)catalog.SupportStars)[0] = new Point());
        }

        [Test]
        public void SupportCapUsesDeterministicQualityOrderWithinFixedSpatialCells() {
            List<DetectedStar> detections = Detections(AlignmentSafetyTests.CreateCatalog(22000, 551));
            AlignmentStarCatalog first = ImageTransformer2.Instance.PrepareAlignmentStars(detections, 1000, 1000);
            detections.Reverse();
            AlignmentStarCatalog reversed = ImageTransformer2.Instance.PrepareAlignmentStars(detections, 1000, 1000);
            Assert.That(first.MatchStars, Is.EqualTo(reversed.MatchStars));
            Assert.That(first.SupportStars, Is.EqualTo(reversed.SupportStars));
        }

        [TestCase(199)]
        [TestCase(200)]
        public void SparseStarLossPreservesTheReportedFixedReferenceFraction(int remaining) {
            List<Point> field = AlignmentSafetyTests.CreateCatalog(1000, 991);
            AlignmentStarCatalog reference = new(field, field);
            AlignmentStarCatalog current = new(field.Take(remaining), field.Take(remaining));
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(current, reference, 1000, 1000);
            Assert.That(result.Success, Is.True, result.ToString());
            Assert.That(result.MatchFraction, Is.EqualTo(remaining / 1000d));
            Assert.That(result.InlierCount, Is.EqualTo(remaining));
        }

        [TestCase(199)]
        [TestCase(200)]
        [TestCase(201)]
        public void AddingUnmatchedStarsAcrossTheFixedMinimumDoesNotRejectRealSupport(int currentCount) {
            List<Point> field = AlignmentSafetyTests.CreateCatalog(1000, 992);
            List<Point> current = field.Take(41).Concat(AlignmentSafetyTests.CreateCatalog(currentCount - 41, 993)).ToList();
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(new AlignmentStarCatalog(current, current), new AlignmentStarCatalog(field, field), 1000, 1000);
            Assert.That(result.Success, Is.True, result.ToString());
            Assert.That(result.InlierCount, Is.GreaterThanOrEqualTo(41));
            Assert.That(result.MatchFraction, Is.LessThan(0.1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SparseUnrelatedFieldsCannotUseTheLowerMinimumAsEvidence(bool clustered) {
            List<Point> reference = clustered ? ClusteredCatalog(315, 994, 18) : AlignmentSafetyTests.CreateCatalog(315, 994);
            List<Point> current = clustered ? ClusteredCatalog(19, 995, 18) : AlignmentSafetyTests.CreateCatalog(19, 995);
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(new AlignmentStarCatalog(current, current), new AlignmentStarCatalog(reference, reference), 1000, 1000);
            Assert.That(result.Success, Is.False, result.ToString());
        }

        [Test]
        public void SparseStarLossFailurePreservesPredictionThenTheNextGoodFrameRecovers() {
            ImageProperties properties = new(1000, 1000, 16, false, 100, 10);
            List<Point> field = AlignmentSafetyTests.CreateCatalog(315, 996);
            LiveStackBag bag = new("target", "L", properties, new ImageMetaData(), null);
            AlignmentStarCatalog reference = new(field, field);
            Assert.That(Add(bag, properties, reference, false).Success, Is.True);
            List<Point> shifted = AlignmentSafetyTests.Project(field, AlignmentSafetyTests.Matrix(tx: 9, ty: -7));
            Assert.That(Add(bag, properties, new AlignmentStarCatalog(shifted, shifted), false).Success, Is.True);
            double[,] committed = Prediction(bag)!;
            List<Point> unrelated = AlignmentSafetyTests.CreateCatalog(19, 997);
            Assert.That(Add(bag, properties, new AlignmentStarCatalog(unrelated, unrelated), false).Success, Is.False);
            Assert.That(Prediction(bag), Is.EqualTo(committed));
            Assert.That(bag.ImageCount, Is.EqualTo(2));
            Assert.That(Add(bag, properties, new AlignmentStarCatalog(shifted, shifted), false).Success, Is.True);
            Assert.That(bag.ImageCount, Is.EqualTo(3));
        }

        [TestCase(-28, 19)]
        [TestCase(28, -19)]
        [TestCase(0, 0)]
        public void AStalePredictionFallsBackToBoundedRecovery(int tx, int ty) {
            List<Point> field = AlignmentSafetyTests.CreateCatalog(80, 501);
            AlignmentStarCatalog reference = new(field, field);
            double[,] expected = AlignmentSafetyTests.Matrix(tx: tx, ty: ty);
            List<Point> shifted = AlignmentSafetyTests.Project(field, expected);
            AlignmentStarCatalog current = new(shifted, shifted);
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(current, reference, 1000, 1000, AlignmentSafetyTests.Matrix(a: -1, d: -1, tx: 999, ty: 999));
            Assert.That(result.Success, Is.True, result.ToString());
            Assert.That(AlignmentSafetyTests.MaxError(AlignmentSafetyTests.Project(field, result.Matrix), shifted), Is.LessThan(0.001));
        }

        [Test]
        public void MeridianFlipAndFlipBackRecoverFromThePreviouslyCommittedOrientation() {
            List<Point> field = AlignmentSafetyTests.CreateCatalog(80, 502);
            AlignmentStarCatalog reference = new(field, field);
            double[,] flip = AlignmentSafetyTests.Matrix(a: -1, d: -1, tx: 999, ty: 999);
            List<Point> flipped = AlignmentSafetyTests.Project(field, flip);
            AlignmentResult forward = ImageTransformer2.Instance.ComputeAlignment(new AlignmentStarCatalog(flipped, flipped), reference, 1000, 1000);
            Assert.That(forward.Success, Is.True, forward.ToString());
            AlignmentResult backward = ImageTransformer2.Instance.ComputeAlignment(new AlignmentStarCatalog(field, field), reference, 1000, 1000, forward.Matrix);
            Assert.That(backward.Success, Is.True, backward.ToString());
            Assert.That(AlignmentSafetyTests.MaxError(AlignmentSafetyTests.Project(field, backward.Matrix), field), Is.LessThan(0.001));
        }

        [TestCase(1000, 778)]
        [TestCase(512, 778)]
        [TestCase(256, 778)]
        [TestCase(256, 787)]
        [TestCase(256, 799)]
        public void DenseUnrelatedSupportCannotValidateAFalsePrediction(int size, int seed) {
            List<Point> reference = ScaleCatalog(AlignmentSafetyTests.CreateCatalog(1000, seed), size);
            List<Point> current = ScaleCatalog(AlignmentSafetyTests.CreateCatalog(16384, seed + 1), size);
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(new AlignmentStarCatalog(current.Take(1000), current), new AlignmentStarCatalog(reference, reference), size, size);
            Assert.That(result.Success, Is.False, result.ToString());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RejectedAndCanceledFramesPreservePredictionAndReferenceReplacementResetsIt(bool ushortInput) {
            ImageProperties properties = new(1000, 1000, 16, false, 100, 10);
            List<Point> field = AlignmentSafetyTests.CreateCatalog(40, 614);
            AlignmentStarCatalog reference = new(field, field);
            LiveStackBag first = new("target", "L", properties, new ImageMetaData(), null);
            LiveStackBag second = new("other", "L", properties, new ImageMetaData(), null);
            Add(first, properties, reference, ushortInput);
            List<Point> shifted = AlignmentSafetyTests.Project(field, AlignmentSafetyTests.Matrix(tx: 9, ty: -7));
            Assert.That(Add(first, properties, new AlignmentStarCatalog(shifted, shifted), ushortInput).Success, Is.True);
            double[,] saved = Prediction(first)!;
            Assert.That(saved[0, 2], Is.EqualTo(9).Within(0.001));
            List<Point> unrelated = AlignmentSafetyTests.CreateCatalog(40, 615);
            Assert.That(Add(first, properties, new AlignmentStarCatalog(unrelated, unrelated), ushortInput).Success, Is.False);
            Assert.Throws<OperationCanceledException>(() => {
                if (ushortInput) {
                    first.AlignAndAdd(new ushort[1000000], properties, reference, new CancellationToken(true));
                } else {
                    first.AlignAndAdd(new float[1000000], properties, reference, new CancellationToken(true));
                }
            });
            Assert.That(Prediction(first), Is.EqualTo(saved));
            Assert.That(first.ImageCount, Is.EqualTo(2));
            Assert.That(first.ReferenceCatalog, Is.SameAs(reference));
            Assert.That(Prediction(second), Is.Null);
            first.ForcePushReference(properties, new AlignmentStarCatalog(unrelated, unrelated), new float[1000000]);
            Assert.That(Prediction(first), Is.Null);
            Assert.That(first.ReferenceCatalog!.MatchStars, Is.EqualTo(unrelated));
        }

        [TestCase(901)]
        [TestCase(903)]
        [TestCase(905)]
        public void LocalDensityRejectsIndependentClustersDespiteGlobalSpatialCoverage(int seed) {
            List<Point> reference = ClusteredCatalog(1000, seed, 18);
            List<Point> current = ClusteredCatalog(16384, seed + 1, 18);
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(new AlignmentStarCatalog(current.Take(1000), current), new AlignmentStarCatalog(reference, reference), 1000, 1000);
            Assert.That(result.Success, Is.False, result.ToString());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DenseRealCorrespondencesRemainAcceptable(bool clustered) {
            List<Point> field = clustered ? ClusteredCatalog(16384, 917, 100) : ScaleCatalog(AlignmentSafetyTests.CreateCatalog(16384, 917), 256);
            int size = clustered ? 1000 : 256;
            double[,] expected = AlignmentSafetyTests.Matrix(tx: 0.7, ty: -0.6);
            Random jitter = new(918);
            List<Point> current = AlignmentSafetyTests.Project(field, expected)
                .Select(point => new Point(point.X + (float)(jitter.NextDouble() - 0.5) * 0.4f, point.Y + (float)(jitter.NextDouble() - 0.5) * 0.4f)).ToList();
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(new AlignmentStarCatalog(current.Skip(900).Take(1000), current), new AlignmentStarCatalog(field.Take(1000), field), size, size, expected);
            Assert.That(result.Success, Is.True, result.ToString());
            Assert.That(result.InlierCount, Is.GreaterThan(900));
            Assert.That(result.RmsErrorPixels, Is.LessThan(0.3));
        }

        [Test]
        public void EightWellDistributedStarsWithCentroidNoiseStillProvideIndependentSupport() {
            List<Point> field = AlignmentSafetyTests.CreateCatalog(8, 919);
            Random jitter = new(920);
            List<Point> current = field.Select(point => new Point(point.X + (float)(jitter.NextDouble() - 0.5), point.Y + (float)(jitter.NextDouble() - 0.5))).ToList();
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(new AlignmentStarCatalog(current, current), new AlignmentStarCatalog(field, field), 1000, 1000);
            Assert.That(result.Success, Is.True, result.ToString());
            Assert.That(result.InlierCount, Is.EqualTo(8));
        }

        [Test]
        public void DenseValidSupportNearFrameEdgesUsesTheAvailableNeighborhoodArea() {
            List<Point> field = ScaleCatalog(AlignmentSafetyTests.CreateCatalog(16384, 921), 256);
            List<Point> edgeStars = field.Where(point => point.X < 20 || point.X > 236).Take(1000).ToList();
            Assert.That(edgeStars.Count, Is.EqualTo(1000));
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(new AlignmentStarCatalog(edgeStars, field), new AlignmentStarCatalog(edgeStars, field), 256, 256);
            Assert.That(result.Success, Is.True, result.ToString());
        }

        [TestCase(-600)]
        [TestCase(600)]
        public void ReferencesOutsideTheOverlapCannotInflateTheChanceExpectation(int shift) {
            List<Point> field = AlignmentSafetyTests.CreateCatalog(1000, 923);
            double[,] expected = AlignmentSafetyTests.Matrix(tx: shift);
            List<Point> current = AlignmentSafetyTests.Project(field, expected).Where(point => point.X >= 4 && point.X < 996).ToList();
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(new AlignmentStarCatalog(current, current), new AlignmentStarCatalog(field, field), 1000, 1000, expected);
            Assert.That(result.Success, Is.True, result.ToString());
            Assert.That(result.OverlapFraction, Is.EqualTo(0.4).Within(0.001));
        }

        [TestCase(32, false, false)]
        [TestCase(32, true, false)]
        [TestCase(71, false, false)]
        [TestCase(71, true, false)]
        [TestCase(72, false, true)]
        [TestCase(72, true, true)]
        public void SparseModelAmbiguityIsBoundedAndCheckedInBothDirections(int count, bool reverse, bool accepted) {
            List<Point> reference = AlignmentSafetyTests.CreateCatalog(count, 998).Select(point => new Point(500 + (point.X - 500) * 0.25f, 500 + (point.Y - 500) * 0.25f)).ToList();
            List<Point> current = AlignmentSafetyTests.Project(reference, AlignmentSafetyTests.Matrix(a: 1.0016, d: 0.9984, tx: -0.8, ty: 0.8));
            if (reverse) {
                (reference, current) = (current, reference);
            }
            Assert.That(ImageTransformer2.Instance.ComputeAlignment(current, reference, 1000, 1000).Success, Is.True, "The compatibility list path retains its previous model preference.");
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(new AlignmentStarCatalog(current, current), new AlignmentStarCatalog(reference, reference), 1000, 1000);
            Assert.That(result.Success, Is.EqualTo(accepted), result.ToString());
        }

        [TestCase(72, false, false)]
        [TestCase(72, true, false)]
        [TestCase(199, false, false)]
        [TestCase(199, true, false)]
        [TestCase(200, false, true)]
        [TestCase(200, true, true)]
        public void ReducedMinimumStillChecksModelAgreementAgainstDenseReference(int count, bool reverse, bool accepted) {
            List<Point> reference = AlignmentSafetyTests.CreateCatalog(count, 998)
                .Select(point => new Point(500 + (point.X - 500) * 0.25f, 500 + (point.Y - 500) * 0.25f)).ToList();
            List<Point> current = AlignmentSafetyTests.Project(reference, AlignmentSafetyTests.Matrix(a: 1.0016, d: 0.9984, tx: -0.8, ty: 0.8));
            if (reverse) {
                (reference, current) = (current, reference);
            }
            for (int i = count; i < 1000; i++) {
                reference.Add(new Point((i % 2 == 0 ? 50 : 850) + i * 0.01f, 50 + (i * 17) % 900));
            }
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(new AlignmentStarCatalog(current, current),
                new AlignmentStarCatalog(reference, reference), 1000, 1000);
            Assert.That(result.Success, Is.EqualTo(accepted), result.ToString());
        }

        private static List<Point> ClusteredCatalog(int count, int seed, double radius) {
            Random random = new(seed);
            List<Point> stars = new(count);
            Point[] centers = new[] { new Point(180, 180), new Point(820, 180), new Point(180, 820), new Point(820, 820) };
            for (int i = 0; i < count; i++) {
                Point center = centers[i % centers.Length];
                float x = (float)(center.X + radius * (random.NextDouble() - 0.5));
                float y = (float)(center.Y + radius * (random.NextDouble() - 0.5));
                stars.Add(new Point(Math.Clamp(x, 4, 995), Math.Clamp(y, 4, 995)));
            }
            return stars;
        }

        private static double[,]? Prediction(LiveStackBag bag) {
            return (double[,]?)typeof(LiveStackBag).GetField("lastCommittedMatrix", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bag);
        }

        private static AlignmentResult Add(LiveStackBag bag, ImageProperties properties, AlignmentStarCatalog stars, bool ushortInput) {
            return ushortInput ? bag.AlignAndAdd(new ushort[1000000], properties, stars) : bag.AlignAndAdd(new float[1000000], properties, stars);
        }

        private static List<Point> ScaleCatalog(List<Point> points, int size) {
            return points.Select(point => new Point(point.X * size / 1000f, point.Y * size / 1000f)).ToList();
        }

        private static List<DetectedStar> Detections(List<Point> points) {
            return points.Select(point => new DetectedStar {
                Position = point, MaxBrightness = 20000 + point.X * 10, HFR = 3, Background = 500,
                BoundingBox = new System.Drawing.Rectangle((int)point.X - 4, (int)point.Y - 4, 8, 8)
            }).ToList();
        }
    }
}
