using Accord;
using Moq;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.LivestackDockables;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using System.Reflection;

namespace nina.plugin.livestack.test {
    [NonParallelizable]
    [Apartment(ApartmentState.STA)]
    public class LivestackAlignmentIntegrationTests {
        private Livestack plugin = null!;
        private Mock<IProfileService> profile = null!;
        private Mock<IImageDataFactory> factory = null!;
        private Mock<IWindowServiceFactory> windows = null!;

        [SetUp]
        public void SetUp() {
            profile = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
            string savedValue = "";
            profile.Setup(p => p.ActiveProfile.PluginSettings.TryGetValue(It.IsAny<Guid>(), It.IsAny<string>(), out savedValue)).Returns(false);
            factory = new Mock<IImageDataFactory>();
            windows = new Mock<IWindowServiceFactory>();
            plugin = new Livestack(profile.Object, factory.Object, windows.Object);
        }

        [TearDown]
        public async Task TearDown() {
            await plugin.Teardown();
        }

        [Test]
        public async Task MonoCallerSkipsRejectedFrameWithoutRenderingOrUpdatingTheStack() {
            List<Point> reference = AlignmentSafetyTests.CreateCatalog(40, 106);
            List<Point> current = AlignmentSafetyTests.CreateCatalog(40, 107);
            LiveStackBag bag = new("target", "L", new ImageProperties(1000, 1000, 16, false, 100, 10), new ImageMetaData(), reference);
            bag.Add(Enumerable.Repeat(0.4f, 1000000).ToArray());
            LiveStackTab tab = new(profile.Object, bag) { StackCount = 1 };
            Mock<IImageSaveMediator> imageSave = new();
            Mock<IMessageBroker> broker = new();
            LivestackDockable dockable = new(profile.Object, Mock.Of<IApplicationStatusMediator>(), imageSave.Object, factory.Object, windows.Object, Mock.Of<ICameraMediator>(), broker.Object);
            LiveStackItem item = CreateItem(current, bitDepth: 16);
            MethodInfo mono = typeof(LivestackDockable).GetMethod("StackMono", BindingFlags.NonPublic | BindingFlags.Instance)!;

            using ImageBufferLease frame = ImageBufferPool.Shared.Rent(1000000);
            Array.Fill(frame.Buffer, 0.8f);
            bool added = await (Task<bool>)mono.Invoke(dockable, new object[] { frame, item, tab, Guid.NewGuid(), CancellationToken.None })!;

            Assert.Multiple(() => {
                Assert.That(added, Is.False);
                Assert.That(bag.Stack, Is.All.EqualTo(0.4f));
                Assert.That(bag.ImageCount, Is.EqualTo(1));
                Assert.That(tab.StackCount, Is.EqualTo(1));
                Assert.That(tab.StackImage, Is.Null);
                Assert.That(tab.ReferenceStars, Is.SameAs(reference));
            });
            factory.VerifyNoOtherCalls();
            broker.Verify(b => b.Publish(It.IsAny<IMessage>()), Times.Never);
        }

        [Test]
        public async Task AcceptedStacksSaveBeforePublishingInChannelOrder([Values(1, 3)] int channels, [Values] bool autosave) {
            string directory = Path.Combine(Path.GetTempPath(), "LivestackCompletion-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try {
                string savedDirectory = directory;
                bool savedAutosave = autosave;
                profile.Setup(p => p.ActiveProfile.PluginSettings.TryGetValue(It.IsAny<Guid>(), nameof(Livestack.WorkingDirectory), out savedDirectory)).Returns(true);
                profile.Setup(p => p.ActiveProfile.PluginSettings.TryGetValue(It.IsAny<Guid>(), nameof(Livestack.SaveStackedLights), out savedAutosave)).Returns(true);
                Assert.That(plugin.WorkingDirectory, Is.EqualTo(directory));
                Assert.That(plugin.SaveStackedLights, Is.EqualTo(autosave));

                string[] filters = channels == 1 ? new[] { "L" } : new[] { LiveStackBag.RED_OSC, LiveStackBag.GREEN_OSC, LiveStackBag.BLUE_OSC };
                LiveStackTab[] tabs = filters.Select(filter => CreateTab(filter, 32)).ToArray();
                foreach (LiveStackTab tab in tabs) {
                    await tab.Refresh(CancellationToken.None);
                }
                string[] paths = filters.Select(filter => Path.Combine(directory, "stacks", "target-" + filter + ".fits")).ToArray();
                List<LivestackBroadcast> published = new();
                Mock<IMessageBroker> broker = new();
                broker.Setup(b => b.Publish(It.IsAny<IMessage>())).Callback<IMessage>(message => {
                    if (message is LivestackBroadcast broadcast) {
                        Assert.That(paths.All(File.Exists), Is.EqualTo(autosave), "All requested saves must finish before the first broadcast.");
                        published.Add(broadcast);
                    }
                }).Returns(Task.CompletedTask);
                LivestackDockable dockable = new(profile.Object, Mock.Of<IApplicationStatusMediator>(), Mock.Of<IImageSaveMediator>(), factory.Object, windows.Object, Mock.Of<ICameraMediator>(), broker.Object);
                Guid correlation = Guid.NewGuid();
                LiveStackItem item = CreateItem(tabs[0].ReferenceStars, 16);
                if (channels == 1) {
                    using ImageBufferLease frame = ImageBufferPool.Shared.Rent(1000000);
                    Array.Fill(frame.Buffer, 0.8f);
                    MethodInfo mono = typeof(LivestackDockable).GetMethod("StackMono", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    bool added = await (Task<bool>)mono.Invoke(dockable, new object[] { frame, item, tabs[0], correlation, CancellationToken.None })!;
                    Assert.That(added, Is.True);
                    Assert.That(tabs[0].StackCount, Is.EqualTo(2));
                } else {
                    MethodInfo complete = typeof(LivestackDockable).GetMethod("SaveAndPublishStacks", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    complete.Invoke(dockable, new object[] { item, correlation, tabs });
                }

                Assert.That(published.Count, Is.EqualTo(channels));
                for (int i = 0; i < channels; i++) {
                    Assert.That(published[i].CorrelationId, Is.EqualTo(correlation));
                    LiveStackBroadcastContent content = (LiveStackBroadcastContent)published[i].Content;
                    Assert.That(content.Filter, Is.EqualTo(filters[i]));
                    Assert.That(content.Target, Is.EqualTo("target"));
                    Assert.That(content.StackCount, Is.EqualTo(tabs[i].StackCount));
                    Assert.That(content.Image, Is.SameAs(tabs[i].StackImage));
                    Assert.That(content.Image, Is.Not.Null);
                    Assert.That(content.IsMonochrome, Is.True);
                    if (autosave) {
                        using CFitsioFITSReader saved = new(paths[i]);
                        Assert.That(saved.ReadFloatHeader("IMGCOUNT"), Is.EqualTo(tabs[i].StackCount));
                        FloatAssert.AreEqual(tabs[i].Stack, saved.ReadAllPixelsAsFloat());
                    }
                }
                Assert.That(Directory.GetFiles(directory, "*.fits", SearchOption.AllDirectories).Length, Is.EqualTo(autosave ? channels : 0));
            } finally {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ColorCombinationRejectsUnrelatedChannelBeforeResampling() {
            LiveStackTab red = CreateTab("R", 106);
            LiveStackTab green = CreateTab("G", 107);
            ColorCombinationTab color = new(profile.Object, red, green, green);
            MethodInfo align = typeof(ColorCombinationTab).GetMethod("AlignTab", BindingFlags.NonPublic | BindingFlags.Instance)!;

            object? pixels = align.Invoke(color, new object[] { red, green, CancellationToken.None });

            Assert.That(pixels, Is.Null);
            Assert.That(red.Stack, Is.All.EqualTo(0.4f));
            Assert.That(green.Stack, Is.All.EqualTo(0.4f));
        }

        [Test]
        public void FramePropertiesUseTheCapturedBitDepth() {
            LiveStackItem item = CreateItem(AlignmentSafetyTests.CreateCatalog(40, 32), bitDepth: 12);
            MethodInfo properties = typeof(LivestackDockable).GetMethod("GetFrameProperties", BindingFlags.NonPublic | BindingFlags.Static)!;
            ImageProperties actual = (ImageProperties)properties.Invoke(null, new object[] { item })!;
            Assert.That(actual.BitDepth, Is.EqualTo(12));
        }

        [TestCase("R", false)]
        [TestCase("R", true)]
        [TestCase("R_OSC", true)]
        [TestCase("G_OSC", true)]
        [TestCase("B_OSC", true)]
        public async Task RemovingSourceAlsoRemovesDependentColorAndSelection(string removedFilter, bool selectColor) {
            SynchronizationContext? previous = SynchronizationContext.Current;
            try {
                // Without a WPF Application, NINA's collection captures a null dispatcher context.
                SynchronizationContext.SetSynchronizationContext(null);
                bool osc = removedFilter.EndsWith("_OSC");
                LiveStackTab red = CreateTab(osc ? "R_OSC" : "R", 32);
                LiveStackTab green = CreateTab(osc ? "G_OSC" : "G", 33);
                LiveStackTab blue = CreateTab(osc ? "B_OSC" : "B", 34);
                LiveStackTab unrelated = CreateTab("Other", 35);
                ColorCombinationTab color = new(profile.Object, red, green, blue);
                LivestackDockable dockable = new(profile.Object, Mock.Of<IApplicationStatusMediator>(), Mock.Of<IImageSaveMediator>(), factory.Object, windows.Object, Mock.Of<ICameraMediator>(), Mock.Of<IMessageBroker>());
                dockable.Tabs.Add(red);
                dockable.Tabs.Add(green);
                dockable.Tabs.Add(blue);
                dockable.Tabs.Add(unrelated);
                dockable.Tabs.Add(color);
                typeof(LivestackDockable).GetField("selectedTab", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dockable, selectColor ? color : red);

                await dockable.RemoveTabCommand.ExecuteAsync(dockable.Tabs.First(tab => tab.Filter == removedFilter));

                Assert.That(dockable.Tabs, Is.EquivalentTo(osc ? new[] { unrelated } : new[] { green, blue, unrelated }));
                Assert.That(dockable.SelectedTab, Is.Not.SameAs(red));
                Assert.That(dockable.SelectedTab, Is.Not.SameAs(color));
            } finally {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        [TestCase(false, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 1)]
        [TestCase(true, 2)]
        public async Task ColorPreviewReleasesScratchWithoutChangingItsPublishedImage(bool background, int downsample) {
            LiveStackTab red = CreateTab("R", 32);
            LiveStackTab green = CreateTab("G", 32);
            LiveStackTab blue = CreateTab("B", 32);
            ColorCombinationTab color = new(profile.Object, red, green, blue) {
                EnableBackgroundExtraction = background,
                Downsample = downsample,
                EnableGreenDeNoise = true
            };
            await color.Refresh(CancellationToken.None);
            Assert.That(color.StackImage, Is.Not.Null, "Rendering failures must not be hidden by Refresh's exception handler.");
            Assert.That(color.StackImage.PixelWidth, Is.EqualTo(1000 / downsample));
            Assert.That(color.StackImage.IsFrozen, Is.True);
            int stride = color.StackImage.PixelWidth * 6;
            byte[] before = new byte[stride * color.StackImage.PixelHeight];
            color.StackImage.CopyPixels(before, stride, 0);
            using (ImageBufferLease scratch = ImageBufferPool.Shared.Rent(1000000)) {
                Array.Fill(scratch.Buffer, 1f);
            }
            ImageBufferPool.Shared.Trim();
            byte[] after = new byte[before.Length];
            color.StackImage.CopyPixels(after, stride, 0);
            Assert.That(after, Is.EqualTo(before));
            Assert.That(red.Stack, Is.All.EqualTo(0.4f));
            Assert.That(green.Stack, Is.All.EqualTo(0.4f));
            Assert.That(blue.Stack, Is.All.EqualTo(0.4f));
        }

        [Test]
        public async Task MonoAndAlignedColorPreviewsPreserveTheSamePixels([Values] bool background, [Values(1, 2)] int downsample) {
            LiveStackTab mono = CreateTab("L", 32);
            for (int i = 0; i < mono.Stack.Length; i++) {
                mono.Stack[i] = 0.1f + (i % 997) / 2000f;
            }
            float[] original = (float[])mono.Stack.Clone();
            mono.EnableBackgroundExtraction = background;
            mono.Downsample = downsample;
            mono.StretchFactor = 0.2;
            mono.BlackClipping = -2.8;
            ColorCombinationTab color = new(profile.Object, mono, mono, mono, channelsAlreadyAligned: true) {
                EnableBackgroundExtraction = background,
                Downsample = downsample,
                RedStretchFactor = mono.StretchFactor,
                GreenStretchFactor = mono.StretchFactor,
                BlueStretchFactor = mono.StretchFactor,
                RedBlackClipping = mono.BlackClipping,
                GreenBlackClipping = mono.BlackClipping,
                BlueBlackClipping = mono.BlackClipping,
                EnableGreenDeNoise = false
            };

            await mono.Refresh(CancellationToken.None);
            await color.Refresh(CancellationToken.None);

            Assert.That(mono.StackImage, Is.Not.Null);
            Assert.That(color.StackImage, Is.Not.Null);
            Assert.That(mono.StackImage.IsFrozen, Is.True);
            Assert.That(mono.StackImage.PixelWidth, Is.EqualTo(1000 / downsample));
            int length = mono.StackImage.PixelWidth * mono.StackImage.PixelHeight;
            ushort[] gray = new ushort[length];
            ushort[] rgb = new ushort[length * 3];
            mono.StackImage.CopyPixels(gray, mono.StackImage.PixelWidth * 2, 0);
            color.StackImage.CopyPixels(rgb, color.StackImage.PixelWidth * 6, 0);
            Assert.That(gray.Distinct().Count(), Is.GreaterThan(100), "A blank preview must not satisfy pixel equivalence.");
            for (int channel = 0; channel < 3; channel++) {
                Assert.That(Enumerable.Range(0, length).Select(i => rgb[i * 3 + channel]), Is.EqualTo(gray));
            }
            using (ImageBufferLease scratch = ImageBufferPool.Shared.Rent(mono.Stack.Length)) {
                Array.Fill(scratch.Buffer, 1f);
            }
            ushort[] after = new ushort[length];
            mono.StackImage.CopyPixels(after, mono.StackImage.PixelWidth * 2, 0);
            Assert.That(after, Is.EqualTo(gray));
            Assert.That(mono.Stack, Is.EqualTo(original));
        }

        [Test]
        public async Task CanceledPreviewWaitDoesNotLeakTheGlobalRenderPermit([Values] bool color) {
            LiveStackTab mono = CreateTab("L", 32);
            ColorCombinationTab combination = new(profile.Object, mono, mono, mono, channelsAlreadyAligned: true);
            IStackTab tab = color ? combination : mono;
            Func<CancellationToken, Task> refresh = color ? combination.Refresh : mono.Refresh;
            using ManualResetEventSlim release = new();
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task active = LiveStackPreview.RenderAsync(() => {
                started.SetResult();
                release.Wait();
            }, CancellationToken.None);
            Task? next = null;
            try {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using CancellationTokenSource cancellation = new();
                Task pending = refresh(cancellation.Token);
                Assert.That(pending.IsCompleted, Is.False);
                cancellation.Cancel();
                await pending.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(tab.StackImage, Is.Null);
                next = refresh(CancellationToken.None);
                Assert.That(next.IsCompleted, Is.False, "Canceling a waiter must not release the active renderer's permit.");
            } finally {
                release.Set();
                await active.WaitAsync(TimeSpan.FromSeconds(5));
                if (next != null) {
                    await next.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            Assert.That(tab.StackImage, Is.Not.Null);
            Assert.That(tab.Locked, Is.False);
        }

        [Test]
        public async Task PreviewFailureKeepsPublishedImageAndAllowsNextRefresh([Values] bool color) {
            LiveStackTab mono = CreateTab("L", 32);
            ColorCombinationTab combination = new(profile.Object, mono, mono, mono, channelsAlreadyAligned: true);
            IStackTab tab = color ? combination : mono;
            Func<CancellationToken, Task> refresh = color ? combination.Refresh : mono.Refresh;
            await refresh(CancellationToken.None);
            Assert.That(tab.StackImage, Is.Not.Null);
            var original = tab.StackImage;

            // The requested size cannot produce a native bitmap. Exercise the real render failure path.
            mono.Downsample = int.MaxValue;
            combination.Downsample = int.MaxValue;
            await refresh(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(tab.StackImage, Is.SameAs(original));
            Assert.That(tab.Locked, Is.False);

            mono.Downsample = 1;
            combination.Downsample = 1;
            await refresh(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(tab.StackImage, Is.Not.SameAs(original));
            Assert.That(tab.Locked, Is.False);
        }

        private LiveStackTab CreateTab(string filter, int seed) {
            LiveStackBag bag = new("target", filter, new ImageProperties(1000, 1000, 16, false, 100, 10), new ImageMetaData(), AlignmentSafetyTests.CreateCatalog(40, seed));
            bag.Add(Enumerable.Repeat(0.4f, 1000000).ToArray());
            return new LiveStackTab(profile.Object, bag);
        }

        private static LiveStackItem CreateItem(List<Point> stars, int bitDepth) {
            List<DetectedStar> detections = stars.Select((point, i) => new DetectedStar {
                Position = point,
                BoundingBox = new System.Drawing.Rectangle((int)point.X - 4, (int)point.Y - 4, 8, 8),
                MaxBrightness = 20000 + i * 20,
                Background = 500,
                HFR = 3
            }).ToList();
            Mock<IStarDetectionAnalysis> analysis = new();
            analysis.SetupGet(a => a.StarList).Returns(detections);
            analysis.SetupGet(a => a.HFR).Returns(3);
            return new LiveStackItem("diagnostic.fits", "target", "L", 60, 100, 10, 1000, 1000, bitDepth, false, analysis.Object, new ImageMetaData());
        }
    }
}
