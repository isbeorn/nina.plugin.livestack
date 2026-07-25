using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.MyMessageBox;
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
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.Plugin.Livestack.LivestackDockables {

    [Export(typeof(IDockableVM))]
    public partial class LivestackDockable : DockableVM, ISubscriber {
        private const int MinimumAffineStarCount = 3;

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
            profileService.ActiveProfile.PropertyChanged += ActiveProfile_PropertyChanged;
            InitializeQualityGates();
            tabs = new AsyncObservableCollection<IStackTab>();
            IsExpanded = true;
            LivestackMediator.RegisterLivestackDockable(this);

            messageBroker.Subscribe("Livestack_LivestackDockable_StartLiveStack", this);
            messageBroker.Subscribe("Livestack_LivestackDockable_StopLiveStack", this);
        }

        private void ActiveProfile_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) {
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

        private int queueEntries;
        public int QueueEntries { get => queueEntries; }

        private Channel<LiveStackItem> channel;
        private readonly IApplicationStatusMediator applicationStatusMediator;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly IImageDataFactory imageDataFactory;
        private readonly IWindowServiceFactory windowServiceFactory;
        private readonly ICameraMediator cameraMediator;
        private readonly IMessageBroker messageBroker;

        // Serializes stack mutations so a manual reset can never interleave with a frame that is currently being stacked
        private readonly SemaphoreSlim stackMutationLock = new SemaphoreSlim(1, 1);

        private Guid? stackSessionId = null;

        [RelayCommand(IncludeCancelCommand = true)]
        private Task StartLiveStack(CancellationToken token) {
            return Task.Run(async () => {
                try {
                    IsExpanded = false;
                    ResetQueueEntries();
                    channel = Channel.CreateBounded<LiveStackItem>(1000);
                    var localQueue = channel;
                    this.imageSaveMediator.BeforeFinalizeImageSaved += ImageSaveMediator_BeforeFinalizeImageSaved;
                    this.stackSessionId = Guid.NewGuid();
                    _ = messageBroker.Publish(new LiveStackStatusBroadcast(LiveStackStatus.Running, this.stackSessionId.Value));
                    applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = "Waiting for first frame" });

                    try {
                        await foreach (var item in channel.Reader.ReadAllAsync(token)) {
                            try {
                                StatusUpdate("Received new frame", item);
                                DecrementQueueEntries();

                                try {
                                    if (item.StarList.Count < 8) {
                                        Logger.Info($"Skipping frame as not enough stars have been detected ({item.StarList.Count})");
                                        continue;
                                    }

                                    if (!ItemPassesQuality(item)) {
                                        continue;
                                    }

                                    await StackItem(item, token);
                                } finally {
                                    try {
                                        File.Delete(item.Path);
                                    } finally {
                                        LiveStackMemoryPressure.CollectIfNeeded("frame completed");
                                    }
                                }

                            } catch (OperationCanceledException) {
                            } catch (Exception ex) {
                                Logger.Error(ex);
                            } finally {
                                applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = "Waiting for next frame" });
                            }
                        }
                    } catch (OperationCanceledException) { }

                    if (localQueue != null) {
                        try {
                            localQueue.Writer.TryComplete();
                            await foreach (var item in channel.Reader.ReadAllAsync()) {
                                StatusUpdate("Flushing queue", item);
                                File.Delete(item.Path);
                            }
                        } catch { }
                    }
                } finally {
                    applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = "" });
                    this.imageSaveMediator.BeforeFinalizeImageSaved -= ImageSaveMediator_BeforeFinalizeImageSaved;
                    _ = messageBroker.Publish(new LiveStackStatusBroadcast(LiveStackStatus.Stopped, this.stackSessionId.Value));
                    this.stackSessionId = null;
                    IsExpanded = true;
                    ResetQueueEntries();
                    LiveStackMemoryPressure.CompactAfterReleasingLargeBuffers("live stack stopped");
                }
            });
        }

        [RelayCommand]
        private async Task ResetStacks(CancellationToken token) {
            var tabsToReset = Tabs.ToList();
            if (tabsToReset.Count == 0) {
                Notification.ShowInformation("Live Stack - There is no stack to reset");
                return;
            }

            var confirmation = MyMessageBox.Show(
                "This will throw away the current live stack and close all stack tabs." + Environment.NewLine
                    + "The next accepted frame will start a new stack and become its alignment reference frame." + Environment.NewLine + Environment.NewLine
                    + "Do you want to reset the stack?",
                "Reset live stack",
                MessageBoxButton.OKCancel,
                MessageBoxResult.Cancel);

            if (confirmation != MessageBoxResult.OK) {
                return;
            }

            await stackMutationLock.WaitAsync(token);
            try {
                Logger.Info($"Live Stack reset on user request. Closing {tabsToReset.Count} stack tab(s) and releasing their stack buffers. The next accepted frame will start a new stack.");
                SelectedTab = null;
                Tabs.Clear();

                // Release the large stack buffers explicitly instead of waiting for the removed tabs to become unreachable
                foreach (var tab in tabsToReset) {
                    tab.ResetStack();
                }

                if (stackSessionId.HasValue) {
                    foreach (var tab in tabsToReset) {
                        if (tab is ColorCombinationTab colorTab) {
                            _ = messageBroker.Publish(new LivestackBroadcast(LiveStackBroadcastContent.Color(0, 0, 0, colorTab.Filter, colorTab.Target, null), stackSessionId.Value));
                        } else {
                            _ = messageBroker.Publish(new LivestackBroadcast(LiveStackBroadcastContent.Monochrome(0, tab.Filter, tab.Target, null), stackSessionId.Value));
                        }
                    }
                }
            } finally {
                stackMutationLock.Release();
            }

            await Task.Run(() => LiveStackMemoryPressure.CompactAfterReleasingLargeBuffers("stacks reset"));
            applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = "Stack reset - waiting for first frame" });
            Notification.ShowInformation("Live Stack - The stack has been reset. The next accepted frame will start a new stack.");
        }

        [RelayCommand]
        private async Task RemoveTab(IStackTab tab) {
            while (tab.Locked) {
                await Task.Delay(10);
            }

            var colorTab = Tabs.Where(x => x is ColorCombinationTab && x.Target == tab.Target).FirstOrDefault() as ColorCombinationTab;
            if (tab.Filter == LiveStackBag.RED_OSC || tab.Filter == LiveStackBag.GREEN_OSC || tab.Filter == LiveStackBag.BLUE_OSC) {
                var red = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == LiveStackBag.RED_OSC && x.Target == tab.Target);
                var green = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == LiveStackBag.GREEN_OSC && x.Target == tab.Target);
                var blue = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == LiveStackBag.BLUE_OSC && x.Target == tab.Target);
                Tabs.Remove(red);
                Tabs.Remove(green);
                Tabs.Remove(blue);
            } else {
                Tabs.Remove(tab);
            }
            await Task.Run(() => LiveStackMemoryPressure.CompactAfterReleasingLargeBuffers("stack tab removed"));
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

        private async Task ImageSaveMediator_BeforeFinalizeImageSaved(object sender, BeforeFinalizeImageSavedEventArgs e) {
            if (e.Image.RawImageData.MetaData.Image.ImageType == NINA.Equipment.Model.CaptureSequence.ImageTypes.LIGHT || e.Image.RawImageData.MetaData.Image.ImageType == NINA.Equipment.Model.CaptureSequence.ImageTypes.SNAPSHOT) {
                _ = Task.Run(async () => {
                    try {
                        var statistics = await e.Image.RawImageData.Statistics;
                        var starDetectionAnalysis = e.Image.RawImageData.StarDetectionAnalysis;
                        if (NeedsStarDetection(starDetectionAnalysis)) {
                            var render = e.Image.RawImageData.RenderImage();
                            render = await render.Stretch(profileService.ActiveProfile.ImageSettings.AutoStretchFactor, profileService.ActiveProfile.ImageSettings.BlackClipping, profileService.ActiveProfile.ImageSettings.UnlinkedStretch);
                            render = await render.DetectStars(false, profileService.ActiveProfile.ImageSettings.StarSensitivity, profileService.ActiveProfile.ImageSettings.NoiseReduction, default, default);
                            starDetectionAnalysis = render.RawImageData.StarDetectionAnalysis;
                        }

                        // Only retrieve the filename part of the pattern
                        var pattern = Path.GetFileName(profileService.ActiveProfile.ImageFileSettings.GetFilePattern(e.Image.RawImageData.MetaData.Image.ImageType));

                        var path = await e.Image.RawImageData.SaveToDisk(
                            new NINA.Image.FileFormat.FileSaveInfo() {
                                FilePath = Path.Combine(LivestackMediator.Plugin.WorkingDirectory, "temp"),
                                FilePattern = pattern,
                                FileType = Core.Enum.FileTypeEnum.FITS
                            },
                            default, true, e.Patterns
                        );
                        await channel.Writer.WriteAsync(new LiveStackItem(path: path,
                                                                   target: e.Image.RawImageData.MetaData.Target.Name,
                                                                   filter: e.Image.RawImageData.MetaData.FilterWheel.Filter,
                                                                   exposureTime: e.Image.RawImageData.MetaData.Image.ExposureTime,
                                                                   gain: e.Image.RawImageData.MetaData.Camera.Gain,
                                                                   offset: e.Image.RawImageData.MetaData.Camera.Offset,
                                                                   width: e.Image.RawImageData.Properties.Width,
                                                                   height: e.Image.RawImageData.Properties.Height,
                                                                   bitDepth: (int)profileService.ActiveProfile.CameraSettings.BitDepth,
                                                                   isBayered: e.Image.RawImageData.Properties.IsBayered,
                                                                   analysis: starDetectionAnalysis,
                                                                   metaData: e.Image.RawImageData.MetaData));

                        IncrementQueueEntries();
                    } catch (Exception ex) {
                        Logger.Error(ex);
                    }
                });
            }
        }

        private void ResetQueueEntries() {
            Interlocked.Exchange(ref queueEntries, 0);
            NotifyQueueEntriesChanged();
        }

        private void IncrementQueueEntries() {
            Interlocked.Increment(ref queueEntries);
            NotifyQueueEntriesChanged();
        }

        private void DecrementQueueEntries() {
            int updated = Interlocked.Decrement(ref queueEntries);
            if (updated < 0) {
                Interlocked.Exchange(ref queueEntries, 0);
            }
            NotifyQueueEntriesChanged();
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
                var bag = new LiveStackBag(target, filter, new ImageProperties(item.Width, item.Height, (int)profileService.ActiveProfile.CameraSettings.BitDepth, item.IsBayered, item.Gain, item.Offset), item.MetaData, stars);
                tab = new LiveStackTab(profileService, bag);
                Tabs.Add(tab);
                return tab as LiveStackTab;
            }
            return tab as LiveStackTab;
        }

        private async Task StackMono(float[] theImageArray, LiveStackItem item, LiveStackTab tab, Guid correlation, CancellationToken token) {
            if (tab.StackCount == 0 || !HasEnoughAlignmentStars(tab.ReferenceStars)) {
                var stars = LivestackMediator.GetImageTransformer().GetStars(item.StarList, item.Width, item.Height);
                if (!HasEnoughAlignmentStars(stars)) {
                    LogSkippedForInsufficientAlignmentStars("mono reference", item, GetRawStarCount(item), stars.Count, tab.ReferenceStars?.Count);
                    return;
                }

                if (tab.ReferenceStars != null && !HasEnoughAlignmentStars(tab.ReferenceStars)) {
                    Logger.Warning($"Live Stack replacing invalid mono reference. Old reference stars={tab.ReferenceStars.Count}; New reference stars={stars.Count}; Required={MinimumAffineStarCount}; Target=\"{item.Target}\"; Filter=\"{item.Filter}\"; Frame=\"{item.Path}\"");
                }

                tab.ForcePushReference(new ImageProperties(item.Width, item.Height, (int)profileService.ActiveProfile.CameraSettings.BitDepth, item.IsBayered, item.Gain, item.Offset), stars, theImageArray);
                LogReferenceAccepted("mono", item, GetRawStarCount(item), stars.Count);
            } else {
                StatusUpdate("Aligning frame", item);
                var stars = LivestackMediator.GetImageTransformer().GetStars(item.StarList, item.Width, item.Height);
                if (!HasEnoughAlignmentStars(stars) || !HasEnoughAlignmentStars(tab.ReferenceStars)) {
                    LogSkippedForInsufficientAlignmentStars("mono frame", item, GetRawStarCount(item), stars.Count, tab.ReferenceStars?.Count);
                    return;
                }

                var affineTransformationMatrix = LivestackMediator.GetImageTransformer().ComputeAffineTransformation(stars, tab.ReferenceStars);
                var flipped = LivestackMediator.GetImageTransformer().IsFlippedImage(affineTransformationMatrix);
                if (flipped) {
                    // The reference is flipped - most likely a meridian flip happend. Rotate starlist by 180° and recompute the affine transform for a tighter fit. The apply method will then account for the indexing switch
                    stars = LivestackMediator.GetImageMath().Flip(stars, item.Width, item.Height);
                    affineTransformationMatrix = LivestackMediator.GetImageTransformer().ComputeAffineTransformation(stars, tab.ReferenceStars);
                }
                tab.AddTransformedImage(theImageArray, affineTransformationMatrix, flipped);

                StatusUpdate("Updating stack", item);
            }

            StatusUpdate("Rendering stack", item);
            await tab.Refresh(token);
            if (LivestackMediator.Plugin.SaveStackedLights) {
                StatusUpdate("Saving stack", item);
                tab.SaveToDisk();
            }

            _ = messageBroker.Publish(new LivestackBroadcast(LiveStackBroadcastContent.Monochrome(tab.StackCount, tab.Filter, tab.Target, tab.StackImage), correlation));
        }

        private async Task StackOSC(float[] theImageArray, LiveStackItem item, LiveStackTab redTab, Guid correlation, CancellationToken token) {
            var meta = new ImageMetaData(); // Set bare minimum for star detection resize factor
            meta.Camera.PixelSize = profileService.ActiveProfile.CameraSettings.PixelSize;
            meta.Telescope.FocalLength = profileService.ActiveProfile.TelescopeSettings.FocalLength;
            var theImageArrayData = imageDataFactory.CreateBaseImageData(theImageArray.ToUShortArray(), item.Width, item.Height, 16, false, meta);
            var image = theImageArrayData.RenderBitmapSource();
            StatusUpdate("Debayering", item);

            var bayerPattern = SensorType.RGGB;
            if (profileService.ActiveProfile.CameraSettings.BayerPattern != BayerPatternEnum.Auto) {
                bayerPattern = (SensorType)profileService.ActiveProfile.CameraSettings.BayerPattern;
            } else if (!cameraMediator.GetInfo().Connected) {
                bayerPattern = cameraMediator.GetInfo().SensorType;
            }
            var debayeredImage = ImageUtility.Debayer(image, System.Drawing.Imaging.PixelFormat.Format16bppGrayScale, true, false, bayerPattern);

            StatusUpdate("Aligning frame - red channel", item);
            var redChannelData = imageDataFactory.CreateBaseImageData(debayeredImage.Data.Red, item.Width, item.Height, redTab.Properties.BitDepth, false, meta);
            // We only need to detect the stars in one channel for OSC. The others should match.
            var channelStatistics = await redChannelData.Statistics;
            var channelRender = redChannelData.RenderImage();
            if (NeedsStarDetection(redChannelData.StarDetectionAnalysis)) {
                var render = channelRender.RawImageData.RenderImage();
                render = await render.Stretch(profileService.ActiveProfile.ImageSettings.AutoStretchFactor, profileService.ActiveProfile.ImageSettings.BlackClipping, profileService.ActiveProfile.ImageSettings.UnlinkedStretch);
                render = await render.DetectStars(false, profileService.ActiveProfile.ImageSettings.StarSensitivity, profileService.ActiveProfile.ImageSettings.NoiseReduction, token, default);
                redChannelData.StarDetectionAnalysis = render.RawImageData.StarDetectionAnalysis;
            }

            var redChannelStarList = redChannelData.StarDetectionAnalysis?.StarList;
            int rawRedChannelStarCount = redChannelStarList?.Count ?? 0;
            var stars = LivestackMediator.GetImageTransformer().GetStars(redChannelStarList, item.Width, item.Height);

            double[,] affineTransformationMatrix = null;
            bool flipped = false;
            bool pushedReference = false;
            var imageProperties = new ImageProperties(item.Width, item.Height, (int)profileService.ActiveProfile.CameraSettings.BitDepth, item.IsBayered, item.Gain, item.Offset);

            if (!HasEnoughAlignmentStars(redTab.ReferenceStars)) {
                if (!HasEnoughAlignmentStars(stars)) {
                    LogSkippedForInsufficientAlignmentStars("OSC red-channel reference", item, rawRedChannelStarCount, stars.Count, redTab.ReferenceStars?.Count);
                    return;
                }

                if (redTab.ReferenceStars != null) {
                    Logger.Warning($"Live Stack replacing invalid OSC red-channel reference. Old reference stars={redTab.ReferenceStars.Count}; New reference stars={stars.Count}; Required={MinimumAffineStarCount}; Target=\"{item.Target}\"; Frame=\"{item.Path}\"");
                }

                redTab.ForcePushReference(imageProperties, stars, redChannelData.Data.FlatArray.ToFloatArray());
                LogReferenceAccepted("OSC red channel", item, rawRedChannelStarCount, stars.Count);
                pushedReference = true;
            } else {
                if (!HasEnoughAlignmentStars(stars)) {
                    LogSkippedForInsufficientAlignmentStars("OSC red channel", item, rawRedChannelStarCount, stars.Count, redTab.ReferenceStars?.Count);
                    return;
                }

                // We only need to compute the transformation in one channel. The others should match.
                affineTransformationMatrix = LivestackMediator.GetImageTransformer().ComputeAffineTransformation(stars, redTab.ReferenceStars);
                flipped = LivestackMediator.GetImageTransformer().IsFlippedImage(affineTransformationMatrix);
                if (flipped) {
                    // The reference is flipped - most likely a meridian flip happend. Rotate starlist by 180° and recompute the affine transform for a tighter fit. The apply method will then account for the indexing switch
                    stars = LivestackMediator.GetImageMath().Flip(stars, item.Width, item.Height);
                    affineTransformationMatrix = LivestackMediator.GetImageTransformer().ComputeAffineTransformation(stars, redTab.ReferenceStars);
                }
                redTab.AddTransformedImage(debayeredImage.Data.Red, affineTransformationMatrix, flipped);
            }

            StatusUpdate("Aligning frame - green channel", item);
            var greenTab = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == LiveStackBag.GREEN_OSC && x.Target == item.Target) as LiveStackTab;
            if (greenTab == null) {
                var bag = new LiveStackBag(item.Target, LiveStackBag.GREEN_OSC, imageProperties, item.MetaData, stars);
                bag.Add(debayeredImage.Data.Green.ToFloatArray());
                greenTab = new LiveStackTab(profileService, bag);
                Tabs.Add(greenTab);
            } else if (pushedReference) {
                greenTab.ForcePushReference(imageProperties, stars, debayeredImage.Data.Green.ToFloatArray());
            } else {
                greenTab.AddTransformedImage(debayeredImage.Data.Green, affineTransformationMatrix, flipped);
            }

            StatusUpdate("Aligning frame - blue channel", item);
            var blueTab = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == LiveStackBag.BLUE_OSC && x.Target == item.Target) as LiveStackTab;
            if (blueTab == null) {
                var bag = new LiveStackBag(item.Target, LiveStackBag.BLUE_OSC, imageProperties, item.MetaData, stars);
                bag.Add(debayeredImage.Data.Blue.ToFloatArray());
                blueTab = new LiveStackTab(profileService, bag);
                Tabs.Add(blueTab);
            } else if (pushedReference) {
                blueTab.ForcePushReference(imageProperties, stars, debayeredImage.Data.Blue.ToFloatArray());
            } else {
                blueTab.AddTransformedImage(debayeredImage.Data.Blue, affineTransformationMatrix, flipped);
            }

            await redTab.Refresh(token);
            await greenTab.Refresh(token);
            await blueTab.Refresh(token);

            var colorTab = Tabs.Where(x => x is ColorCombinationTab && x.Target == item.Target).FirstOrDefault() as ColorCombinationTab;
            if (colorTab == null) {
                colorTab = new ColorCombinationTab(profileService, redTab, greenTab, blueTab, channelsAlreadyAligned: true);
                Tabs.Add(colorTab);
            }
            if (LivestackMediator.Plugin.SaveStackedLights) {
                StatusUpdate("Saving stacks", item);
                redTab.SaveToDisk();
                greenTab.SaveToDisk();
                blueTab.SaveToDisk();
            }

            _ = messageBroker.Publish(new LivestackBroadcast(LiveStackBroadcastContent.Monochrome(redTab.StackCount, redTab.Filter, redTab.Target, redTab.StackImage), correlation));
            _ = messageBroker.Publish(new LivestackBroadcast(LiveStackBroadcastContent.Monochrome(greenTab.StackCount, greenTab.Filter, greenTab.Target, greenTab.StackImage), correlation));
            _ = messageBroker.Publish(new LivestackBroadcast(LiveStackBroadcastContent.Monochrome(blueTab.StackCount, blueTab.Filter, blueTab.Target, blueTab.StackImage), correlation));
        }

        private async Task StackItem(LiveStackItem item, CancellationToken token) {
            await stackMutationLock.WaitAsync(token);
            LiveStackTab tab = null;
            try {
                tab = GetOrCreateStackBag(item);
                tab.Locked = true;
                if (SelectedTab == null) {
                    SelectedTab = tab;
                }

                var calibratedFrame = CalibrateFrame(item);

                SaveCalibratedFrameIfNeeded(calibratedFrame, item);

                RemoveHotpixelsIfNeeded(calibratedFrame, item);

                Guid correlation = this.stackSessionId.Value;
                if (item.IsBayered) {
                    await StackOSC(calibratedFrame, item, tab, correlation, token);
                } else {
                    await StackMono(calibratedFrame, item, tab, correlation, token);
                }

                var colorTab = Tabs.Where(x => x is ColorCombinationTab && x.Target == tab.Target).FirstOrDefault() as ColorCombinationTab;
                if (colorTab != null) {
                    colorTab.MarkDirty();
                    if (ShouldRefreshColorTab(colorTab)) {
                        StatusUpdate("Refreshing color combined stack", item);
                        await colorTab.Refresh(token);

                        if (LivestackMediator.Plugin.SaveStackedLights) {
                            StatusUpdate("Saving color combined stack", item);
                            colorTab.AutoSaveToDisk();
                        }

                        _ = messageBroker.Publish(new LivestackBroadcast(LiveStackBroadcastContent.Color(colorTab.StackCountRed, colorTab.StackCountGreen, colorTab.StackCountBlue, colorTab.Filter, colorTab.Target, colorTab.StackImage), correlation));
                    }
                }
            } finally {
                if (tab != null) {
                    tab.Locked = false;
                }
                stackMutationLock.Release();
            }
        }

        private bool ShouldRefreshColorTab(ColorCombinationTab colorTab) {
            return ReferenceEquals(SelectedTab, colorTab)
                || colorTab.StackImage == null
                || LivestackMediator.Plugin.SaveStackedLights;
        }

        private float[] CalibrateFrame(LiveStackItem item) {
            StatusUpdate("Calibrating frame", item);
            using var calibrationManager = LivestackMediator.CreateCalibrationManager();
            RegisterCalibrationMasters(calibrationManager);
            float[] theImageArray;
            using (CFitsioFITSReader reader = new CFitsioFITSReader(item.Path)) {
                theImageArray = calibrationManager.ApplyLightFrameCalibrationInPlace(reader, item.Width, item.Height, item.ExposureTime, item.Gain, item.Offset, item.Filter, item.IsBayered);
            }
            return theImageArray;
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

        private static int GetRawStarCount(LiveStackItem item) {
            return item.StarList?.Count ?? 0;
        }

        private static bool HasEnoughAlignmentStars(List<Accord.Point> stars) {
            return stars?.Count >= MinimumAffineStarCount;
        }

        private static void LogReferenceAccepted(string context, LiveStackItem item, int rawDetectedStars, int filteredReferenceStars) {
            Logger.Info($"Live Stack reference accepted ({context}). Raw detector stars={rawDetectedStars}; Filtered reference stars={filteredReferenceStars}; Required={MinimumAffineStarCount}; Target=\"{item.Target}\"; Filter=\"{item.Filter}\"; Frame=\"{item.Path}\"");
        }

        private static void LogSkippedForInsufficientAlignmentStars(string context, LiveStackItem item, int rawDetectedStars, int filteredAlignmentStars, int? referenceAlignmentStars) {
            string referenceStarMessage = referenceAlignmentStars.HasValue
                ? $"; Filtered reference stars={referenceAlignmentStars.Value}"
                : "; Reference stars=not set";
            Logger.Warning($"Live Stack skipping frame ({context}) because affine alignment needs at least {MinimumAffineStarCount} filtered stars on both sides. Raw detector stars={rawDetectedStars}; Filtered current-frame stars={filteredAlignmentStars}{referenceStarMessage}; Target=\"{item.Target}\"; Filter=\"{item.Filter}\"; Frame=\"{item.Path}\"");
        }

        private void RegisterCalibrationMasters(ICalibrationManager calibrationManager) {
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
