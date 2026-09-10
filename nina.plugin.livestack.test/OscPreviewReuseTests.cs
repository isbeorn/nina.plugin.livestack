using NINA.Image.ImageData;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.LivestackDockables;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace nina.plugin.livestack.test {
    [NonParallelizable]
    [Apartment(ApartmentState.STA)]
    public class OscPreviewReuseTests {
        private static readonly MethodInfo render = typeof(ColorCombinationTab).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic)!;

        [Test]
        public async Task WarmOscPreviewAvoidsRepeatedFullFrameRendering() {
            await using CaptureTestContext host = new();
            LiveStackTab[] tabs = Enumerable.Range(0, 3).Select(channel => new LiveStackTab(host.Profile.Object, Bag(channel, 1000, 800)) { Downsample = 4 }).ToArray();
            foreach (LiveStackTab tab in tabs) await tab.Refresh(CancellationToken.None);
            ColorCombinationTab color = new(host.Profile.Object, tabs[0], tabs[1], tabs[2], channelsAlreadyAligned: true) { Downsample = 4 };
            render.Invoke(color, new object[] { CancellationToken.None });
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < 5; i++) render.Invoke(color, new object[] { CancellationToken.None });
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            TestContext.WriteLine($"Five warmed OSC previews: {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2} ms, {allocated} allocated bytes.");
            Assert.That(color.StackImage, Is.Not.Null);
            Assert.That(allocated, Is.LessThan(256 * 1024), "Reusing published previews must not allocate histograms or full-frame image caches.");
        }

        [Test]
        public async Task ReusedPreviewsMatchFreshRendering([Values] bool background, [Values(1, 2, 4)] int downsample) {
            await using CaptureTestContext host = new();
            LiveStackTab[] tabs = Enumerable.Range(0, 3).Select(channel => new LiveStackTab(host.Profile.Object, Bag(channel)) {
                Downsample = downsample, EnableBackgroundExtraction = background, BackgroundExtractionAmount = 0.7,
                StretchFactor = 0.15 + channel * 0.05, BlackClipping = -2.5 - channel * 0.1
            }).ToArray();
            ColorCombinationTab color = Color(host, tabs, background, downsample);
            await color.Refresh(CancellationToken.None);
            ushort[] expected = Pixels(color.StackImage);
            foreach (LiveStackTab tab in tabs) await tab.Refresh(CancellationToken.None);
            await color.Refresh(CancellationToken.None);
            Assert.That(Pixels(color.StackImage), Is.EqualTo(expected));
            Assert.That(expected.Distinct().Count(), Is.GreaterThan(100));
        }

        [Test]
        public async Task ChangesToPixelsSettingsAndFailedPreviewsCannotReuseStaleImages() {
            await using CaptureTestContext host = new();
            LiveStackBag[] bags = Enumerable.Range(0, 3).Select(channel => Bag(channel)).ToArray();
            LiveStackTab[] tabs = bags.Select(bag => new LiveStackTab(host.Profile.Object, bag)).ToArray();
            ColorCombinationTab color = new(host.Profile.Object, tabs[0], tabs[1], tabs[2], channelsAlreadyAligned: true);
            foreach (LiveStackTab tab in tabs) await tab.Refresh(CancellationToken.None);
            await color.Refresh(CancellationToken.None);
            ushort[] original = Pixels(color.StackImage);
            for (int channel = 0; channel < 3; channel++) {
                bags[channel].Add(PixelData(channel + 3, 257, 129));
                await AssertMatchesFresh();
                // Replacing the reference can return ImageCount to a previously rendered value.
                bags[channel].ForcePushReference(bags[channel].Properties, bags[channel].ReferenceImageStars, PixelData(channel + 6, 257, 129));
                await AssertMatchesFresh();
            }
            Assert.That(Pixels(color.StackImage), Is.Not.EqualTo(original));
            foreach (LiveStackTab tab in tabs) await tab.Refresh(CancellationToken.None);
            color.RedStretchFactor = 0.3;
            color.GreenBlackClipping = -4;
            color.Downsample = 2;
            color.EnableBackgroundExtraction = true;
            color.BackgroundExtractionAmount = 0.9;
            await AssertMatchesFresh();
            BitmapSource previous = tabs[0].StackImage;
            tabs[0].Downsample = int.MaxValue;
            await tabs[0].Refresh(CancellationToken.None);
            Assert.That(tabs[0].StackImage, Is.SameAs(previous));
            await AssertMatchesFresh();
            using CancellationTokenSource canceled = new();
            canceled.Cancel();
            await color.Refresh(canceled.Token);
            await AssertMatchesFresh();

            async Task AssertMatchesFresh() {
                LiveStackTab[] fresh = bags.Select(bag => new LiveStackTab(host.Profile.Object, bag)).ToArray();
                ColorCombinationTab expected = new(host.Profile.Object, fresh[0], fresh[1], fresh[2], channelsAlreadyAligned: true) {
                    RedStretchFactor = color.RedStretchFactor, GreenStretchFactor = color.GreenStretchFactor, BlueStretchFactor = color.BlueStretchFactor,
                    RedBlackClipping = color.RedBlackClipping, GreenBlackClipping = color.GreenBlackClipping, BlueBlackClipping = color.BlueBlackClipping,
                    Downsample = color.Downsample, EnableBackgroundExtraction = color.EnableBackgroundExtraction,
                    BackgroundExtractionAmount = color.BackgroundExtractionAmount, EnableGreenDeNoise = color.EnableGreenDeNoise,
                    GreenDeNoiseAmount = color.GreenDeNoiseAmount
                };
                await expected.Refresh(CancellationToken.None);
                await color.Refresh(CancellationToken.None);
                Assert.That(Pixels(color.StackImage), Is.EqualTo(Pixels(expected.StackImage)));
            }
        }

        private static ColorCombinationTab Color(CaptureTestContext host, LiveStackTab[] tabs, bool background, int downsample) => new(host.Profile.Object, tabs[0], tabs[1], tabs[2], channelsAlreadyAligned: true) {
            RedStretchFactor = tabs[0].StretchFactor, GreenStretchFactor = tabs[1].StretchFactor, BlueStretchFactor = tabs[2].StretchFactor,
            RedBlackClipping = tabs[0].BlackClipping, GreenBlackClipping = tabs[1].BlackClipping, BlueBlackClipping = tabs[2].BlackClipping,
            EnableBackgroundExtraction = background, BackgroundExtractionAmount = 0.7, Downsample = downsample, EnableGreenDeNoise = true
        };

        private static LiveStackBag Bag(int channel, int width = 257, int height = 129) {
            LiveStackBag bag = new("target", channel.ToString(), new ImageProperties(width, height, 16, false, 100, 10), new ImageMetaData(), new());
            bag.Add(PixelData(channel, width, height));
            return bag;
        }

        private static float[] PixelData(int channel, int width, int height) => Enumerable.Range(0, width * height)
            .Select(i => 0.1f + ((i * (channel + 1)) % 997) / 2000f).ToArray();

        private static ushort[] Pixels(BitmapSource source) {
            Assert.That(source, Is.Not.Null);
            ushort[] pixels = new ushort[source.PixelWidth * source.PixelHeight * 3];
            source.CopyPixels(pixels, source.PixelWidth * 6, 0);
            return pixels;
        }
    }
}
