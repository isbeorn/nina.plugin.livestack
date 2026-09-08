using Moq;
using NINA.Core.Enum;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.LivestackDockables;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.IO;

namespace NINA.Plugin.Livestack.Replay {
    // Only N.I.N.A.'s host services are substituted. Image processing uses the plugin and N.I.N.A. assemblies.
    public sealed class ReplayHost : IAsyncDisposable {
        private readonly ImageDataFactory factory;
        private readonly CameraSettings camera = new();
        private readonly ILogger originalLogger;
        private readonly Logger replayLogger;
        public Livestack Plugin { get; }
        public LivestackDockable Dockable { get; }
        public string WorkingDirectory { get; } = Path.Combine(Path.GetTempPath(), "Livestack-Replay", Guid.NewGuid().ToString("N"));

        public ReplayHost(IProgress<string> status, IProgress<string> log) {
            Directory.CreateDirectory(WorkingDirectory);
            Mock<IProfileService> profile = new() { DefaultValue = DefaultValue.Mock };
            profile.SetupGet(p => p.ActiveProfile.PluginSettings).Returns(new PluginSettings());
            profile.SetupGet(p => p.ActiveProfile.ImageSettings).Returns(new ImageSettings {
                AutoStretchFactor = 0.2, BlackClipping = -2.8, StarSensitivity = StarSensitivityEnum.High, NoiseReduction = NoiseReductionEnum.None
            });
            profile.SetupGet(p => p.ActiveProfile.CameraSettings).Returns(camera);
            profile.SetupGet(p => p.ActiveProfile.FocuserSettings).Returns(new FocuserSettings {
                AutoFocusInnerCropRatio = 1, AutoFocusOuterCropRatio = 1, AutoFocusUseBrightestStars = 0
            });
            profile.SetupGet(p => p.ActiveProfile.TelescopeSettings).Returns(new TelescopeSettings());
            profile.Setup(p => p.ActiveProfile.ImageFileSettings.GetFilePattern(It.IsAny<string>())).Returns(() => "replay_" + Guid.NewGuid().ToString("N"));
            Mock<IPluggableBehaviorSelector<IStarDetection>> detector = new();
            detector.Setup(d => d.GetBehavior()).Returns(new StarDetection());
            Mock<IPluggableBehaviorSelector<IStarAnnotator>> annotator = new();
            annotator.Setup(a => a.GetBehavior()).Returns(new StarAnnotator());
            factory = new ImageDataFactory(profile.Object, detector.Object, annotator.Object);
            IWindowServiceFactory windows = Mock.Of<IWindowServiceFactory>();
            Plugin = new Livestack(profile.Object, factory, windows) { WorkingDirectory = WorkingDirectory, DefaultDownsample = 2 };
            Mock<IApplicationStatusMediator> statuses = new();
            statuses.Setup(s => s.StatusUpdate(It.IsAny<ApplicationStatus>())).Callback<ApplicationStatus>(s => status.Report(s.Status));
            Dockable = new LivestackDockable(profile.Object, statuses.Object, Mock.Of<IImageSaveMediator>(), factory, windows, Mock.Of<ICameraMediator>(), new OfflineMessageBroker());
            NINA.Core.Utility.Logger.Info("Live Stack Replay initialized.");
            originalLogger = Serilog.Log.Logger;
            replayLogger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(new LogSink(log)).CreateLogger();
            Serilog.Log.Logger = replayLogger;
        }

        public async Task<bool> ProcessFileAsync(string path, string sensorMode, Guid session, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            camera.BayerPattern = sensorMode == "Mono" ? BayerPatternEnum.RGGB : Enum.Parse<BayerPatternEnum>(sensorMode);
            IImageData image = await BaseImageData.FromFile(path, 16, sensorMode != "Mono", null, factory);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image.MetaData.Image.ImageType)) image.MetaData.Image.ImageType = "LIGHT";
            if (string.IsNullOrWhiteSpace(image.MetaData.Target.Name)) image.MetaData.Target.Name = LiveStackBag.NOTARGET;
            if (string.IsNullOrWhiteSpace(image.MetaData.FilterWheel.Filter)) image.MetaData.FilterWheel.Filter = LiveStackBag.NOFILTER;
            return await Dockable.ProcessImageAsync(image, session, token);
        }

        public CalibrationFrameMeta ReadMaster(string path, CalibrationFrameType type) {
            using CFitsioFITSReader reader = new(path);
            ImageMetaData metadata = reader.ReadHeader().ExtractMetaData();
            double sum = 0;
            float[] row = new float[reader.Width];
            if (type == CalibrationFrameType.FLAT) {
                for (int y = 0; y < reader.Height; y++) {
                    reader.ReadPixelRowAsFloat(y, row);
                    foreach (float pixel in row) sum += pixel;
                }
            }
            return new CalibrationFrameMeta(type, path, metadata.Camera.Gain, metadata.Camera.Offset,
                double.IsFinite(metadata.Image.ExposureTime) ? metadata.Image.ExposureTime : 0,
                string.IsNullOrWhiteSpace(metadata.FilterWheel.Filter) ? LiveStackBag.NOFILTER : metadata.FilterWheel.Filter,
                reader.Width, reader.Height, type == CalibrationFrameType.FLAT ? (float)(sum / ((long)reader.Width * reader.Height)) : float.NaN);
        }

        public async ValueTask DisposeAsync() {
            Dockable.SelectedTab = null!;
            Dockable.Tabs.Clear();
            await Plugin.Teardown();
            if (ReferenceEquals(Serilog.Log.Logger, replayLogger)) Serilog.Log.Logger = originalLogger;
            replayLogger.Dispose();
            // This generated directory contains only this replay's temporary copies and optional outputs.
            Directory.Delete(WorkingDirectory, true);
        }

        // No external subscribers exist in the replay. Recording broadcasts would retain every preview bitmap.
        private sealed class OfflineMessageBroker : IMessageBroker {
            public Task Publish(IMessage message) => Task.CompletedTask;
            public void Subscribe(string topic, ISubscriber subscriber) { }
            public void Unsubscribe(string topic, ISubscriber subscriber) { }
        }

        private sealed class LogSink(IProgress<string> progress) : ILogEventSink {
            public void Emit(LogEvent entry) {
                progress.Report($"{entry.Timestamp:HH:mm:ss} {entry.Level}: {entry.RenderMessage()}" + (entry.Exception == null ? "" : " " + entry.Exception.Message));
            }
        }
    }
}
