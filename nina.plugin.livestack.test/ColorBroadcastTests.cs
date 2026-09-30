using Moq;
using NINA.Core.Model;
using NINA.Image.FileFormat;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.LivestackDockables;
using System.Windows.Media.Imaging;

namespace nina.plugin.livestack.test {
    [NonParallelizable]
    [Apartment(ApartmentState.STA)]
    public class ColorBroadcastTests {
        [Test]
        public async Task AcceptedFramesBroadcastFreshColorRegardlessOfSelection([Values] bool selectColor, [Values] bool autosave) {
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try {
                await using CaptureTestContext host = new();
                string[] filters = { "R", "G", "B" };
                foreach (string filter in filters) {
                    Assert.That(await host.Dockable.ProcessImageAsync(CreateImage(host, filter), Guid.NewGuid(), CancellationToken.None), Is.True);
                }
                LiveStackTab[] channels = host.Dockable.Tabs.OfType<LiveStackTab>().ToArray();
                ColorCombinationTab color = new(host.Profile.Object, channels[0], channels[1], channels[2]);
                host.Dockable.Tabs.Add(color);
                await color.Refresh(CancellationToken.None);
                Assert.That(color.StackImage, Is.Not.Null);
                host.Dockable.SelectedTab = selectColor ? color : channels[0];
                host.Plugin.SaveStackedLights = autosave;

                string savedColor = Path.Combine(host.DirectoryPath, "stacks", "target-RGB.png");
                List<LivestackBroadcast> broadcasts = new();
                host.Broker.Setup(b => b.Publish(It.IsAny<IMessage>())).Callback<IMessage>(message => {
                    if (message is LivestackBroadcast broadcast) {
                        broadcasts.Add(broadcast);
                        if (!((LiveStackBroadcastContent)broadcast.Content).IsMonochrome) {
                            Assert.That(File.Exists(savedColor), Is.EqualTo(autosave), "RGB autosave must finish before its broadcast.");
                        }
                    }
                }).Returns(Task.CompletedTask);

                for (int channel = 0; channel < filters.Length; channel++) {
                    BitmapSource previousImage = color.StackImage;
                    Guid correlation = Guid.NewGuid();
                    broadcasts.Clear();

                    bool accepted = await host.Dockable.ProcessImageAsync(CreateImage(host, filters[channel]), correlation, CancellationToken.None);

                    Assert.That(accepted, Is.True);
                    Assert.That(broadcasts, Has.Count.EqualTo(2), "Every accepted frame must publish both its channel and the updated RGB image.");
                    Assert.That(((LiveStackBroadcastContent)broadcasts[0].Content).Filter, Is.EqualTo(filters[channel]));
                    LiveStackBroadcastContent content = (LiveStackBroadcastContent)broadcasts[1].Content;
                    Assert.Multiple(() => {
                        Assert.That(broadcasts[1].CorrelationId, Is.EqualTo(correlation));
                        Assert.That(content.IsMonochrome, Is.False);
                        Assert.That(content.Filter, Is.EqualTo("RGB"));
                        Assert.That(content.Target, Is.EqualTo("target"));
                        Assert.That(content.RedStackCount, Is.EqualTo(2));
                        Assert.That(content.GreenStackCount, Is.EqualTo(channel >= 1 ? 2 : 1));
                        Assert.That(content.BlueStackCount, Is.EqualTo(channel >= 2 ? 2 : 1));
                        Assert.That(content.Image, Is.SameAs(color.StackImage));
                        Assert.That(content.Image, Is.Not.SameAs(previousImage));
                        Assert.That(content.Image.IsFrozen, Is.True);
                        Assert.That(color.NeedsRefresh, Is.False);
                    });
                }
                Assert.That(Directory.GetFiles(host.DirectoryPath, "*.png", SearchOption.AllDirectories).Length, Is.EqualTo(autosave ? 1 : 0));
                Assert.That(Directory.GetFiles(host.DirectoryPath, "*.fits", SearchOption.AllDirectories).Length, Is.EqualTo(autosave ? 3 : 0));

                BitmapSource acceptedImage = color.StackImage;
                broadcasts.Clear();
                bool rejected = await host.Dockable.ProcessImageAsync(CreateImage(host, "R", seed: 107), Guid.NewGuid(), CancellationToken.None);
                Assert.That(rejected, Is.False);
                Assert.That(broadcasts, Is.Empty, "A rejected frame must not publish a stack update.");
                Assert.That(color.StackImage, Is.SameAs(acceptedImage));
                Assert.That(channels.Select(tab => tab.StackCount), Is.EqualTo(new[] { 2, 2, 2 }));
            } finally {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        private static IImageData CreateImage(CaptureTestContext host, string filter, int seed = 32) {
            Mock<IImageData> image = host.Image();
            image.Object.MetaData.FilterWheel.Filter = filter;
            image.Object.MetaData.Camera.Gain = 100;
            image.Object.MetaData.Camera.Offset = 10;
            image.Object.MetaData.Image.ExposureTime = 60;
            image.SetupGet(i => i.Properties).Returns(new ImageProperties(1000, 1000, 16, false, 100, 10));
            List<DetectedStar> stars = AlignmentSafetyTests.CreateCatalog(40, seed).Select((point, i) => new DetectedStar {
                Position = point,
                BoundingBox = new System.Drawing.Rectangle((int)point.X - 4, (int)point.Y - 4, 8, 8),
                MaxBrightness = 20000 + i * 20,
                Background = 500,
                HFR = 3
            }).ToList();
            Mock<IStarDetectionAnalysis> analysis = new();
            analysis.SetupGet(a => a.DetectedStars).Returns(stars.Count);
            analysis.SetupGet(a => a.StarList).Returns(stars);
            image.SetupGet(i => i.StarDetectionAnalysis).Returns(analysis.Object);
            image.Setup(i => i.SaveToDisk(It.IsAny<FileSaveInfo>(), It.IsAny<CancellationToken>(), true, It.IsAny<IList<ImagePattern>>()))
                .Returns(() => {
                    string path = Path.Combine(host.DirectoryPath, "capture.fits");
                    float[] pixels = Enumerable.Range(0, 1000000).Select(i => 0.1f + (i % 997) / 2000f).ToArray();
                    CFitsioFITSExtendedWriter writer = new(path, pixels, 1000, 1000);
                    writer.Close();
                    return Task.FromResult(path);
                });
            return image.Object;
        }
    }
}
