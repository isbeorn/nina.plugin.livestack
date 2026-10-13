using Moq;
using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.LivestackDockables;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using System.Reflection;

namespace nina.plugin.livestack.test {
    [NonParallelizable]
    [Apartment(ApartmentState.STA)]
    public class BayerPatternTests {
        [Test]
        public async Task OscCallerUsesConfiguredOrConnectedSensorPattern(
            [Values(SensorType.RGGB, SensorType.BGGR, SensorType.GRBG, SensorType.GBRG, SensorType.RGBG, SensorType.GRGB, SensorType.GBGR, SensorType.BGRG)] SensorType sensor,
            [Values] bool connected, [Values] bool automatic) {
            const int width = 16, height = 16;
            Mock<IProfileService> profile = new() { DefaultValue = DefaultValue.Mock };
            profile.SetupGet(p => p.ActiveProfile.PluginSettings).Returns(new PluginSettings());
            profile.SetupGet(p => p.ActiveProfile.CameraSettings).Returns(new CameraSettings {
                BayerPattern = automatic ? BayerPatternEnum.Auto : BayerPatternEnum.GBRG
            });
            Mock<IPluggableBehaviorSelector<IStarDetection>> detector = new();
            detector.Setup(d => d.GetBehavior()).Returns(new StarDetection());
            Mock<IPluggableBehaviorSelector<IStarAnnotator>> annotator = new();
            annotator.Setup(a => a.GetBehavior()).Returns(new StarAnnotator());
            ImageDataFactory realFactory = new(profile.Object, detector.Object, annotator.Object);
            Mock<IImageDataFactory> factory = new();
            Mock<ICameraMediator> camera = new() { DefaultValue = DefaultValue.Mock };
            var cameraInfo = camera.Object.GetInfo();
            cameraInfo.Connected = connected;
            cameraInfo.SensorType = sensor;
            IWindowServiceFactory windows = Mock.Of<IWindowServiceFactory>();
            Livestack plugin = new(profile.Object, factory.Object, windows);
            try {
                float[] pixels = Enumerable.Range(0, width * height).Select(i => 0.1f + 0.2f * ((i % 2) + 2 * ((i / width) % 2))).ToArray();
                ImageMetaData metadata = new();
                BaseImageData raw = realFactory.CreateBaseImageData(pixels.ToUShortArray(), width, height, 16, false, metadata);
                SensorType expectedPattern = automatic ? (connected ? sensor : SensorType.RGGB) : SensorType.GBRG;
                var expected = ImageUtility.Debayer(raw.RenderBitmapSource(), System.Drawing.Imaging.PixelFormat.Format16bppGrayScale, true, false, expectedPattern);
                ushort[]? redPixels = null;
                bool usesBitmapPath = expectedPattern is SensorType.RGBG or SensorType.GRGB or SensorType.GBGR or SensorType.BGRG;
                int calls = 0;
                factory.Setup(f => f.CreateBaseImageData(It.IsAny<ushort[]>(), width, height, 16, false, It.IsAny<ImageMetaData>()))
                    .Returns((ushort[] data, int w, int h, int depth, bool bayered, ImageMetaData meta) => {
                        if (++calls == 1 && usesBitmapPath) return raw;
                        redPixels = data;
                        throw new InvalidOperationException("Red channel captured");
                    });
                LivestackDockable dockable = new(profile.Object, Mock.Of<IApplicationStatusMediator>(), Mock.Of<IImageSaveMediator>(),
                    factory.Object, windows, camera.Object, Mock.Of<IMessageBroker>());
                Mock<IStarDetectionAnalysis> analysis = new();
                analysis.SetupGet(a => a.StarList).Returns(new List<DetectedStar>());
                LiveStackItem item = new("unused.fits", "target", "L", 60, 100, 10, -1, -1, width, height, 16, true, analysis.Object, metadata);
                LiveStackTab red = new(profile.Object, new LiveStackBag("target", LiveStackBag.RED_OSC,
                    new ImageProperties(width, height, 16, true, 100, 10), metadata, new()));
                using ImageBufferLease frame = ImageBufferPool.Shared.Rent(pixels.Length);
                pixels.CopyTo(frame.Buffer, 0);
                MethodInfo stack = typeof(LivestackDockable).GetMethod("StackOSC", BindingFlags.NonPublic | BindingFlags.Instance)!;
                InvalidOperationException? exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await (Task<bool>)stack.Invoke(dockable, new object[] { frame, item, red, Guid.NewGuid(), CancellationToken.None })!);
                Assert.That(exception!.Message, Is.EqualTo("Red channel captured"));
                Assert.That(redPixels, Is.EqualTo(expected.Data.Red));
            } finally {
                await plugin.Teardown();
            }
        }
    }
}
