using NINA.Image.FileFormat.FITS;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.LivestackDockables;
using System.Diagnostics;
using System.Reflection;
using System.Collections;

namespace nina.plugin.livestack.test {
    [NonParallelizable]
    [Apartment(ApartmentState.STA)]
    public class CalibrationReuseTests {
        private const int Width = 257, Height = 129;
        private static readonly MethodInfo calibrate = typeof(LivestackDockable).GetMethod("CalibrateFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;

        [Test]
        public async Task RepeatedCompressedCalibrationKeepsOnlyStreamingReaders() {
            await using CaptureTestContext host = new();
            LiveStackItem item = Setup(host);
            using (ImageBufferLease warm = Apply(host, item)) Assert.That(warm.Buffer[0], Is.EqualTo(0.7f).Within(1e-4));
            CalibrationManagerSimd.CalibrationMaster reader = CachedReader(host);
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < 20; i++) {
                using ImageBufferLease pixels = Apply(host, item);
                Assert.That(pixels.Buffer[0], Is.EqualTo(0.7f).Within(1e-4));
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            TestContext.WriteLine($"Twenty warmed calibrations: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2} ms, {allocated} allocated bytes.");
            Assert.That(CachedReader(host), Is.SameAs(reader), "Even compressed masters must reuse their reader.");
            Assert.That(allocated / 20, Is.LessThan(Width * Height), "Per-frame overhead must stay well below one float image.");
            await host.Dockable.StopAsync();
            Assert.Throws<ObjectDisposedException>(() => reader.ReadPixelRow(0));
        }

        [Test]
        public async Task LibraryAndFileChangesAreObservedAndStopReleasesReaders() {
            await using CaptureTestContext host = new();
            LiveStackItem item = Setup(host);
            using (ImageBufferLease pixels = Apply(host, item)) Assert.That(pixels.Buffer[0], Is.EqualTo(0.7f).Within(1e-4));
            CalibrationManagerSimd.CalibrationMaster original = CachedReader(host);
            CalibrationFrameMeta master = LivestackMediator.CalibrationVM.BiasLibrary.Single();
            File.Delete(master.Path);
            Write(master.Path, 0.2f, compressed: true);
            File.SetLastWriteTimeUtc(master.Path, DateTime.UtcNow.AddSeconds(2));
            using (ImageBufferLease pixels = Apply(host, item)) Assert.That(pixels.Buffer[0], Is.EqualTo(0.6f).Within(1e-4));
            Assert.Throws<ObjectDisposedException>(() => original.ReadPixelRow(0));
            host.Plugin.UseBiasForLights = false;
            using (ImageBufferLease pixels = Apply(host, item)) Assert.That(pixels.Buffer[0], Is.EqualTo(0.8f).Within(1e-4));
            host.Plugin.UseBiasForLights = true;
            using (ImageBufferLease pixels = Apply(host, item)) Assert.That(pixels.Buffer[0], Is.EqualTo(0.6f).Within(1e-4));
            CalibrationManagerSimd.CalibrationMaster beforeMetadataChange = CachedReader(host);
            master.Mean = 0.2f;
            using (ImageBufferLease pixels = Apply(host, item)) Assert.That(pixels.Buffer[0], Is.EqualTo(0.6f).Within(1e-4));
            Assert.Throws<ObjectDisposedException>(() => beforeMetadataChange.ReadPixelRow(0));
            master.Gain = 200;
            using (ImageBufferLease pixels = Apply(host, item)) Assert.That(pixels.Buffer[0], Is.EqualTo(0.8f).Within(1e-4));
            master.Gain = 100;
            using (ImageBufferLease pixels = Apply(host, item)) Assert.That(pixels.Buffer[0], Is.EqualTo(0.6f).Within(1e-4));
            ChangeLibrary(() => LivestackMediator.CalibrationVM.BiasLibrary.Clear());
            using (ImageBufferLease pixels = Apply(host, item)) Assert.That(pixels.Buffer[0], Is.EqualTo(0.8f).Within(1e-4));
            ChangeLibrary(() => LivestackMediator.CalibrationVM.BiasLibrary.Add(master));
            using (ImageBufferLease pixels = Apply(host, item)) Assert.That(pixels.Buffer[0], Is.EqualTo(0.6f).Within(1e-4));
            await host.Dockable.StopAsync();
            using FileStream exclusive = File.Open(master.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        private static LiveStackItem Setup(CaptureTestContext host) {
            string light = Path.Combine(host.DirectoryPath, "light.fits"), bias = Path.Combine(host.DirectoryPath, "bias.fits");
            Write(light, 0.8f, compressed: false);
            Write(bias, 0.1f, compressed: true);
            ChangeLibrary(() => LivestackMediator.CalibrationVM.BiasLibrary.Add(new CalibrationFrameMeta(CalibrationFrameType.BIAS, bias, 100, 10, -1, -1, 0, "L", Width, Height, 0.1f)));
            var image = host.Image().Object;
            return new LiveStackItem(light, "target", "L", 60, 100, 10, -1, -1, Width, Height, 16, false, image.StarDetectionAnalysis, image.MetaData);
        }

        private static void Write(string path, float value, bool compressed) {
            CFitsioFITSExtendedWriter writer = new(path, Enumerable.Repeat(value, Width * Height).ToArray(), Width, Height,
                compressed ? CfitsioNative.COMPRESSION.GZIP_1 : CfitsioNative.COMPRESSION.NOCOMPRESS);
            writer.Close();
        }

        private static ImageBufferLease Apply(CaptureTestContext host, LiveStackItem item) => (ImageBufferLease)calibrate.Invoke(host.Dockable, new object[] { item, CancellationToken.None })!;

        private static CalibrationManagerSimd.CalibrationMaster CachedReader(CaptureTestContext host) {
            object manager = typeof(LivestackDockable).GetField("calibrationManager", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host.Dockable)!;
            IDictionary cache = (IDictionary)typeof(CalibrationManagerSimd).GetField("masterCache", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(manager)!;
            Assert.That(cache.Count, Is.LessThanOrEqualTo(3));
            CalibrationManagerSimd.CalibrationMaster reader = cache.Values.Cast<CalibrationManagerSimd.CalibrationMaster>().Single();
            float[] buffer = (float[])reader.GetType().GetField("_data", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
            Assert.That(buffer.Length, Is.LessThan(Width * 2), "Only a rented row may be retained, never a full master image.");
            return reader;
        }

        private static void ChangeLibrary(Action action) {
            SynchronizationContext? context = SynchronizationContext.Current;
            try { SynchronizationContext.SetSynchronizationContext(null); action(); }
            finally { SynchronizationContext.SetSynchronizationContext(context); }
        }
    }
}
