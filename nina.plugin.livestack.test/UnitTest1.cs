using MathNet.Numerics.Statistics;
using Moq;
using NINA.Core.Utility.WindowService;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.TestSupport;
using NINA.Plugin.Livestack.LivestackDockables;
using NINA.Profile.Interfaces;
using Accord;
using System.Reflection;

namespace nina.plugin.livestack.test {

    public class Tests {
        public string ImagePath { get; set; } = "..\\..\\..\\..\\nina.plugin.livestack.benchmark\\BenchmarkData\\light_b_1.fits";

        public string FlatPath { get; set; } = "..\\..\\..\\..\\nina.plugin.livestack.benchmark\\BenchmarkData\\flat_b.fits";

        public string BiasPath { get; set; } = "..\\..\\..\\..\\nina.plugin.livestack.benchmark\\BenchmarkData\\bias.fits";

        private int frameWidth;
        private int frameHeight;
        private double frameExposureTime;
        private int frameGain;
        private int frameOffset;
        private int frameBinX;
        private int frameBinY;
        private string frameFilter;
        private bool frameIsBayered;

        private CFitsioFITSReader? reader;
        private CalibrationReference? manager;
        private CalibrationManagerSimd? manager2;

        [SetUp]
        public void Setup() {
            var path = Path.GetFullPath(ImagePath);
            var profileMock = new Mock<IProfileService>();
            string outValue = "";
            profileMock.Setup(m => m.ActiveProfile.PluginSettings.TryGetValue(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    out outValue))
                .Returns(true);
            var dataFactoryMock = new Mock<IImageDataFactory>();
            var windowMock = new Mock<IWindowServiceFactory>();
            var plugin = new Livestack(profileMock.Object, dataFactoryMock.Object, windowMock.Object);
            LivestackMediator.RegisterPlugin(plugin);

            reader = new CFitsioFITSReader(ImagePath);

            // Readout metadata
            using (var fits = new CFitsioFITSReader(ImagePath)) {
                var metaData = fits.ReadHeader().ExtractMetaData();

                frameGain = metaData.Camera.Gain;
                frameOffset = metaData.Camera.Offset;
                frameBinX = metaData.Camera.BinX;
                frameBinY = metaData.Camera.BinY;
                frameFilter = metaData.FilterWheel.Filter;
                frameExposureTime = double.IsNaN(metaData.Image.ExposureTime) ? 0 : metaData.Image.ExposureTime;
                frameWidth = fits.Width;
                frameHeight = fits.Height;
            }

            manager = new CalibrationReference();
            manager2 = new CalibrationManagerSimd();

            // Register FLAT Master
            using (var fits = new CFitsioFITSReader(FlatPath)) {
                var metaData = fits.ReadHeader().ExtractMetaData();

                var imageType = metaData.Image.ImageType;
                var gain = metaData.Camera.Gain;
                var offset = metaData.Camera.Offset;
                var binX = metaData.Camera.BinX;
                var binY = metaData.Camera.BinY;
                var filter = metaData.FilterWheel.Filter;
                var exposureTime = double.IsNaN(metaData.Image.ExposureTime) ? 0 : metaData.Image.ExposureTime;
                var width = fits.Width;
                var height = fits.Height;

                var mean = (float)fits.ReadAllPixelsAsFloat().Mean();

                manager.RegisterFlatMaster(new CalibrationFrameMeta(CalibrationFrameType.FLAT, FlatPath, gain, offset, binX, binY, exposureTime, filter, width, height, mean));
                manager2.RegisterFlatMaster(new CalibrationFrameMeta(CalibrationFrameType.FLAT, FlatPath, gain, offset, binX, binY, exposureTime, filter, width, height, mean));
            }

            // Register BIAS Master
            if (!string.IsNullOrEmpty(BiasPath)) {
                using (var fits = new CFitsioFITSReader(BiasPath)) {
                    var metaData = fits.ReadHeader().ExtractMetaData();

                    var imageType = metaData.Image.ImageType;
                    var gain = metaData.Camera.Gain;
                    var offset = metaData.Camera.Offset;
                    var binX = metaData.Camera.BinX;
                    var binY = metaData.Camera.BinY;
                    var filter = metaData.FilterWheel.Filter;
                    var exposureTime = double.IsNaN(metaData.Image.ExposureTime) ? 0 : metaData.Image.ExposureTime;
                    var width = fits.Width;
                    var height = fits.Height;

                    var mean = (float)fits.ReadAllPixelsAsFloat().Mean();

                    manager.RegisterBiasMaster(new CalibrationFrameMeta(CalibrationFrameType.BIAS, BiasPath, gain, offset, binX, binY, exposureTime, filter, width, height, mean));
                    manager2.RegisterBiasMaster(new CalibrationFrameMeta(CalibrationFrameType.BIAS, BiasPath, gain, offset, binX, binY, exposureTime, filter, width, height, mean));
                }
                plugin.UseBiasForLights = true;
            }
        }

        [TearDown]
        public void Teardown() {
            reader?.Dispose();
            manager?.Dispose();
            manager2?.Dispose();
        }

        [Test]
        public void Test1() {
            var baseline = manager.ApplyLightFrameCalibrationInPlace(reader, frameWidth, frameHeight, frameExposureTime, frameGain, frameOffset, frameBinX, frameBinY, frameFilter, frameIsBayered);

            var simd = manager2.ApplyLightFrameCalibrationInPlace(reader, frameWidth, frameHeight, frameExposureTime, frameGain, frameOffset, frameBinX, frameBinY, frameFilter, frameIsBayered);

            FloatAssert.AreEqual(baseline, simd);
        }

        [Test]
        public void CanRoundTripFitsPathWithBracketsAndCurlyBraces() {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"Livestack[Test]{{{Guid.NewGuid():N}}}");
            Directory.CreateDirectory(tempRoot);

            var filePath = Path.Combine(tempRoot, "frame[1]{test}.fits");
            ushort[] source = [0, 16384, 32768, ushort.MaxValue];

            try {
                var writer = new CFitsioFITSExtendedWriter(filePath, source, 2, 2, NINA.Image.FileFormat.FITS.CfitsioNative.COMPRESSION.NOCOMPRESS);
                writer.Close();

                using var fits = new CFitsioFITSReader(filePath);
                var actual = fits.ReadAllPixelsAsFloat();

                Assert.That(fits.Width, Is.EqualTo(2));
                Assert.That(fits.Height, Is.EqualTo(2));
                FloatAssert.AreEqual([0f, 16384f / ushort.MaxValue, 32768f / ushort.MaxValue, 1f], actual, absTol: 1e-6f, relTol: 1e-6f);
            } finally {
                if (File.Exists(filePath)) {
                    File.Delete(filePath);
                }

                if (Directory.Exists(tempRoot)) {
                    Directory.Delete(tempRoot);
                }
            }
        }

        [Test]
        public void ComputeAffineTransformation_RecoversSparseTranslationThroughPublicMatcher() {
            List<Point> reference = AlignmentSafetyTests.CreateCatalog(8, 149);
            double[,] expected = AlignmentSafetyTests.Matrix(tx: 12, ty: -7);
            double[,] actual = ImageTransformer2.Instance.ComputeAffineTransformation(AlignmentSafetyTests.Project(reference, expected), reference);
            AssertAffineClose(expected, actual, matrixTol: 1e-5, translationTol: 0.001);
        }

        [Test]
        public void GetStars_FiltersPoorQualityDetectionsWhenEnoughGoodStarsExist() {
            var transformer = ImageTransformer2.Instance;
            var stars = new List<DetectedStar>();

            for (int i = 0; i < 12; i++) {
                int x = 120 + ((i % 4) * 300);
                int y = 140 + ((i / 4) * 250);
                stars.Add(new DetectedStar {
                    Position = new Point(x, y),
                    BoundingBox = new System.Drawing.Rectangle(x - 5, y - 5, 10, 10),
                    MaxBrightness = 18000 + (i * 250),
                    Background = 600,
                    HFR = 3.1 + ((i % 3) * 0.1)
                });
            }

            var saturated = new DetectedStar {
                Position = new Point(500, 900),
                BoundingBox = new System.Drawing.Rectangle(495, 895, 10, 10),
                MaxBrightness = 65535,
                Background = 500,
                HFR = 3.2
            };
            var edgeStar = new DetectedStar {
                Position = new Point(2, 400),
                BoundingBox = new System.Drawing.Rectangle(0, 395, 10, 10),
                MaxBrightness = 21000,
                Background = 500,
                HFR = 3.2
            };
            var elongatedBox = new DetectedStar {
                Position = new Point(900, 700),
                BoundingBox = new System.Drawing.Rectangle(892, 697, 18, 6),
                MaxBrightness = 19000,
                Background = 500,
                HFR = 3.2
            };
            var bloated = new DetectedStar {
                Position = new Point(1150, 720),
                BoundingBox = new System.Drawing.Rectangle(1144, 714, 12, 12),
                MaxBrightness = 19500,
                Background = 500,
                HFR = 12.5
            };

            stars.AddRange([saturated, edgeStar, elongatedBox, bloated]);

            var selected = transformer.GetStars(stars, 1600, 1200);

            Assert.That(selected, Does.Not.Contain(saturated.Position));
            Assert.That(selected, Does.Not.Contain(edgeStar.Position));
            Assert.That(selected, Does.Contain(elongatedBox.Position));
            Assert.That(selected, Does.Not.Contain(bloated.Position));
            Assert.That(selected.Count, Is.EqualTo(13));
        }

        [Test]
        public void GetStars_UsesDetectedStarsWhenBrightnessMetadataIsSaturated() {
            var transformer = ImageTransformer2.Instance;
            var stars = new List<DetectedStar>();

            for (int i = 0; i < 12; i++) {
                int x = 300 + ((i % 4) * 700);
                int y = 240 + ((i / 4) * 520);
                stars.Add(new DetectedStar {
                    Position = new Point(x, y),
                    BoundingBox = new System.Drawing.Rectangle(x - 5, y - 5, 10, 10),
                    MaxBrightness = 65535,
                    AverageBrightness = 24000,
                    Background = 700,
                    HFR = 2.2 + ((i % 3) * 0.1)
                });
            }

            var selected = transformer.GetStars(stars, 3840, 2160);

            Assert.That(selected.Count, Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void GetStars_UsesDetectedStarsWhenBrightnessMetadataIsNaN() {
            var transformer = ImageTransformer2.Instance;
            var stars = new List<DetectedStar>();

            for (int i = 0; i < 12; i++) {
                int x = 300 + ((i % 4) * 700);
                int y = 240 + ((i / 4) * 520);
                stars.Add(new DetectedStar {
                    Position = new Point(x, y),
                    BoundingBox = new System.Drawing.Rectangle(x - 5, y - 5, 10, 10),
                    MaxBrightness = double.NaN,
                    AverageBrightness = double.NaN,
                    Background = double.NaN,
                    HFR = 2.2 + ((i % 3) * 0.1)
                });
            }

            var selected = transformer.GetStars(stars, 3840, 2160);

            Assert.That(selected.Count, Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void GetStars_KeepsSpatialSelectionStableWhenBrightnessRanksChange() {
            var transformer = ImageTransformer2.Instance;
            var firstFrameStars = CreateDenseGridStars(brightnessVariant: 0);
            var secondFrameStars = CreateDenseGridStars(brightnessVariant: 1);

            var firstSelection = transformer.GetStars(firstFrameStars, 600, 600);
            var secondSelection = transformer.GetStars(secondFrameStars, 600, 600);

            var firstSelectionPositions = firstSelection.Select(x => (x.X, x.Y)).ToHashSet();
            int overlap = secondSelection.Count(x => firstSelectionPositions.Contains((x.X, x.Y)));

            Assert.That(firstSelection.Count, Is.EqualTo(firstFrameStars.Count));
            Assert.That(secondSelection.Count, Is.EqualTo(secondFrameStars.Count));
            Assert.That(overlap, Is.GreaterThanOrEqualTo(firstFrameStars.Count - 4));
        }

        [Test]
        public void GetStars_KeepsPhysicalSelectionStableWhenFrameShifts() {
            var transformer = ImageTransformer2.Instance;
            const int shiftX = 45;
            const int shiftY = 38;
            var firstFrameStars = CreateDenseGridStars(brightnessVariant: 0);
            var shiftedFrameStars = CreateDenseGridStars(brightnessVariant: 1, shiftX, shiftY);

            var firstSelection = transformer.GetStars(firstFrameStars, 700, 700);
            var shiftedSelection = transformer.GetStars(shiftedFrameStars, 700, 700);

            var firstSelectionPositions = firstSelection.Select(x => (x.X, x.Y)).ToHashSet();
            int overlap = shiftedSelection.Count(x => firstSelectionPositions.Contains((x.X - shiftX, x.Y - shiftY)));

            Assert.That(firstSelection.Count, Is.EqualTo(firstFrameStars.Count));
            Assert.That(shiftedSelection.Count, Is.EqualTo(shiftedFrameStars.Count));
            Assert.That(overlap, Is.GreaterThanOrEqualTo(firstFrameStars.Count - 4));
        }

        [Test]
        public void ComputeAffineTransformation_RecoversKnownTransformWithOutliers() {
            var transformer = ImageTransformer2.Instance;
            var referenceStars = CreateSyntheticReferenceStars();

            double[,] expected = new double[3, 3] {
                { 0.9985, 0.0175, -12.75 },
                { -0.0145, 1.0015, 9.25 },
                { 0.0, 0.0, 1.0 }
            };

            var sourceStars = ApplyAffine(referenceStars, expected, seed: 41, jitter: 0.35f);
            sourceStars.AddRange(new[] {
                new Point(75, 70),
                new Point(1490, 100),
                new Point(1510, 1120),
                new Point(210, 1050),
                new Point(820, 610),
                new Point(1280, 520)
            });
            Shuffle(sourceStars, seed: 99);

            var actual = transformer.ComputeAffineTransformation(sourceStars, referenceStars);

            AssertAffineClose(expected, actual, matrixTol: 0.02, translationTol: 1.2);
            Assert.That(ComputeMedianResidual(referenceStars, ApplyAffine(referenceStars, expected), actual), Is.LessThan(1.2));
        }

        [Test]
        public void ComputeAffineTransformation_RecoversMeridianFlipLikeRotation() {
            var transformer = ImageTransformer2.Instance;
            var referenceStars = CreateSyntheticReferenceStars();

            double[,] expected = new double[3, 3] {
                { -1.0, 0.0, 1600.0 },
                { 0.0, -1.0, 1200.0 },
                { 0.0, 0.0, 1.0 }
            };

            var sourceStars = ApplyAffine(referenceStars, expected, seed: 11, jitter: 0.2f);
            sourceStars.AddRange(new[] {
                new Point(40, 100),
                new Point(1540, 180),
                new Point(1470, 1090),
                new Point(300, 1110)
            });

            var actual = transformer.ComputeAffineTransformation(sourceStars, referenceStars);

            AssertAffineClose(expected, actual, matrixTol: 0.02, translationTol: 1.0);
            Assert.That(transformer.IsFlippedImage(actual), Is.True);
        }

        [Test]
        public void ComputeAffineTransformation_RecoversKnownTransformInSparseField() {
            var transformer = ImageTransformer2.Instance;
            var referenceStars = new List<Point> {
                new Point(120, 90),
                new Point(430, 130),
                new Point(760, 260),
                new Point(260, 510),
                new Point(640, 620),
                new Point(1040, 420),
                new Point(1180, 760),
                new Point(350, 850)
            };

            double[,] expected = new double[3, 3] {
                { 1.002, -0.023, 31.5 },
                { 0.019, 0.998, -18.25 },
                { 0.0, 0.0, 1.0 }
            };

            var sourceStars = ApplyAffine(referenceStars, expected, seed: 7, jitter: 0.18f);
            sourceStars.AddRange(new[] {
                new Point(85, 810),
                new Point(1160, 120),
                new Point(900, 930)
            });
            Shuffle(sourceStars, seed: 17);

            var actual = transformer.ComputeAffineTransformation(sourceStars, referenceStars);

            AssertAffineClose(expected, actual, matrixTol: 0.015, translationTol: 0.9);
            Assert.That(ComputeMedianResidual(referenceStars, ApplyAffine(referenceStars, expected), actual), Is.LessThan(0.9));
        }

        [Test]
        public void ComputeAffineTransformation_RecoversKnownTransformInVeryDenseField() {
            var transformer = ImageTransformer2.Instance;
            var referenceStars = CreateRandomStarField(count: 3000, width: 4200, height: 2800, seed: 91);

            double[,] expected = new double[3, 3] {
                { 1.0012, 0.0125, -42.0 },
                { -0.0105, 0.9991, 27.5 },
                { 0.0, 0.0, 1.0 }
            };

            var sourceStars = ApplyAffine(referenceStars, expected, seed: 123, jitter: 0.22f);
            sourceStars.AddRange(CreateRandomStarField(count: 350, width: 4200, height: 2800, seed: 404));
            Shuffle(sourceStars, seed: 812);
            Shuffle(referenceStars, seed: 613);

            var actual = transformer.ComputeAffineTransformation(sourceStars, referenceStars);

            AssertAffineClose(expected, actual, matrixTol: 0.01, translationTol: 1.0);
            Assert.That(ComputeMedianResidual(referenceStars, ApplyAffine(referenceStars, expected), actual), Is.LessThan(0.9));
        }

        [Test]
        public void ComputeAffineTransformation_RecoversDenseMeridianFlipLikeRotation() {
            var transformer = ImageTransformer2.Instance;
            var referenceStars = CreateRandomStarField(count: 3000, width: 4200, height: 2800, seed: 151);

            double[,] expected = new double[3, 3] {
                { -1.0, 0.0, 4200.0 },
                { 0.0, -1.0, 2800.0 },
                { 0.0, 0.0, 1.0 }
            };

            var sourceStars = ApplyAffine(referenceStars, expected, seed: 227, jitter: 0.22f);
            sourceStars.AddRange(CreateRandomStarField(count: 350, width: 4200, height: 2800, seed: 808));
            Shuffle(sourceStars, seed: 331);
            Shuffle(referenceStars, seed: 977);

            var actual = transformer.ComputeAffineTransformation(sourceStars, referenceStars);

            AssertAffineClose(expected, actual, matrixTol: 0.01, translationTol: 1.0);
            Assert.That(ComputeMedianResidual(referenceStars, ApplyAffine(referenceStars, expected), actual), Is.LessThan(0.9));
            Assert.That(transformer.IsFlippedImage(actual), Is.True);
        }

        [Test]
        public void NeedsStarDetection_RedetectsWhenDetectedStarsIsZero() {
            var needsStarDetectionMethod = typeof(LivestackDockable).GetMethod("NeedsStarDetection", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(needsStarDetectionMethod, Is.Not.Null);

            Assert.That((bool)needsStarDetectionMethod!.Invoke(null, new object?[] { null })!, Is.True);
            Assert.That((bool)needsStarDetectionMethod.Invoke(null, new object[] { new StarDetectionAnalysis { DetectedStars = -1 } })!, Is.True);
            Assert.That((bool)needsStarDetectionMethod.Invoke(null, new object[] { new StarDetectionAnalysis { DetectedStars = 0 } })!, Is.True);
            Assert.That((bool)needsStarDetectionMethod.Invoke(null, new object[] { new StarDetectionAnalysis { DetectedStars = 1 } })!, Is.False);
        }

        [Test]
        public void UniformCoverageStackMatchesScalarReference() {
            float[] originalStack = Enumerable.Range(0, 37).Select(i => ((i * 17) % 101) / 100f).ToArray();
            float[] image = Enumerable.Range(0, 37).Select(i => ((i * 29) % 97) / 96f).ToArray();
            float[] stack = (float[])originalStack.Clone();

            ImageTransformer2.Instance.ApplyAffineTransformationAndStack(image, stack, stackImageCount: 7,
                width: 37, height: 1, AlignmentSafetyTests.Matrix());

            float[] expected = SequentialStackReference(image, originalStack, stackImageCount: 7);
            FloatAssert.AreEqual(expected, stack);
        }

        [Test]
        public void ApplyAffineTransformation_FloatMatchesReferenceImplementation() {
            var transformer = ImageTransformer2.Instance;
            const int width = 320;
            const int height = 240;

            float[] source = Enumerable.Range(0, width * height)
                .Select(i => ((i * 73) % 1021) / 1020f)
                .ToArray();

            foreach (double[,] matrix in GetAffineTestMatrices()) {
                foreach (bool flipped in new[] { false, true }) {
                    float[] expected = ApplyAffineTransformationReference(source, width, height, matrix, flipped);
                    float[] actual = transformer.ApplyAffineTransformation(source, width, height, matrix, flipped);
                    FloatAssert.AreEqual(expected, actual, absTol: 1e-6f, relTol: 1e-6f);

                    float[] destination = Enumerable.Repeat(1f, width * height).ToArray();
                    transformer.ApplyAffineTransformationInto(source, destination, width, height, matrix, flipped);
                    FloatAssert.AreEqual(expected, destination, absTol: 1e-6f, relTol: 1e-6f);

                    float[] originalStack = Enumerable.Range(0, width * height)
                        .Select(i => ((i * 97) % 997) / 996f)
                        .ToArray();
                    float[] stack = (float[])originalStack.Clone();
                    transformer.ApplyAffineTransformationAndStack(source, stack, stackImageCount: 5, width, height, matrix, flipped);

                    float[] expectedStack = StackWithCoverageReference(expected, originalStack, 5, width, height, matrix, flipped);
                    FloatAssert.AreEqual(expectedStack, stack, absTol: 1e-6f, relTol: 1e-6f);
                }
            }
        }

        [Test]
        public void ApplyAffineTransformation_UShortVariantsMatchReferenceImplementation() {
            var transformer = ImageTransformer2.Instance;
            const int width = 320;
            const int height = 240;

            ushort[] ushortSource = Enumerable.Range(0, width * height)
                .Select(i => (ushort)((i * 131) % ushort.MaxValue))
                .ToArray();
            float[] floatSource = ushortSource.Select(x => x / (float)ushort.MaxValue).ToArray();

            foreach (double[,] matrix in GetAffineTestMatrices()) {
                foreach (bool flipped in new[] { false, true }) {
                    float[] expectedFloat = ApplyAffineTransformationReference(ushortSource, width, height, matrix, flipped);
                    float[] actualFloat = transformer.ApplyAffineTransformation(ushortSource, width, height, matrix, flipped);
                    FloatAssert.AreEqual(expectedFloat, actualFloat, absTol: 1e-6f, relTol: 1e-6f);

                    float[] destinationFloat = Enumerable.Repeat(1f, width * height).ToArray();
                    transformer.ApplyAffineTransformationInto(ushortSource, destinationFloat, width, height, matrix, flipped);
                    FloatAssert.AreEqual(expectedFloat, destinationFloat, absTol: 1e-6f, relTol: 1e-6f);

                    float[] originalStack = Enumerable.Range(0, width * height)
                        .Select(i => ((i * 83) % 991) / 990f)
                        .ToArray();
                    float[] stack = (float[])originalStack.Clone();
                    transformer.ApplyAffineTransformationAndStack(ushortSource, stack, stackImageCount: 3, width, height, matrix, flipped);

                    float[] expectedStack = StackWithCoverageReference(expectedFloat, originalStack, 3, width, height, matrix, flipped);
                    FloatAssert.AreEqual(expectedStack, stack, absTol: 1e-6f, relTol: 1e-6f);

                    ushort[] expectedUshort = ApplyAffineTransformationAsUShortReference(floatSource, width, height, matrix, flipped);
                    ushort[] actualUshort = transformer.ApplyAffineTransformationAsUshort(floatSource, width, height, matrix, flipped);
                    Assert.That(actualUshort, Is.EqualTo(expectedUshort).Within(1));

                    ushort[] destinationUshort = Enumerable.Repeat((ushort)123, width * height).ToArray();
                    transformer.ApplyAffineTransformationAsUshortInto(floatSource, destinationUshort, width, height, matrix, flipped);
                    Assert.That(destinationUshort, Is.EqualTo(expectedUshort).Within(1));
                }
            }
        }

        [Test]
        public void PercentileClipping_MatchesReferenceImplementation() {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"LivestackPercentile[{Guid.NewGuid():N}]");
            Directory.CreateDirectory(tempRoot);

            string[] filePaths = [
                Path.Combine(tempRoot, "flat1.fits"),
                Path.Combine(tempRoot, "flat2.fits"),
                Path.Combine(tempRoot, "flat3.fits")
            ];

            float[][] sourceImages = [
                [1f, 2f, 3f, 4f],
                [2f, 4f, 6f, 8f],
                [40f, 8f, 12f, 16f]
            ];

            float[] medians = [1f, 2f, 4f];

            try {
                for (int i = 0; i < filePaths.Length; i++) {
                    var writer = new CFitsioFITSExtendedWriter(filePaths[i], sourceImages[i], 2, 2, NINA.Image.FileFormat.FITS.CfitsioNative.COMPRESSION.NOCOMPRESS);
                    writer.AddHeader("MEDIAN", medians[i], "");
                    writer.Close();
                }

                using var fits1 = new CFitsioFITSReader(filePaths[0]);
                using var fits2 = new CFitsioFITSReader(filePaths[1]);
                using var fits3 = new CFitsioFITSReader(filePaths[2]);

                float[] actual = ImageMath.Instance.PercentileClipping([fits1, fits2, fits3], 0.2, 0.1);
                float[] expected = PercentileClippingReference(sourceImages, medians, 0.2f, 0.1f, 2, 2);

                FloatAssert.AreEqual(expected, actual, absTol: 0f, relTol: 0f);
            } finally {
                foreach (string filePath in filePaths) {
                    if (File.Exists(filePath)) {
                        File.Delete(filePath);
                    }
                }

                if (Directory.Exists(tempRoot)) {
                    Directory.Delete(tempRoot);
                }
            }
        }

        private static List<Point> CreateSyntheticReferenceStars() {
            return new List<Point> {
                new Point(120, 140),
                new Point(420, 150),
                new Point(760, 130),
                new Point(1110, 180),
                new Point(1420, 160),
                new Point(160, 360),
                new Point(510, 390),
                new Point(840, 345),
                new Point(1190, 410),
                new Point(1450, 360),
                new Point(110, 650),
                new Point(470, 700),
                new Point(760, 660),
                new Point(1090, 720),
                new Point(1430, 690),
                new Point(180, 980),
                new Point(520, 1020),
                new Point(910, 960),
                new Point(1260, 1040),
                new Point(1460, 980)
            };
        }

        private static List<DetectedStar> CreateDenseGridStars(int brightnessVariant, int shiftX = 0, int shiftY = 0) {
            const int cellSize = 100;
            var localPositions = new[] {
                (X: 50, Y: 50),
                (X: 25, Y: 25),
                (X: 75, Y: 75),
                (X: 20, Y: 80)
            };

            var stars = new List<DetectedStar>();
            for (int cellY = 0; cellY < 6; cellY++) {
                for (int cellX = 0; cellX < 6; cellX++) {
                    for (int i = 0; i < localPositions.Length; i++) {
                        var localPosition = localPositions[i];
                        int x = (cellX * cellSize) + localPosition.X + shiftX;
                        int y = (cellY * cellSize) + localPosition.Y + shiftY;
                        bool brightInThisVariant = brightnessVariant == 0
                            ? i < 2
                            : i >= 2;

                        stars.Add(new DetectedStar {
                            Position = new Point(x, y),
                            BoundingBox = new System.Drawing.Rectangle(x - 4, y - 4, 8, 8),
                            MaxBrightness = brightInThisVariant ? 30000 - (i * 100) : 12000 - (i * 100),
                            Background = 500,
                            HFR = 3.0
                        });
                    }
                }
            }

            return stars;
        }

        private static List<Point> CreateRandomStarField(int count, int width, int height, int seed) {
            Random random = new Random(seed);
            var stars = new List<Point>(count);

            for (int i = 0; i < count; i++) {
                float x = 20f + ((float)random.NextDouble() * (width - 40f));
                float y = 20f + ((float)random.NextDouble() * (height - 40f));
                stars.Add(new Point(x, y));
            }

            return stars;
        }

        private static List<Point> ApplyAffine(IEnumerable<Point> points, double[,] matrix, int seed = 0, float jitter = 0f) {
            Random random = new Random(seed);
            var transformed = new List<Point>();

            foreach (var point in points) {
                float x = (float)((matrix[0, 0] * point.X) + (matrix[0, 1] * point.Y) + matrix[0, 2]);
                float y = (float)((matrix[1, 0] * point.X) + (matrix[1, 1] * point.Y) + matrix[1, 2]);

                if (jitter > 0f) {
                    x += (((float)random.NextDouble() * 2f) - 1f) * jitter;
                    y += (((float)random.NextDouble() * 2f) - 1f) * jitter;
                }

                transformed.Add(new Point(x, y));
            }

            return transformed;
        }

        private static void Shuffle<T>(IList<T> list, int seed) {
            Random random = new Random(seed);
            for (int i = list.Count - 1; i > 0; i--) {
                int swapIndex = random.Next(i + 1);
                (list[i], list[swapIndex]) = (list[swapIndex], list[i]);
            }
        }

        private static void AssertAffineClose(double[,] expected, double[,] actual, double matrixTol, double translationTol) {
            Assert.That(actual[0, 0], Is.EqualTo(expected[0, 0]).Within(matrixTol));
            Assert.That(actual[0, 1], Is.EqualTo(expected[0, 1]).Within(matrixTol));
            Assert.That(actual[1, 0], Is.EqualTo(expected[1, 0]).Within(matrixTol));
            Assert.That(actual[1, 1], Is.EqualTo(expected[1, 1]).Within(matrixTol));
            Assert.That(actual[0, 2], Is.EqualTo(expected[0, 2]).Within(translationTol));
            Assert.That(actual[1, 2], Is.EqualTo(expected[1, 2]).Within(translationTol));
        }

        private static double ComputeMedianResidual(IReadOnlyList<Point> referenceStars, IReadOnlyList<Point> sourceStars, double[,] matrix) {
            var residuals = new double[referenceStars.Count];
            for (int i = 0; i < referenceStars.Count; i++) {
                double projectedX = (matrix[0, 0] * referenceStars[i].X) + (matrix[0, 1] * referenceStars[i].Y) + matrix[0, 2];
                double projectedY = (matrix[1, 0] * referenceStars[i].X) + (matrix[1, 1] * referenceStars[i].Y) + matrix[1, 2];
                double dx = projectedX - sourceStars[i].X;
                double dy = projectedY - sourceStars[i].Y;
                residuals[i] = Math.Sqrt((dx * dx) + (dy * dy));
            }

            return residuals.Median();
        }

        private static float[] SequentialStackReference(float[] image, float[] stack, int stackImageCount) {
            float[] result = (float[])stack.Clone();
            float nextCount = stackImageCount + 1f;
            for (int i = 0; i < result.Length; i++) {
                result[i] = (stackImageCount * result[i] + image[i]) / nextCount;
            }
            return result;
        }

        private static IEnumerable<double[,]> GetAffineTestMatrices() {
            yield return new double[3, 3] {
                { 1.0, 0.0, 2.75 },
                { 0.0, 1.0, -3.5 },
                { 0.0, 0.0, 1.0 }
            };
            yield return new double[3, 3] {
                { 0.9875, 0.04375, -1.8 },
                { -0.03125, 1.0125, 4.2 },
                { 0.0, 0.0, 1.0 }
            };
            yield return new double[3, 3] {
                { 1.0009765625, -0.0625, 6.125 },
                { 0.0546875, 0.998046875, -5.875 },
                { 0.0, 0.0, 1.0 }
            };
        }

        // Deliberately scalar mathematical reference: four bilinear weights and an explicit
        // validity mask, independent of the fused production accumulator.
        private static float[] ApplyAffineTransformationReference(float[] source, int width, int height, double[,] matrix, bool flipped) {
            float[] output = new float[source.Length];
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    double u = matrix[0, 0] * x + matrix[0, 1] * y + matrix[0, 2];
                    double v = matrix[1, 0] * x + matrix[1, 1] * y + matrix[1, 2];
                    if (flipped) {
                        u = width - 1 - u;
                        v = height - 1 - v;
                    }
                    if (u < 0 || v < 0 || u > width - 1 || v > height - 1) {
                        continue;
                    }
                    int left = (int)Math.Floor(u), top = (int)Math.Floor(v);
                    int right = Math.Min(left + 1, width - 1), bottom = Math.Min(top + 1, height - 1);
                    double horizontal = u - left, vertical = v - top;
                    output[y * width + x] = (float)(source[top * width + left] * (1 - horizontal) * (1 - vertical)
                        + source[top * width + right] * horizontal * (1 - vertical)
                        + source[bottom * width + left] * (1 - horizontal) * vertical
                        + source[bottom * width + right] * horizontal * vertical);
                }
            }
            return output;
        }

        private static float[] ApplyAffineTransformationReference(ushort[] source, int width, int height, double[,] matrix, bool flipped) {
            return ApplyAffineTransformationReference(source.Select(value => value / (float)ushort.MaxValue).ToArray(), width, height, matrix, flipped);
        }

        private static ushort[] ApplyAffineTransformationAsUShortReference(float[] source, int width, int height, double[,] matrix, bool flipped) {
            return ApplyAffineTransformationReference(source, width, height, matrix, flipped).Select(value => (ushort)Math.Clamp(value * ushort.MaxValue, 0, ushort.MaxValue)).ToArray();
        }

        private static float[] StackWithCoverageReference(float[] transformed, float[] original, int count, int width, int height, double[,] matrix, bool flipped) {
            float[] valid = ApplyAffineTransformationReference(Enumerable.Repeat(1f, original.Length).ToArray(), width, height, matrix, flipped);
            return transformed.Select((value, i) => valid[i] == 0 ? original[i] : (count * original[i] + value) / (count + 1f)).ToArray();
        }

        private static float[] PercentileClippingReference(IReadOnlyList<float[]> images, IReadOnlyList<float> medians, float lowerPercentile, float upperPercentile, int width, int height) {
            int imageCount = images.Count;
            float[] normalization = new float[imageCount];
            float referenceMedian = medians[0];
            for (int i = 0; i < imageCount; i++) {
                normalization[i] = referenceMedian / medians[i];
            }

            float[] result = new float[width * height];
            float[] pixelValues = new float[imageCount];

            for (int pixelIndex = 0; pixelIndex < result.Length; pixelIndex++) {
                for (int i = 0; i < imageCount; i++) {
                    pixelValues[i] = images[i][pixelIndex] * normalization[i];
                }

                Array.Sort(pixelValues);
                float median = imageCount % 2 == 1
                    ? pixelValues[imageCount / 2]
                    : (pixelValues[(imageCount / 2) - 1] + pixelValues[imageCount / 2]) * 0.5f;

                float lowerLimit = median - (median * lowerPercentile);
                float upperLimit = median + (median * upperPercentile);
                float minAccepted = Math.Min(lowerLimit, upperLimit);
                float maxAccepted = Math.Max(lowerLimit, upperLimit);

                float sum = 0f;
                int count = 0;
                for (int i = 0; i < imageCount; i++) {
                    float pixel = pixelValues[i];
                    if (pixel >= minAccepted && pixel <= maxAccepted) {
                        sum += pixel;
                        count++;
                    }
                }

                result[pixelIndex] = count == 0 ? median : sum / count;
            }

            return result;
        }
    }

    public static class FloatAssert {

        public static void AreEqual(
            ReadOnlySpan<float> expected,
            ReadOnlySpan<float> actual,
            float absTol = 1e-6f,
            float relTol = 1e-6f) {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));

            for (int i = 0; i < expected.Length; i++) {
                float e = expected[i];
                float a = actual[i];

                float abs = MathF.Abs(a - e);
                float rel = abs / MathF.Max(MathF.Abs(e), 1e-20f);

                if (abs > absTol && rel > relTol) {
                    Assert.Fail(
                        $"Mismatch at index {i}: expected={e}, actual={a}, abs={abs}, rel={rel}");
                }
            }
        }
    }
}
