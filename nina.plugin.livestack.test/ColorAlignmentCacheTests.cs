using Accord;
using NINA.Image.ImageData;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.LivestackDockables;
using System.Diagnostics;
using System.Reflection;

namespace nina.plugin.livestack.test {
    [NonParallelizable]
    [Apartment(ApartmentState.STA)]
    public class ColorAlignmentCacheTests {
        private static readonly MethodInfo align = typeof(ColorCombinationTab).GetMethod("AlignTab", BindingFlags.NonPublic | BindingFlags.Instance)!;

        [Test]
        public async Task RepeatedAlignmentReusesThePlanWithoutSolverAllocations() {
            await using CaptureTestContext host = new();
            LiveStackBag reference = Bag(32), target = Bag(32);
            LiveStackTab red = new(host.Profile.Object, reference), green = new(host.Profile.Object, target);
            ColorCombinationTab color = new(host.Profile.Object, red, green, green);
            using (ImageBufferLease? warmup = Align(color, red, green)) Assert.That(warmup, Is.Not.Null);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < 5; i++) {
                using ImageBufferLease? pixels = Align(color, red, green);
                Assert.That(pixels!.Buffer[500500], Is.EqualTo(0.4f).Within(1e-5));
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            TestContext.WriteLine($"Five warmed channel alignments: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2} ms, {allocated} allocated bytes.");
            Assert.That(allocated, Is.LessThan(64 * 1024), "Reusing a matrix should not repeat triangle/RANSAC allocations or retain full image snapshots.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ChangingEitherReferenceInvalidatesTheCachedPlan(bool changeRed) {
            await using CaptureTestContext host = new();
            LiveStackBag reference = Bag(32), target = Bag(32);
            LiveStackTab red = new(host.Profile.Object, reference), green = new(host.Profile.Object, target);
            ColorCombinationTab color = new(host.Profile.Object, red, green, green);
            using (ImageBufferLease? first = Align(color, red, green)) Assert.That(first, Is.Not.Null);
            LiveStackBag changed = changeRed ? reference : target;
            changed.ForcePushReference(changed.Properties, AlignmentSafetyTests.CreateCatalog(40, 107), new float[1000000]);
            using (ImageBufferLease? rejected = Align(color, red, green)) Assert.That(rejected, Is.Null);
            changed.ReferenceImageStars.Clear();
            changed.ReferenceImageStars.AddRange(AlignmentSafetyTests.CreateCatalog(40, 32));
            using (ImageBufferLease? recovered = Align(color, red, green)) Assert.That(recovered, Is.Not.Null);
            TargetInvocationException? canceled = Assert.Throws<TargetInvocationException>(() => Align(color, red, green, new CancellationToken(true)));
            Assert.That(canceled!.InnerException, Is.InstanceOf<OperationCanceledException>());
            using ImageBufferLease? afterCancellation = Align(color, red, green);
            Assert.That(afterCancellation, Is.Not.Null);
        }

        private static LiveStackBag Bag(int seed) {
            LiveStackBag bag = new("target", "L", new ImageProperties(1000, 1000, 16, false, 100, 10), new ImageMetaData(), AlignmentSafetyTests.CreateCatalog(40, seed));
            bag.Add(Enumerable.Repeat(0.4f, 1000000).ToArray());
            return bag;
        }

        private static ImageBufferLease? Align(ColorCombinationTab color, LiveStackTab reference, LiveStackTab target, CancellationToken token = default) {
            return (ImageBufferLease?)align.Invoke(color, new object[] { reference, target, token });
        }
    }
}
