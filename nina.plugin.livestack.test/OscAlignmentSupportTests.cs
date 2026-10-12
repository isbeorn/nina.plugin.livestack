using Accord;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.LivestackDockables;
using System.Reflection;

namespace nina.plugin.livestack.test {
    [NonParallelizable]
    [Apartment(ApartmentState.STA)]
    public class OscAlignmentSupportTests {
        [Test]
        public async Task OscDetectsAndSolvesRedOnceThenSharesReferenceSupportAcrossChannels() {
            SynchronizationContext? previous = SynchronizationContext.Current;
            try {
                SynchronizationContext.SetSynchronizationContext(null);
                await using CaptureTestContext host = new();
                List<Point> field = AlignmentSafetyTests.CreateCatalog(3000, 801);
                StarDetectionAnalysis analysis = Analysis(field);
                int analysisRequests = 0;
                host.StarDetection.Setup(detector => detector.CreateAnalysis()).Returns(() => {
                    analysisRequests++;
                    return analysis;
                });
                ImageProperties properties = new(1000, 1000, 16, true, 100, 10);
                LiveStackBag redBag = new("target", LiveStackBag.RED_OSC, properties, new ImageMetaData(), null);
                LiveStackTab red = new(host.Profile.Object, redBag);
                host.Dockable.Tabs.Add(red);

                Assert.That(await Stack(host, red, analysis), Is.True);
                LiveStackTab green = host.Dockable.Tabs.OfType<LiveStackTab>().Single(tab => tab.Filter == LiveStackBag.GREEN_OSC);
                LiveStackTab blue = host.Dockable.Tabs.OfType<LiveStackTab>().Single(tab => tab.Filter == LiveStackBag.BLUE_OSC);
                AlignmentStarCatalog reference = red.ReferenceCatalog;
                Assert.Multiple(() => {
                    Assert.That(reference.MatchStars.Count, Is.EqualTo(1000));
                    Assert.That(reference.SupportStars.Count, Is.EqualTo(3000));
                    Assert.That(green.ReferenceCatalog, Is.SameAs(reference));
                    Assert.That(blue.ReferenceCatalog, Is.SameAs(reference));
                    Assert.That(analysisRequests, Is.EqualTo(1));
                });

                analysis = Analysis(AlignmentSafetyTests.Project(field, AlignmentSafetyTests.Matrix(tx: 9, ty: -7)));
                Assert.That(await Stack(host, red, analysis), Is.True);
                Assert.Multiple(() => {
                    Assert.That(analysisRequests, Is.EqualTo(2));
                    Assert.That(red.StackCount, Is.EqualTo(2));
                    Assert.That(green.StackCount, Is.EqualTo(2));
                    Assert.That(blue.StackCount, Is.EqualTo(2));
                    Assert.That(red.ReferenceCatalog, Is.SameAs(reference));
                    Assert.That(green.ReferenceCatalog, Is.SameAs(reference));
                    Assert.That(blue.ReferenceCatalog, Is.SameAs(reference));
                    Assert.That(Prediction(redBag)![0, 2], Is.EqualTo(9).Within(0.01));
                });

                double[,] committed = Prediction(redBag)!;
                analysis = Analysis(AlignmentSafetyTests.CreateCatalog(3000, 802));
                Assert.That(await Stack(host, red, analysis), Is.False);
                Assert.That(Prediction(redBag), Is.EqualTo(committed));
                Assert.That(red.StackCount, Is.EqualTo(2));
                Assert.That(green.StackCount, Is.EqualTo(2));
                Assert.That(blue.StackCount, Is.EqualTo(2));

                analysis = Analysis(field);
                Assert.ThrowsAsync<OperationCanceledException>(async () => await Stack(host, red, analysis, new CancellationToken(true)));
                Assert.That(Prediction(redBag), Is.EqualTo(committed));
                Assert.That(redBag.ImageCount, Is.EqualTo(2));
            } finally {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task LaterChannelOverflowPreservesEveryChannelAndPrediction(bool blueFails) {
            SynchronizationContext? previous = SynchronizationContext.Current;
            try {
                SynchronizationContext.SetSynchronizationContext(null);
                await using CaptureTestContext host = new();
                List<Point> field = AlignmentSafetyTests.CreateCatalog(40, 913);
                StarDetectionAnalysis analysis = Analysis(field);
                host.StarDetection.Setup(detector => detector.CreateAnalysis()).Returns(() => analysis);
                ImageProperties properties = new(1000, 1000, 16, true, 100, 10);
                LiveStackBag redBag = new("target", LiveStackBag.RED_OSC, properties, new ImageMetaData(), null);
                LiveStackTab red = new(host.Profile.Object, redBag);
                host.Dockable.Tabs.Add(red);
                Assert.That(await Stack(host, red, analysis), Is.True);
                double[,] committed = Prediction(redBag)!;
                LiveStackBag[] bags = host.Dockable.Tabs.OfType<LiveStackTab>()
                    .Select(tab => (LiveStackBag)typeof(LiveStackTab).GetField("bag", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!).ToArray();
                LiveStackBag failing = bags.Single(bag => bag.Filter == (blueFails ? LiveStackBag.BLUE_OSC : LiveStackBag.GREEN_OSC));
                typeof(LiveStackBag).GetField("<ImageCount>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(failing, int.MaxValue);
                int[] counts = bags.Select(bag => bag.ImageCount).ToArray();
                float[][] pixels = bags.Select(bag => (float[])bag.Stack.Clone()).ToArray();
                analysis = Analysis(AlignmentSafetyTests.Project(field, AlignmentSafetyTests.Matrix(tx: 9, ty: -7)));

                Assert.ThrowsAsync<InvalidOperationException>(async () => await Stack(host, red, analysis));
                Assert.That(Prediction(redBag), Is.EqualTo(committed), "An accepted red solution is not committed as a hint when another channel cannot accumulate.");
                Assert.That(bags.Select(bag => bag.ImageCount), Is.EqualTo(counts));
                for (int i = 0; i < bags.Length; i++) {
                    Assert.That(bags[i].Stack, Is.EqualTo(pixels[i]));
                }
            } finally {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        private static double[,]? Prediction(LiveStackBag bag) {
            return (double[,]?)typeof(LiveStackBag).GetField("lastCommittedMatrix", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bag);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MalformedLaterChannelLeavesAllFirstReferencesUnpublished(bool blueFails) {
            ImageProperties properties = new(1000, 1000, 16, true, 100, 10);
            List<Point> field = AlignmentSafetyTests.CreateCatalog(40, 1447);
            AlignmentStarCatalog stars = new(field, field);
            LiveStackBag red = new("target", LiveStackBag.RED_OSC, properties, new ImageMetaData(), null);
            LiveStackBag green = new("target", LiveStackBag.GREEN_OSC, properties, new ImageMetaData(), null);
            LiveStackBag blue = new("target", LiveStackBag.BLUE_OSC, properties, new ImageMetaData(), null);
            ushort[] valid = new ushort[1000000];
            AlignmentResult result = red.AlignAndAddOsc(green, blue, valid, blueFails ? valid : new ushort[1], blueFails ? new ushort[1] : valid,
                properties, stars, default);
            Assert.That(result.Success, Is.False);
            foreach (LiveStackBag bag in new[] { red, green, blue }) {
                Assert.That(bag.Stack, Is.Null);
                Assert.That(bag.ReferenceImageStars, Is.Null);
                Assert.That(bag.ImageCount, Is.Zero);
                Assert.That(Prediction(bag), Is.Null);
            }
        }

        [TestCase(255)]
        [TestCase(65535)]
        public async Task AllOscChannelsPromoteCountsAndAccumulateTheNextFrame(int priorCount) {
            SynchronizationContext? previous = SynchronizationContext.Current;
            try {
                SynchronizationContext.SetSynchronizationContext(null);
                await using CaptureTestContext host = new();
                StarDetectionAnalysis analysis = Analysis(AlignmentSafetyTests.CreateCatalog(40, 1449));
                host.StarDetection.Setup(detector => detector.CreateAnalysis()).Returns(() => analysis);
                ImageProperties properties = new(1000, 1000, 16, true, 100, 10);
                LiveStackBag redBag = new("target", LiveStackBag.RED_OSC, properties, new ImageMetaData(), null);
                LiveStackTab red = new(host.Profile.Object, redBag);
                host.Dockable.Tabs.Add(red);
                Assert.That(await Stack(host, red, analysis), Is.True);
                LiveStackBag[] bags = host.Dockable.Tabs.OfType<LiveStackTab>()
                    .Select(tab => (LiveStackBag)typeof(LiveStackTab).GetField("bag", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!).ToArray();
                FieldInfo countsField = typeof(LiveStackBag).GetField("contributionCounts", BindingFlags.Instance | BindingFlags.NonPublic)!;
                foreach (LiveStackBag bag in bags) {
                    Array.Fill(bag.Stack, 0.2f);
                    typeof(LiveStackBag).GetProperty(nameof(LiveStackBag.ImageCount))!.SetValue(bag, priorCount);
                    Array counts = priorCount == 255 ? Enumerable.Repeat((byte)255, 1000000).ToArray() : Enumerable.Repeat(ushort.MaxValue, 1000000).ToArray();
                    countsField.SetValue(bag, counts);
                }

                Assert.That(await Stack(host, red, analysis), Is.True);
                foreach (LiveStackBag bag in bags) {
                    Array counts = (Array)countsField.GetValue(bag)!;
                    Assert.That(counts, Is.TypeOf(priorCount == 255 ? typeof(ushort[]) : typeof(uint[])));
                    Assert.That(Convert.ToUInt32(counts.GetValue(500500)), Is.EqualTo(priorCount + 1));
                    Assert.That(bag.ImageCount, Is.EqualTo(priorCount + 1));
                    Assert.That(bag.Stack[500500], Is.EqualTo((float)(0.2f + (0.4 - 0.2f) / (priorCount + 1))).Within(1e-7));
                }
            } finally {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        private static StarDetectionAnalysis Analysis(List<Point> stars) {
            return new StarDetectionAnalysis {
                DetectedStars = stars.Count, HFR = 3,
                StarList = stars.Select(point => new DetectedStar {
                    Position = point, HFR = 3, MaxBrightness = 20000 + point.X * 10, Background = 500,
                    BoundingBox = new System.Drawing.Rectangle((int)point.X - 4, (int)point.Y - 4, 8, 8)
                }).ToList()
            };
        }

        private static async Task<bool> Stack(CaptureTestContext host, LiveStackTab red, StarDetectionAnalysis analysis, CancellationToken token = default) {
            LiveStackItem item = new("unused.fits", "target", "L", 60, 100, 10, 1000, 1000, 16, true, analysis, new ImageMetaData());
            using ImageBufferLease pixels = ImageBufferPool.Shared.Rent(1000000);
            Array.Fill(pixels.Buffer, 0.4f);
            MethodInfo stack = typeof(LivestackDockable).GetMethod("StackOSC", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return await (Task<bool>)stack.Invoke(host.Dockable, new object[] { pixels, item, red, Guid.NewGuid(), token })!;
        }
    }
}
