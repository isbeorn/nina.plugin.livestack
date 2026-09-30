using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Core.Utility.WindowService;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.QualityGate;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.ViewModel;
using Nito.AsyncEx;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.Plugin.Livestack.LivestackDockables {

    [Export(typeof(IDockableVM))]
    public partial class LivestackDockable : DockableVM, ISubscriber {

        public override bool IsTool { get; } = true;

        [ImportingConstructor]
        public LivestackDockable(IProfileService profileService,
                                 IApplicationStatusMediator applicationStatusMediator,
                                 IImageSaveMediator imageSaveMediator,
                                 IImageDataFactory imageDataFactory,
                                 IWindowServiceFactory windowServiceFactory,
                                 ICameraMediator cameraMediator,
                                 IMessageBroker messageBroker) : base(profileService) {
            this.Title = "Live Stack";
            var dict = new ResourceDictionary();
            dict.Source = new Uri("NINA.Plugin.Livestack;component/Options.xaml", UriKind.RelativeOrAbsolute);
            ImageGeometry = (System.Windows.Media.GeometryGroup)dict["Livestack_StackSVG"];
            ImageGeometry.Freeze();

            this.applicationStatusMediator = applicationStatusMediator;
            this.imageSaveMediator = imageSaveMediator;
            this.imageDataFactory = imageDataFactory;
            this.windowServiceFactory = windowServiceFactory;
            this.cameraMediator = cameraMediator;
            this.messageBroker = messageBroker;
            profileService.ProfileChanged += ProfileService_ProfileChanged;
            InitializeQualityGates();
            tabs = new AsyncObservableCollection<IStackTab>();
            IsExpanded = true;
            LivestackMediator.RegisterLivestackDockable(this);

            messageBroker.Subscribe("Livestack_LivestackDockable_StartLiveStack", this);
            messageBroker.Subscribe("Livestack_LivestackDockable_StopLiveStack", this);
        }

        private void ProfileService_ProfileChanged(object sender, EventArgs e) {
            foreach (var q in QualityGates) {
                q.PropertyChanged -= QualityGate_PropertyChanged;
            }
            InitializeQualityGates();
        }

        private void InitializeQualityGates() {
            QualityGates = new AsyncObservableCollection<IQualityGate>(LivestackMediator.PluginSettings.GetValueString(nameof(QualityGates), "").FromStringToList<IQualityGate>());
            foreach (var q in QualityGates) {
                q.PropertyChanged += QualityGate_PropertyChanged;
            }
        }

        [ObservableProperty]
        private bool isExpanded;

        [ObservableProperty]
        private AsyncObservableCollection<IQualityGate> qualityGates;

        [ObservableProperty]
        private AsyncObservableCollection<IStackTab> tabs;

        [ObservableProperty]
        private IStackTab selectedTab;

        private FrameProcessingSession activeSession;
        private bool disposed;
        public int QueueEntries => activeSession?.QueueEntries ?? 0;

        private readonly IApplicationStatusMediator applicationStatusMediator;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly IImageDataFactory imageDataFactory;
        private readonly IWindowServiceFactory windowServiceFactory;
        private readonly ICameraMediator cameraMediator;
        private readonly IMessageBroker messageBroker;

        [RelayCommand(IncludeCancelCommand = true)]
        private async Task StartLiveStack(CancellationToken token) {
            ObjectDisposedException.ThrowIf(disposed, this);
            Guid correlation = Guid.NewGuid();
            string workingDirectory = LivestackMediator.Plugin.WorkingDirectory;
            await using FrameProcessingSession session = new(async (item, frameToken) => {
                StatusUpdate("Received new frame", item);
                try {
                    await ProcessFrameAsync(item, correlation, frameToken);
                } finally {
                    applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = "Waiting for next frame" });
                }
            }, NotifyQueueEntriesChanged, token);
            Func<object, BeforeFinalizeImageSavedEventArgs, Task> receive = (sender, e) => {
                IImageData image = e.Image.RawImageData;
                return image.MetaData.Image.ImageType == "LIGHT" || image.MetaData.Image.ImageType == "SNAPSHOT"
                    ? session.EnqueueAsync(frameToken => PrepareFrameAsync(image, e.Patterns, frameToken, workingDirectory))
                    : Task.CompletedTask;
            };
            activeSession = session;
            try {
                IsExpanded = false;
                imageSaveMediator.BeforeFinalizeImageSaved += receive;
                _ = messageBroker.Publish(new LiveStackStatusBroadcast(LiveStackStatus.Running, correlation));
                applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = "Waiting for first frame" });
                await session.Completion;
            } finally {
                imageSaveMediator.BeforeFinalizeImageSaved -= receive;
                await session.DisposeAsync();
                await ReleaseCalibrationAsync();
                activeSession = null;
                NotifyQueueEntriesChanged();
                applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = "" });
                _ = messageBroker.Publish(new LiveStackStatusBroadcast(LiveStackStatus.Stopped, correlation));
                IsExpanded = true;
                LiveStackMemoryPressure.TrimAfterReleasingLargeBuffers("live stack stopped");
            }
        }

        public async Task StopAsync() {
            Task running = StartLiveStackCommand.ExecutionTask;
            StartLiveStackCommand.Cancel();
            if (running != null) await running;
            await ReleaseCalibrationAsync();
        }

        [RelayCommand]
        private async Task RemoveTab(IStackTab tab) {
            HashSet<IStackTab> removed = new() { tab };
            if (tab.Filter == LiveStackBag.RED_OSC || tab.Filter == LiveStackBag.GREEN_OSC || tab.Filter == LiveStackBag.BLUE_OSC) {
                foreach (LiveStackTab channelTab in Tabs.OfType<LiveStackTab>().Where(x => x.Target == tab.Target
                    && (x.Filter == LiveStackBag.RED_OSC || x.Filter == LiveStackBag.GREEN_OSC || x.Filter == LiveStackBag.BLUE_OSC))) {
                    removed.Add(channelTab);
                }
            }
            foreach (ColorCombinationTab colorTab in Tabs.OfType<ColorCombinationTab>()) {
                if (removed.OfType<LiveStackTab>().Any(colorTab.UsesSource)) {
                    removed.Add(colorTab);
                }
            }
            while (removed.Any(x => x.Locked)) {
                await Task.Delay(10);
            }
            if (removed.Contains(SelectedTab)) {
                SelectedTab = null;
            }
            foreach (IStackTab removedTab in removed) {
                Tabs.Remove(removedTab);
            }
            LiveStackMemoryPressure.TrimAfterReleasingLargeBuffers("stack tab removed");
        }

        [RelayCommand]
        private void DeleteQualityGate(IQualityGate obj) {
            obj.PropertyChanged -= QualityGate_PropertyChanged;
            QualityGates.Remove(obj);
            LivestackMediator.PluginSettings.SetValueString(nameof(QualityGates), QualityGates.FromListToString());
        }

        [RelayCommand]
        private async Task<bool> AddQualityGate() {
            var service = windowServiceFactory.Create();
            var prompt = new QualityGatePrompt();
            await service.ShowDialog(prompt, "Quality Gate Addition", System.Windows.ResizeMode.NoResize, System.Windows.WindowStyle.ToolWindow);

            if (prompt.Continue && prompt.SelectedGate != null) {
                prompt.SelectedGate.PropertyChanged += QualityGate_PropertyChanged;
                QualityGates.Add(prompt.SelectedGate);

                LivestackMediator.PluginSettings.SetValueString(nameof(QualityGates), QualityGates.FromListToString());
            }
            return prompt.Continue;
        }

        [RelayCommand]
        private async Task AddColorCombination(CancellationToken token) {
            var service = windowServiceFactory.Create();
            var prompt = new ColorCombinationPrompt(Tabs);
            await service.ShowDialog(prompt, "Color Combination Wizard", System.Windows.ResizeMode.NoResize, System.Windows.WindowStyle.ToolWindow);

            if (prompt.Continue) {
                if (!string.IsNullOrEmpty(prompt.Target)) {
                    var colorTab = new ColorCombinationTab(profileService, prompt.RedChannel, prompt.GreenChannel, prompt.BlueChannel);
                    Tabs.Add(colorTab);
                    await colorTab.Refresh(token);
                }
            }
        }

        private void QualityGate_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) {
            LivestackMediator.PluginSettings.SetValueString(nameof(QualityGates), QualityGates.FromListToString());
        }

        partial void OnSelectedTabChanged(IStackTab value) {
            if (value == null) {
                return;
            }

            _ = RefreshSelectedTabAsync(value);
        }

        private void NotifyQueueEntriesChanged() {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess() && !dispatcher.HasShutdownStarted) {
                _ = dispatcher.BeginInvoke(new Action(() => RaisePropertyChanged(nameof(QueueEntries))));
                return;
            }

            RaisePropertyChanged(nameof(QueueEntries));
        }

        private static bool NeedsStarDetection(IStarDetectionAnalysis analysis) {
            return analysis is null || analysis.DetectedStars <= 0;
        }

        private async Task RefreshSelectedTabAsync(IStackTab tab) {
            try {
                while (ReferenceEquals(SelectedTab, tab) && tab.Locked) {
                    await Task.Delay(25);
                }

                if (!ReferenceEquals(SelectedTab, tab)) {
                    return;
                }

                if (tab is ColorCombinationTab colorTab) {
                    if (colorTab.NeedsRefresh || colorTab.StackImage == null) {
                        await colorTab.Refresh(CancellationToken.None);
                    }
                } else if (tab is LiveStackTab liveTab && liveTab.StackImage == null) {
                    await liveTab.Refresh(CancellationToken.None);
                }
            } catch {
            }
        }

        private bool ItemPassesQuality(LiveStackItem item) {
            var failedGates = QualityGates.Where(x => !x.Passes(item));
            if (failedGates.Any()) {
                var failedGatesInfo = "Live Stack - Image ignored as it does not meet quality gate critera." + Environment.NewLine + string.Join(Environment.NewLine, failedGates.Select(x => $"{x.Name}: {x.Value}"));
                Logger.Warning(failedGatesInfo);
                Notification.ShowWarning(failedGatesInfo);
                return false;
            }
            return true;
        }

        private LiveStackTab GetOrCreateStackBag(LiveStackItem item) {
            var target = string.IsNullOrWhiteSpace(item.Target) ? LiveStackBag.NOTARGET : item.Target;
            var filter = string.IsNullOrWhiteSpace(item.Filter) ? LiveStackBag.NOFILTER : item.Filter;
            if (item.IsBayered) { filter = LiveStackBag.RED_OSC; }

            var tab = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == filter && x.Target == target);
            if (tab == null) {
                List<Accord.Point> stars = null;
                var bag = new LiveStackBag(target, filter, GetFrameProperties(item), item.MetaData, stars);
                tab = new LiveStackTab(profileService, bag);
                Tabs.Add(tab);
                return tab as LiveStackTab;
            }
            return tab as LiveStackTab;
        }

        private async Task<bool> StackMono(ImageBufferLease theImageArray, LiveStackItem item, LiveStackTab tab, Guid correlation, CancellationToken token) {
            StatusUpdate("Aligning frame", item);
            var stars = LivestackMediator.GetImageTransformer().GetStars(item.StarList, item.Width, item.Height);
            AlignmentResult alignment = tab.AlignAndAdd(theImageArray, GetFrameProperties(item), stars, token);
            theImageArray.Dispose();
            LogAlignment(alignment, item);
            if (!alignment.Success) {
                return false;
            }

            StatusUpdate("Rendering stack", item);
            await tab.Refresh(token);
            SaveAndPublishStacks(item, correlation, tab);
            return true;
        }

        private async Task<bool> StackOSC(ImageBufferLease theImageArray, LiveStackItem item, LiveStackTab redTab, Guid correlation, CancellationToken token) {
            var meta = new ImageMetaData(); // Set bare minimum for star detection resize factor
            meta.Camera.PixelSize = profileService.ActiveProfile.CameraSettings.PixelSize;
            meta.Telescope.FocalLength = profileService.ActiveProfile.TelescopeSettings.FocalLength;
            StatusUpdate("Debayering", item);

            var bayerPattern = SensorType.RGGB;
            if (profileService.ActiveProfile.CameraSettings.BayerPattern != BayerPatternEnum.Auto) {
                bayerPattern = (SensorType)profileService.ActiveProfile.CameraSettings.BayerPattern;
            } else if (cameraMediator.GetInfo() is { Connected: true } cameraInfo) {
                bayerPattern = cameraInfo.SensorType;
            }
            if (BayerChannelExtractor.TryExtract(theImageArray.Buffer, item.Width, item.Height, bayerPattern, out LRGBArrays channels)) {
                theImageArray.Dispose();
            } else {
                var theImageArrayData = imageDataFactory.CreateBaseImageData(theImageArray.Buffer.ToUShortArray(), item.Width, item.Height, 16, false, meta);
                theImageArray.Dispose();
                var image = theImageArrayData.RenderBitmapSource();
                channels = ImageUtility.Debayer(image, System.Drawing.Imaging.PixelFormat.Format16bppGrayScale, true, false, bayerPattern).Data;
            }

            StatusUpdate("Aligning frame - red channel", item);
            var redChannelData = imageDataFactory.CreateBaseImageData(channels.Red, item.Width, item.Height, redTab.Properties.BitDepth, false, meta);
            // We only need to detect the stars in one channel for OSC. The others should match.
            var channelStatistics = await redChannelData.Statistics;
            if (NeedsStarDetection(redChannelData.StarDetectionAnalysis)) {
                var render = redChannelData.RenderImage();
                render = await render.Stretch(profileService.ActiveProfile.ImageSettings.AutoStretchFactor, profileService.ActiveProfile.ImageSettings.BlackClipping, profileService.ActiveProfile.ImageSettings.UnlinkedStretch);
                render = await render.DetectStars(false, profileService.ActiveProfile.ImageSettings.StarSensitivity, profileService.ActiveProfile.ImageSettings.NoiseReduction, token, default);
                redChannelData.StarDetectionAnalysis = render.RawImageData.StarDetectionAnalysis;
            }

            var redChannelStarList = redChannelData.StarDetectionAnalysis?.StarList;
            var stars = LivestackMediator.GetImageTransformer().GetStars(redChannelStarList, item.Width, item.Height);

            var imageProperties = GetFrameProperties(item);
            var greenTab = Tabs.OfType<LiveStackTab>().FirstOrDefault(x => x.Filter == LiveStackBag.GREEN_OSC && x.Target == redTab.Target);
            var blueTab = Tabs.OfType<LiveStackTab>().FirstOrDefault(x => x.Filter == LiveStackBag.BLUE_OSC && x.Target == redTab.Target);
            if ((greenTab != null && !greenTab.IsCompatible(imageProperties)) || (blueTab != null && !blueTab.IsCompatible(imageProperties))) {
                LogAlignment(AlignmentResult.Rejected("OSC channel dimensions or capture settings differ from the reference."), item);
                return false;
            }

            // Solve once in red. The validated matrix also handles a meridian flip directly.
            AlignmentResult alignment = redTab.AlignAndAdd(channels.Red, imageProperties, stars, token);
            LogAlignment(alignment, item);
            if (!alignment.Success) {
                return false;
            }
            double[,] matrix = alignment.Matrix;
            if (greenTab == null) {
                greenTab = new LiveStackTab(profileService, new LiveStackBag(redTab.Target, LiveStackBag.GREEN_OSC, imageProperties, item.MetaData, redTab.ReferenceStars));
                Tabs.Add(greenTab);
            }
            if (blueTab == null) {
                blueTab = new LiveStackTab(profileService, new LiveStackBag(redTab.Target, LiveStackBag.BLUE_OSC, imageProperties, item.MetaData, redTab.ReferenceStars));
                Tabs.Add(blueTab);
            }
            greenTab.AddTransformedImage(channels.Green, matrix, false);
            blueTab.AddTransformedImage(channels.Blue, matrix, false);

            await redTab.Refresh(token);
            await greenTab.Refresh(token);
            await blueTab.Refresh(token);

            var colorTab = Tabs.Where(x => x is ColorCombinationTab && x.Target == redTab.Target).FirstOrDefault() as ColorCombinationTab;
            if (colorTab == null) {
                colorTab = new ColorCombinationTab(profileService, redTab, greenTab, blueTab, channelsAlreadyAligned: true);
                Tabs.Add(colorTab);
            }
            SaveAndPublishStacks(item, correlation, redTab, greenTab, blueTab);
            return true;
        }

        private void SaveAndPublishStacks(LiveStackItem item, Guid correlation, params LiveStackTab[] tabs) {
            if (LivestackMediator.Plugin.SaveStackedLights) {
                StatusUpdate(tabs.Length == 1 ? "Saving stack" : "Saving stacks", item);
                foreach (LiveStackTab tab in tabs) {
                    tab.SaveToDisk();
                }
            }
            // Finish every channel save before publishing any update for this frame.
            foreach (LiveStackTab tab in tabs) {
                _ = messageBroker.Publish(new LivestackBroadcast(LiveStackBroadcastContent.Monochrome(tab.StackCount, tab.Filter, tab.Target, tab.StackImage), correlation));
            }
        }

        private async Task<bool> StackItem(LiveStackItem item, Guid correlation, CancellationToken token) {
            var tab = GetOrCreateStackBag(item);
            if (!tab.IsCompatible(GetFrameProperties(item))) {
                LogAlignment(AlignmentResult.Rejected("Frame dimensions or capture settings differ from the reference. Start a new stack for this capture setup."), item);
                return false;
            }
            tab.Locked = true;
            try {
                if (SelectedTab == null) {
                    SelectedTab = tab;
                }

                using ImageBufferLease calibratedFrame = CalibrateFrame(item, token);

                SaveCalibratedFrameIfNeeded(calibratedFrame.Buffer, item);

                RemoveHotpixelsIfNeeded(calibratedFrame.Buffer, item);

                bool added = item.IsBayered
                    ? await StackOSC(calibratedFrame, item, tab, correlation, token)
                    : await StackMono(calibratedFrame, item, tab, correlation, token);
                if (!added) {
                    return false;
                }

                var colorTab = Tabs.Where(x => x is ColorCombinationTab && x.Target == tab.Target).FirstOrDefault() as ColorCombinationTab;
                if (colorTab != null) {
                    colorTab.MarkDirty();
                    // Broker subscribers need a current RGB image regardless of tab selection.
                    StatusUpdate("Refreshing color combined stack", item);
                    await colorTab.Refresh(token);

                    if (LivestackMediator.Plugin.SaveStackedLights) {
                        StatusUpdate("Saving color combined stack", item);
                        colorTab.AutoSaveToDisk();
                    }

                    _ = messageBroker.Publish(new LivestackBroadcast(LiveStackBroadcastContent.Color(colorTab.StackCountRed, colorTab.StackCountGreen, colorTab.StackCountBlue, colorTab.Filter, colorTab.Target, colorTab.StackImage), correlation));
                }
                return true;
            } finally {
                tab.Locked = false;
            }
        }

        private ImageBufferLease CalibrateFrame(LiveStackItem item, CancellationToken token) {
            StatusUpdate("Calibrating frame", item);
            calibrationManager ??= LivestackMediator.CreateCalibrationManager();
            RegisterCalibrationMasters(calibrationManager);
            ImageBufferLease frame = ImageBufferPool.Shared.Rent(AffineResampler.GetLength(item.Width, item.Height));
            try {
                using CFitsioFITSReader reader = new(item.Path);
                calibrationManager.ApplyLightFrameCalibrationInto(reader, frame.Buffer, item.Width, item.Height, item.ExposureTime, item.Gain, item.Offset, item.Filter, item.IsBayered, token);
                return frame;
            } catch {
                frame.Dispose();
                throw;
            }
        }

        private void SaveCalibratedFrameIfNeeded(float[] theImageArray, LiveStackItem item) {
            if (LivestackMediator.Plugin.SaveCalibratedLights) {
                var fileName = Path.GetFileNameWithoutExtension(item.Path) + "_c" + ".fits";

                var destinationFolder = Path.Combine(LivestackMediator.Plugin.WorkingDirectory, "calibrated", "light", item.Target, item.Filter);
                if (!Directory.Exists(destinationFolder)) {
                    Directory.CreateDirectory(destinationFolder);
                }
                var destinationFile = CoreUtil.GetUniqueFilePath(Path.Combine(destinationFolder, fileName), "{0}_{1}");

                StatusUpdate($"Saving calibrated light frame at {destinationFile}", item);
                var writer = new CFitsioFITSExtendedWriter(destinationFile, theImageArray, item.Width, item.Height);
                writer.PopulateHeaderCards(item.MetaData);
                writer.Close();
            }
        }

        private void RemoveHotpixelsIfNeeded(float[] theImageArray, LiveStackItem item) {
            if (LivestackMediator.Plugin.HotpixelRemoval) {
                StatusUpdate("Removing hot pixels in frame", item);
                LivestackMediator.GetImageMath().RemoveHotPixelOutliers(theImageArray, item.Width, item.Height);
            }
        }

        private void StatusUpdate(string status, LiveStackItem item) {
            if (!string.IsNullOrEmpty(status)) {
                Logger.Info($"{status} - {item.Path}");
            }
            applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = status });
        }

        private static ImageProperties GetFrameProperties(LiveStackItem item) {
            return new ImageProperties(item.Width, item.Height, item.BitDepth, item.IsBayered, item.Gain, item.Offset);
        }

        private static void LogAlignment(AlignmentResult result, LiveStackItem item) {
            string message = $"Live Stack alignment: {result}; Target=\"{item.Target}\"; Filter=\"{item.Filter}\"; Frame=\"{item.Path}\"";
            if (result.Success) {
                Logger.Info(message);
            } else {
                Logger.Warning(message);
            }
        }

        private void RegisterCalibrationMasters(ICalibrationManager calibrationManager) {
            calibrationManager.ClearRegisteredMasters();
            foreach (var meta in LivestackMediator.CalibrationVM.BiasLibrary) {
                calibrationManager.RegisterBiasMaster(meta);
            }
            foreach (var meta in LivestackMediator.CalibrationVM.DarkLibrary) {
                calibrationManager.RegisterDarkMaster(meta);
            }
            foreach (var meta in LivestackMediator.CalibrationVM.FlatLibrary) {
                calibrationManager.RegisterFlatMaster(meta);
            }
            foreach (var meta in LivestackMediator.CalibrationVM.SessionFlatLibrary) {
                calibrationManager.RegisterFlatMaster(meta);
            }
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            activeSession?.Cancel();
            if (frameProcessing.Wait(0)) {
                try { ResetCalibration(); } finally { frameProcessing.Release(); }
            }
            profileService.ProfileChanged -= ProfileService_ProfileChanged;
            foreach (IQualityGate gate in QualityGates) gate.PropertyChanged -= QualityGate_PropertyChanged;
            messageBroker.Unsubscribe("Livestack_LivestackDockable_StartLiveStack", this);
            messageBroker.Unsubscribe("Livestack_LivestackDockable_StopLiveStack", this);
        }

        public async Task OnMessageReceived(IMessage message) {
            if (message.Topic == $"Livestack_LivestackDockable_StartLiveStack") {
                if (LivestackMediator.LiveStackDockable.StartLiveStackCommand.IsRunning) {
                    return;
                }
                await Application.Current.Dispatcher.BeginInvoke(() => StartLiveStackCommand.ExecuteAsync(null));
            } else if (message.Topic == $"Livestack_LivestackDockable_StopLiveStack") {
                if (LivestackMediator.LiveStackDockable.StartLiveStackCommand.IsRunning) {
                    await Application.Current.Dispatcher.BeginInvoke(() => LivestackMediator.LiveStackDockable.StartLiveStackCancelCommand.Execute(null));
                }
            }
        }
    }

    public class LivestackBroadcast : IMessage {

        public LivestackBroadcast(object content, Guid correlation) {
            Content = content;
            CorrelationId = correlation;
        }

        public Guid SenderId => Guid.Parse(LivestackMediator.Plugin.Identifier);

        public string Sender => "Livestack";

        public DateTimeOffset SentAt => DateTimeOffset.UtcNow;

        public Guid MessageId => Guid.NewGuid();

        public DateTimeOffset? Expiration => null;

        public Guid? CorrelationId { get; }

        public int Version => 1;

        public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();

        public string Topic => "Livestack_LivestackDockable_StackUpdateBroadcast";

        public object Content { get; }
    }

    public class LiveStackBroadcastContent {

        private LiveStackBroadcastContent(bool isMonochrome, int? stackCount, int? redStackCount, int? greenStackCount, int? blueStackCount, string filter, string target, BitmapSource image) {
            IsMonochrome = isMonochrome;
            StackCount = stackCount;
            RedStackCount = redStackCount;
            GreenStackCount = greenStackCount;
            BlueStackCount = blueStackCount;
            Filter = filter;
            Target = target;
            Image = image;
        }

        public static LiveStackBroadcastContent Monochrome(int stackCount, string filter, string target, BitmapSource image) {
            return new LiveStackBroadcastContent(
                true,
                stackCount,
                null,
                null,
                null,
                filter,
                target,
                image
            );
        }

        public static LiveStackBroadcastContent Color(int redStackCount, int greenStackCount, int blueStackCount, string filter, string target, BitmapSource image) {
            return new LiveStackBroadcastContent(
                false,
                null,
                redStackCount,
                greenStackCount,
                blueStackCount,
                filter,
                target,
                image
            );
        }

        public bool IsMonochrome { get; }
        public int? StackCount { get; } // Only used for monochrome

        // Only used for color
        public int? RedStackCount { get; }

        public int? GreenStackCount { get; }
        public int? BlueStackCount { get; }

        public string Filter { get; }
        public string Target { get; }
        public BitmapSource Image { get; }
    }

    public class LiveStackStatusBroadcast : IMessage {

        public LiveStackStatusBroadcast(LiveStackStatus status, Guid correlation) {
            Content = status.ToString();
            CorrelationId = correlation;
        }

        public Guid SenderId => Guid.Parse(LivestackMediator.Plugin.Identifier);

        public string Sender => "Livestack";

        public DateTimeOffset SentAt => DateTimeOffset.UtcNow;

        public Guid MessageId => Guid.NewGuid();

        public DateTimeOffset? Expiration => null;

        public Guid? CorrelationId { get; }

        public int Version => 1;

        public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();

        public string Topic => "Livestack_LivestackDockable_StatusBroadcast";

        public object Content { get; }
    }

    public enum LiveStackStatus {

        [Description("running")]
        Running,

        [Description("stopped")]
        Stopped
    }
}
