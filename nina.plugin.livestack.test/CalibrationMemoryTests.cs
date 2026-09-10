using Moq;
using NINA.Core.Utility.WindowService;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.TestSupport;
using NINA.Profile.Interfaces;

namespace nina.plugin.livestack.test {
    [NonParallelizable]
    public class CalibrationMemoryTests {
        private string directory = null!;
        private Livestack plugin = null!;
        private const int Width = 257, Height = 129;

        [SetUp]
        public void Setup() {
            Mock<IProfileService> profile = new() { DefaultValue = DefaultValue.Mock };
            plugin = new Livestack(profile.Object, Mock.Of<IImageDataFactory>(), Mock.Of<IWindowServiceFactory>()) { UseBiasForLights = true };
            directory = Path.Combine(Path.GetTempPath(), "LivestackMemory-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Write("light", i => 0.3f + 0.3f * i / (Width * Height));
            Write("bias", i => 0.01f + (i % Width) * 0.00001f);
            Write("dark", i => 0.02f + (i / Width) * 0.00001f);
            Write("flat", i => 0.7f + (i % Width) * 0.0001f);
        }

        [TearDown]
        public async Task Teardown() {
            await plugin.Teardown();
            Directory.Delete(directory, true);
        }

        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, true, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        public void StreamingAndCachedIntoPathsMatchBaselineAcrossRepeatedReads(bool flat, bool cached, bool omitBias) {
            using CalibrationReference baseline = new();
            using CalibrationManagerSimd optimized = new(cached);
            Register(baseline, omitBias);
            Register(optimized, omitBias);
            using CFitsioFITSReader reader = new(Path.Combine(directory, "light.fits"));
            float[] expected = flat
                ? baseline.ApplyFlatFrameCalibrationInPlace(reader, Width, Height, 60, 100, 10, "L", false)
                : baseline.ApplyLightFrameCalibrationInPlace(reader, Width, Height, 60, 100, 10, "L", false);
            float[] actual = Enumerable.Repeat(float.NaN, Width * Height).ToArray();
            for (int pass = 0; pass < 2; pass++) {
                Array.Fill(actual, float.NaN);
                if (flat) {
                    optimized.ApplyFlatFrameCalibrationInto(reader, actual, Width, Height, 60, 100, 10, "L", false);
                } else {
                    optimized.ApplyLightFrameCalibrationInto(reader, actual, Width, Height, 60, 100, 10, "L", false);
                }
                FloatAssert.AreEqual(expected, actual);
            }
        }

        [Test]
        public void ShortRowsMatchBaseline([Values(1, 3)] int width, [Values] bool flat, [Values] bool cached) {
            const int height = 3;
            foreach (string name in new[] { "light", "bias", "dark", "flat" }) {
                File.Delete(Path.Combine(directory, name + ".fits"));
            }
            Write("light", i => new[] { 0.005f, 0.45f, 1.4f }[i % 3], width, height);
            Write("bias", _ => 0.01f, width, height);
            Write("dark", _ => 0.02f, width, height);
            Write("flat", i => 0.5f + (i % 3) * 0.1f, width, height);
            using CalibrationReference baseline = new();
            using CalibrationManagerSimd optimized = new(cached);
            Register(baseline, false, width, height);
            Register(optimized, false, width, height);
            using CFitsioFITSReader reader = new(Path.Combine(directory, "light.fits"));
            float[] expected = flat
                ? baseline.ApplyFlatFrameCalibrationInPlace(reader, width, height, 60, 100, 10, "L", false)
                : baseline.ApplyLightFrameCalibrationInPlace(reader, width, height, 60, 100, 10, "L", false);
            float[] actual = new float[width * height];
            if (flat) {
                optimized.ApplyFlatFrameCalibrationInto(reader, actual, width, height, 60, 100, 10, "L", false);
            } else {
                optimized.ApplyLightFrameCalibrationInto(reader, actual, width, height, 60, 100, 10, "L", false);
            }
            FloatAssert.AreEqual(expected, actual);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CancellationAndWrongDimensionsDoNotWriteDestination(bool flat) {
            using CalibrationManagerSimd manager = new();
            using CFitsioFITSReader reader = new(Path.Combine(directory, "light.fits"));
            float[] pixels = Enumerable.Repeat(0.9f, Width * Height).ToArray();
            Action<int, int, CancellationToken> apply = (width, height, token) => {
                if (flat) {
                    manager.ApplyFlatFrameCalibrationInto(reader, pixels, width, height, 60, 100, 10, "L", false, token);
                } else {
                    manager.ApplyLightFrameCalibrationInto(reader, pixels, width, height, 60, 100, 10, "L", false, token);
                }
            };
            Assert.Throws<OperationCanceledException>(() => apply(Width, Height, new CancellationToken(true)));
            Assert.Throws<ArgumentException>(() => apply(Height, Width, CancellationToken.None));
            Assert.That(pixels, Is.All.EqualTo(0.9f));
        }

        [Test]
        public void StreamingCalibrationDoesNotAllocateFullMasterImages() {
            using CFitsioFITSReader reader = new(Path.Combine(directory, "light.fits"));
            float[] output = new float[Width * Height];
            Apply(); // Warm native calls, logging and the shared row pool.
            long start = GC.GetAllocatedBytesForCurrentThread();
            Apply();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.That(allocated, Is.LessThan(Width * Height * sizeof(float)), "Three masters should require rows, not three image allocations.");
            void Apply() {
                using CalibrationManagerSimd manager = new();
                Register(manager, false);
                manager.ApplyLightFrameCalibrationInto(reader, output, Width, Height, 60, 100, 10, "L", false);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MasterReadsHandleBackwardRowsAndDisposeExactlyOnce(bool cached) {
            using CalibrationManagerSimd.CalibrationMaster master = new(Meta("bias", CalibrationFrameType.BIAS), cached);
            float[] first = master.ReadPixelRow(0).ToArray();
            master.ReadPixelRow(Height - 1);
            Assert.That(master.ReadPixelRow(0).ToArray(), Is.EqualTo(first));
            master.Dispose();
            master.Dispose();
            Assert.Throws<ObjectDisposedException>(() => master.ReadPixelRow(0));
        }

        [Test]
        public void DarkWildcardsPreserveExposureMatching([Values(100, -1)] int gain, [Values(10, -1)] int offset,
            [Values(2, 60, 120)] double exposure, [Values] bool flat) {
            using CalibrationManagerSimd manager = new();
            CalibrationFrameMeta dark = Meta("dark", CalibrationFrameType.DARK);
            dark.Gain = gain;
            dark.Offset = offset;
            manager.RegisterDarkMaster(dark);
            using CFitsioFITSReader reader = new(Path.Combine(directory, "light.fits"));
            float[] result = flat
                ? manager.ApplyFlatFrameCalibrationInPlace(reader, Width, Height, exposure, 100, 10, "L", false)
                : manager.ApplyLightFrameCalibrationInPlace(reader, Width, Height, exposure, 100, 10, "L", false);
            Assert.That(result[0], Is.EqualTo(exposure == 60 ? 0.28f : 0.3f).Within(1e-6));
        }

        private void Register(ICalibrationManager manager, bool omitBias, int width = Width, int height = Height) {
            if (!omitBias) manager.RegisterBiasMaster(Meta("bias", CalibrationFrameType.BIAS, width, height));
            manager.RegisterDarkMaster(Meta("dark", CalibrationFrameType.DARK, width, height));
            manager.RegisterFlatMaster(Meta("flat", CalibrationFrameType.FLAT, width, height));
        }

        private CalibrationFrameMeta Meta(string name, CalibrationFrameType type, int width = Width, int height = Height) => new(type, Path.Combine(directory, name + ".fits"), 100, 10, 60, "L", width, height, 0.72f);

        private void Write(string name, Func<int, float> value, int width = Width, int height = Height) {
            CFitsioFITSExtendedWriter writer = new(Path.Combine(directory, name + ".fits"), Enumerable.Range(0, width * height).Select(value).ToArray(), width, height);
            writer.Close();
        }
    }
}
