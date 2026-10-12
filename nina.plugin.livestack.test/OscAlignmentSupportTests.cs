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

        [Test]
        public async Task PredictionCommitsOnlyAfterEveryOscChannelHasAccumulated() {
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
                LiveStackTab green = host.Dockable.Tabs.OfType<LiveStackTab>().Single(tab => tab.Filter == LiveStackBag.GREEN_OSC);
                LiveStackBag greenBag = (LiveStackBag)typeof(LiveStackTab).GetField("bag", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(green)!;
                typeof(LiveStackBag).GetField("<ImageCount>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(greenBag, int.MaxValue);
                analysis = Analysis(AlignmentSafetyTests.Project(field, AlignmentSafetyTests.Matrix(tx: 9, ty: -7)));

                Assert.ThrowsAsync<InvalidOperationException>(async () => await Stack(host, red, analysis));
                Assert.That(Prediction(redBag), Is.EqualTo(committed), "An accepted red solution is not committed as a hint when another channel cannot accumulate.");
            } finally {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        private static double[,]? Prediction(LiveStackBag bag) {
            return (double[,]?)typeof(LiveStackBag).GetField("lastCommittedMatrix", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bag);
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
