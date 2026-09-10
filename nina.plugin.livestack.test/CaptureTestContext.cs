using Moq;
using NINA.Core.Model;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.FileFormat;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.LivestackDockables;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using Nito.AsyncEx;

namespace nina.plugin.livestack.test {
    internal sealed class CaptureTestContext : IAsyncDisposable {
        internal Mock<IProfileService> Profile { get; } = new() { DefaultValue = DefaultValue.Mock };
        internal Mock<IImageSaveMediator> ImageSave { get; } = new();
        internal Mock<IMessageBroker> Broker { get; } = new();
        internal Livestack Plugin { get; }
        internal LivestackDockable Dockable { get; }
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "LivestackCapture-" + Guid.NewGuid().ToString("N"));
        internal TaskCompletionSource Subscribed { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Func<object, BeforeFinalizeImageSavedEventArgs, Task>? Capture { get; private set; }

        internal CaptureTestContext() {
            Directory.CreateDirectory(DirectoryPath);
            Profile.SetupGet(p => p.ActiveProfile.PluginSettings).Returns(new PluginSettings());
            Profile.Setup(p => p.ActiveProfile.ImageFileSettings.GetFilePattern(It.IsAny<string>())).Returns("capture");
            ImageSave.SetupAdd(s => s.BeforeFinalizeImageSaved += It.IsAny<Func<object, BeforeFinalizeImageSavedEventArgs, Task>>())
                .Callback<Func<object, BeforeFinalizeImageSavedEventArgs, Task>>(handler => { Capture += handler; Subscribed.TrySetResult(); });
            ImageSave.SetupRemove(s => s.BeforeFinalizeImageSaved -= It.IsAny<Func<object, BeforeFinalizeImageSavedEventArgs, Task>>())
                .Callback<Func<object, BeforeFinalizeImageSavedEventArgs, Task>>(handler => {
                    Capture -= handler;
                    if (Capture == null) Subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                });
            Broker.Setup(b => b.Publish(It.IsAny<IMessage>())).Returns(Task.CompletedTask);
            IImageDataFactory factory = Mock.Of<IImageDataFactory>();
            IWindowServiceFactory windows = Mock.Of<IWindowServiceFactory>();
            Plugin = new Livestack(Profile.Object, factory, windows) { WorkingDirectory = DirectoryPath, HotpixelRemoval = false };
            Dockable = new(Profile.Object, Mock.Of<IApplicationStatusMediator>(), ImageSave.Object, factory, windows, Mock.Of<ICameraMediator>(), Broker.Object);
        }

        internal Mock<IImageData> Image(string imageType = "LIGHT") {
            Mock<IImageData> image = new();
            ImageMetaData metadata = new();
            metadata.Image.ImageType = imageType;
            metadata.Target.Name = "target";
            metadata.FilterWheel.Filter = "L";
            image.SetupGet(i => i.MetaData).Returns(metadata);
            image.SetupGet(i => i.Properties).Returns(new ImageProperties(16, 16, 16, false, 100, 10));
            image.SetupGet(i => i.Statistics).Returns(new AsyncLazy<IImageStatistics>(() => Task.FromResult(Mock.Of<IImageStatistics>())));
            Mock<IStarDetectionAnalysis> analysis = new();
            analysis.SetupGet(a => a.DetectedStars).Returns(1);
            analysis.SetupGet(a => a.StarList).Returns(new List<DetectedStar>());
            image.SetupGet(i => i.StarDetectionAnalysis).Returns(analysis.Object);
            image.Setup(i => i.GetImagePatterns()).Returns(new ImagePatterns());
            return image;
        }

        internal static BeforeFinalizeImageSavedEventArgs Event(IImageData image) {
            Mock<IRenderedImage> rendered = new();
            rendered.SetupGet(r => r.RawImageData).Returns(image);
            return new BeforeFinalizeImageSavedEventArgs(rendered.Object);
        }

        public async ValueTask DisposeAsync() {
            Dockable.StartLiveStackCommand.Cancel();
            if (Dockable.StartLiveStackCommand.ExecutionTask is Task running) await running.WaitAsync(TimeSpan.FromSeconds(5));
            await Plugin.Teardown();
            Directory.Delete(DirectoryPath, true);
        }
    }
}
