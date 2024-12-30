using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack.QualityGate;
using NINA.Plugin.Livestack.Image;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.ViewModel;
using Nito.AsyncEx;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NINA.Core.Utility.WindowService;
using NINA.Image.ImageAnalysis;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.IO;
using System.Drawing.Imaging;
using NINA.Image.ImageData;
using NINA.Core.Enum;
using NINA.Equipment.Interfaces.Mediator;

namespace NINA.Plugin.Livestack.LivestackDockables {

    [Export(typeof(IDockableVM))]
    public partial class LivestackDockable : DockableVM {
        public override bool IsTool { get; } = true;

        [ImportingConstructor]
        public LivestackDockable(IProfileService profileService, IApplicationStatusMediator applicationStatusMediator, IImageSaveMediator imageSaveMediator, IImageDataFactory imageDataFactory, IWindowServiceFactory windowServiceFactory, ICameraMediator cameraMediator) : base(profileService) {
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

            profileService.ActiveProfile.PropertyChanged += ActiveProfile_PropertyChanged;
            InitializeQualityGates();
            tabs = new AsyncObservableCollection<IStackTab>();
            IsExpanded = true;
            LivestackMediator.RegisterLivestackDockable(this);
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

        [ObservableProperty]
        private int queueEntries;

        private AsyncProducerConsumerQueue<LiveStackItem> queue;
        private readonly IApplicationStatusMediator applicationStatusMediator;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly IImageDataFactory imageDataFactory;
        private readonly IWindowServiceFactory windowServiceFactory;
        private readonly ICameraMediator cameraMediator;

        [RelayCommand(IncludeCancelCommand = true)]
        private Task StartLiveStack(CancellationToken token) {
            return Task.Run(async () => {
                try {
                    IsExpanded = false;
                    QueueEntries = 0;
                    queue = new AsyncProducerConsumerQueue<LiveStackItem>(1000);
                    var localQueue = queue;
                    this.imageSaveMediator.BeforeFinalizeImageSaved += ImageSaveMediator_BeforeFinalizeImageSaved;

                    while (!token.IsCancellationRequested) {
                        try {
                            applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = "Waiting for next frame" });

                            var available = await localQueue.OutputAvailableAsync(token);
                            if (!available) { return; }
                            var item = await localQueue.DequeueAsync(token);

                            Interlocked.Decrement(ref queueEntries);
                            RaisePropertyChanged(nameof(QueueEntries));

                            try {
                                if (item.StarList.Count < 8) {
                                    Logger.Info($"Skipping frame as not enough stars have been detected ({item.StarList.Count})");
                                    continue;
                                }

                                StatusUpdate("Received new frame", item);
                                if (!ItemPassesQuality(item)) {
                                    continue;
                                }

                                await StackItem(item, token);
                            } finally {
                                File.Delete(item.Path);
                            }

                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                        } catch (OperationCanceledException) {
                        } catch (Exception ex) {
                            Logger.Error(ex);
                        }
                    }

                    if (localQueue != null) {
                        try {
                            localQueue.CompleteAdding();
                            while (true) {
                                var item = await localQueue.DequeueAsync(token);
                                StatusUpdate("Flushing queue", item);
                                File.Delete(item.Path);
                            }
                        } catch { }
                    }
                } finally {
                    applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = "" });
                    this.imageSaveMediator.BeforeFinalizeImageSaved -= ImageSaveMediator_BeforeFinalizeImageSaved;
                    IsExpanded = true;
                    QueueEntries = 0;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            });
        }

        [RelayCommand]
        private async Task RemoveTab(IStackTab tab) {
            while (tab.Locked) {
                await Task.Delay(100);
            }
            Tabs.Remove(tab);
            GC.Collect();
            GC.WaitForPendingFinalizers();
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

        private async Task ImageSaveMediator_BeforeFinalizeImageSaved(object sender, BeforeFinalizeImageSavedEventArgs e) {
            if (e.Image.RawImageData.MetaData.Image.ImageType == NINA.Equipment.Model.CaptureSequence.ImageTypes.LIGHT || e.Image.RawImageData.MetaData.Image.ImageType == NINA.Equipment.Model.CaptureSequence.ImageTypes.SNAPSHOT) {
                _ = Task.Run(async () => {
                    try {
                        var statistics = await e.Image.RawImageData.Statistics;
                        var starDetectionAnalysis = e.Image.RawImageData.StarDetectionAnalysis;
                        if (starDetectionAnalysis is null || starDetectionAnalysis.DetectedStars <= 0) {
                            var render = e.Image.RawImageData.RenderImage();
                            render = await render.Stretch(profileService.ActiveProfile.ImageSettings.AutoStretchFactor, profileService.ActiveProfile.ImageSettings.BlackClipping, profileService.ActiveProfile.ImageSettings.UnlinkedStretch);
                            render = await render.DetectStars(false, profileService.ActiveProfile.ImageSettings.StarSensitivity, profileService.ActiveProfile.ImageSettings.NoiseReduction, default, default);
                            starDetectionAnalysis = render.RawImageData.StarDetectionAnalysis;
                        }
                        var path = await e.Image.RawImageData.SaveToDisk(
                            new NINA.Image.FileFormat.FileSaveInfo() {
                                FilePath = Path.Combine(LivestackMediator.Plugin.WorkingDirectory, "temp"),
                                FilePattern = Path.GetRandomFileName(),
                                FileType = Core.Enum.FileTypeEnum.FITS
                            },
                            default, true
                        );
                        await queue.EnqueueAsync(new LiveStackItem(path: path,
                                                                   target: e.Image.RawImageData.MetaData.Target.Name,
                                                                   filter: e.Image.RawImageData.MetaData.FilterWheel.Filter,
                                                                   exposureTime: e.Image.RawImageData.MetaData.Image.ExposureTime,
                                                                   gain: e.Image.RawImageData.MetaData.Camera.Gain,
                                                                   offset: e.Image.RawImageData.MetaData.Camera.Offset,
                                                                   width: e.Image.RawImageData.Properties.Width,
                                                                   height: e.Image.RawImageData.Properties.Height,
                                                                   bitDepth: (int)profileService.ActiveProfile.CameraSettings.BitDepth,
                                                                   isBayered: e.Image.RawImageData.Properties.IsBayered,
                                                                   analysis: starDetectionAnalysis));
                        Interlocked.Increment(ref queueEntries);
                        RaisePropertyChanged(nameof(QueueEntries));
                    } catch (Exception ex) {
                        Logger.Error(ex);
                    }
                });
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

        private async Task<(LiveStackTab, bool created)> GetOrCreateStackBag(LiveStackItem item, CancellationToken token) {
            var target = string.IsNullOrWhiteSpace(item.Target) ? LiveStackBag.NOTARGET : item.Target;
            var filter = string.IsNullOrWhiteSpace(item.Filter) ? LiveStackBag.NOFILTER : item.Filter;
            if (item.IsBayered) { filter = "R_OSC"; }

            var tab = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == filter && x.Target == target);
            if (tab == null) {
                var stars = ImageTransformer.GetStars(item.StarList, item.Width, item.Height);
                if (item.IsBayered) { stars = null; }
                var bag = new LiveStackBag(target, filter, new ImageProperties(item.Width, item.Height, (int)profileService.ActiveProfile.CameraSettings.BitDepth, item.IsBayered, item.Gain, item.Offset), stars);
                tab = new LiveStackTab(profileService, bag);
                Tabs.Add(tab);
                return (tab as LiveStackTab, true);
            }
            return (tab as LiveStackTab, false);
        }

        private async Task StackMono(LiveStackItem item, LiveStackTab tab, CalibrationManager calibrationManager, CancellationToken token) {
            ushort[] theImageArray;
            StatusUpdate("Calibrating frame", item);
            using (CFitsioFITSReader reader = new CFitsioFITSReader(item.Path)) {
                theImageArray = calibrationManager.ApplyLightFrameCalibrationInPlace(reader, item.Width, item.Height, item.ExposureTime, item.Gain, item.Offset, item.Filter, item.IsBayered, token);
            }

            if (LivestackMediator.PluginSettings.GetValueBoolean(nameof(Livestack.HotpixelRemoval), true)) {
                StatusUpdate("Removing hot pixels in frame", item);
                ImageMath.RemoveHotPixelOutliers(theImageArray, item.Width, item.Height);
            }

            StatusUpdate("Aligning frame", item);
            var stars = ImageTransformer.GetStars(item.StarList, item.Width, item.Height);
            var affineTransformationMatrix = ImageTransformer.ComputeAffineTransformation(stars, tab.ReferenceStars);
            var flipped = ImageTransformer.IsFlippedImage(affineTransformationMatrix);
            if (flipped) {
                // The reference is flipped - most likely a meridian flip happend. Rotate starlist by 180° and recompute the affine transform for a tighter fit. The apply method will then account for the indexing switch
                stars = ImageMath.Flip(stars, item.Width, item.Height);
                affineTransformationMatrix = ImageTransformer.ComputeAffineTransformation(stars, tab.ReferenceStars);
            }
            var transformedImage = ImageTransformer.ApplyAffineTransformation(theImageArray, item.Width, item.Height, affineTransformationMatrix, flipped);

            StatusUpdate("Updating stack", item);
            tab.AddImage(transformedImage);

            StatusUpdate("Rendering stack", item);
            await tab.Refresh(token);

            tab.SaveToDisk();
        }

        private async Task StackOSC(LiveStackItem item, LiveStackTab tab, CalibrationManager calibrationManager, CancellationToken token) {
            ushort[] theImageArray;
            StatusUpdate("Calibrating frame", item);
            using (CFitsioFITSReader reader = new CFitsioFITSReader(item.Path)) {
                theImageArray = calibrationManager.ApplyLightFrameCalibrationInPlace(reader, item.Width, item.Height, item.ExposureTime, item.Gain, item.Offset, item.Filter, item.IsBayered, token);
                if (LivestackMediator.PluginSettings.GetValueBoolean(nameof(Livestack.HotpixelRemoval), true)) {
                    StatusUpdate("Removing hot pixels in frame", item);
                    ImageMath.RemoveHotPixelOutliers(theImageArray, item.Width, item.Height);
                }
            }

            var meta = new ImageMetaData(); // Set bare minimum for star detection resize factor
            meta.Camera.PixelSize = profileService.ActiveProfile.CameraSettings.PixelSize;
            meta.Telescope.FocalLength = profileService.ActiveProfile.TelescopeSettings.FocalLength;
            var theImageArrayData = imageDataFactory.CreateBaseImageData(theImageArray, tab.Properties.Width, tab.Properties.Height, 16, false, meta);
            var image = theImageArrayData.RenderBitmapSource();
            StatusUpdate("Debayering", item);

            var bayerPattern = SensorType.RGGB;
            if (profileService.ActiveProfile.CameraSettings.BayerPattern != BayerPatternEnum.Auto) {
                bayerPattern = (SensorType)profileService.ActiveProfile.CameraSettings.BayerPattern;
            } else if (!cameraMediator.GetInfo().Connected) {
                bayerPattern = cameraMediator.GetInfo().SensorType;
            }
            var debayeredImage = ImageUtility.Debayer(image, System.Drawing.Imaging.PixelFormat.Format16bppGrayScale, true, false, bayerPattern);

            List<(ushort[], List<Accord.Point>)> transformed = new();
            foreach (var channelArray in new List<ushort[]> { debayeredImage.Data.Red, debayeredImage.Data.Green, debayeredImage.Data.Blue }) {
                StatusUpdate("Aligning frame", item);

                var channelData = imageDataFactory.CreateBaseImageData(channelArray, tab.Properties.Width, tab.Properties.Height, 16, false, meta);
                var channelStatistics = await channelData.Statistics;
                var channelRender = channelData.RenderImage();
                if (channelData.StarDetectionAnalysis is null || channelData.StarDetectionAnalysis.DetectedStars < 0) {
                    var render = channelRender.RawImageData.RenderImage();
                    render = await render.Stretch(profileService.ActiveProfile.ImageSettings.AutoStretchFactor, profileService.ActiveProfile.ImageSettings.BlackClipping, profileService.ActiveProfile.ImageSettings.UnlinkedStretch);
                    render = await render.DetectStars(false, profileService.ActiveProfile.ImageSettings.StarSensitivity, profileService.ActiveProfile.ImageSettings.NoiseReduction, token, default);
                    channelData.StarDetectionAnalysis = render.RawImageData.StarDetectionAnalysis;
                }

                var stars = ImageTransformer.GetStars(channelData.StarDetectionAnalysis.StarList, tab.Properties.Width, tab.Properties.Height);

                if (tab.ReferenceStars != null) {
                    var affineTransformationMatrix = ImageTransformer.ComputeAffineTransformation(stars, tab.ReferenceStars);
                    var flipped = ImageTransformer.IsFlippedImage(affineTransformationMatrix);
                    if (flipped) {
                        // The reference is flipped - most likely a meridian flip happend. Rotate starlist by 180° and recompute the affine transform for a tighter fit. The apply method will then account for the indexing switch
                        stars = ImageMath.Flip(stars, item.Width, item.Height);
                        affineTransformationMatrix = ImageTransformer.ComputeAffineTransformation(stars, tab.ReferenceStars);
                    }
                    transformed.Add((ImageTransformer.ApplyAffineTransformation(channelArray, item.Width, item.Height, affineTransformationMatrix, flipped), stars));
                } else {
                    tab.ForcePushReference(channelData, stars, channelData.Data.FlatArray);
                    transformed.Add((null, null));
                }
            }

            if (transformed[0].Item1 != null) {
                tab.AddImage(transformed[0].Item1);
            }
            await tab.Refresh(token);

            var greenTab = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == "G_OSC" && x.Target == tab.Target) as LiveStackTab;
            if (greenTab == null) {
                var bag = new LiveStackBag(tab.Target, "G_OSC", new ImageProperties(item.Width, item.Height, (int)profileService.ActiveProfile.CameraSettings.BitDepth, item.IsBayered, item.Gain, item.Offset), transformed[1].Item2);
                bag.Add(transformed[1].Item1);
                greenTab = new LiveStackTab(profileService, bag);
                Tabs.Add(greenTab);
            } else {
                greenTab.AddImage(transformed[1].Item1);
            }
            await greenTab.Refresh(token);
            var blueTab = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == "B_OSC" && x.Target == tab.Target) as LiveStackTab;
            if (blueTab == null) {
                var bag = new LiveStackBag(tab.Target, "B_OSC", new ImageProperties(item.Width, item.Height, (int)profileService.ActiveProfile.CameraSettings.BitDepth, item.IsBayered, item.Gain, item.Offset), transformed[2].Item2);
                bag.Add(transformed[2].Item1);
                blueTab = new LiveStackTab(profileService, bag);
                Tabs.Add(blueTab);
            } else {
                blueTab.AddImage(transformed[2].Item1);
            }
            await blueTab.Refresh(token);

            var colorTab = Tabs.Where(x => x is ColorCombinationTab && x.Target == tab.Target).FirstOrDefault() as ColorCombinationTab;
            if (colorTab == null) {
                colorTab = new ColorCombinationTab(profileService, tab, greenTab, blueTab);
                Tabs.Add(colorTab);
            }

            tab.SaveToDisk();
            greenTab.SaveToDisk();
            blueTab.SaveToDisk();
        }

        private async Task StackItem(LiveStackItem item, CancellationToken token) {
            var (tab, created) = await GetOrCreateStackBag(item, token);
            tab.Locked = true;
            try {
                using var calibrationManager = new CalibrationManager();
                RegisterCalibrationMasters(calibrationManager);

                if (created && !tab.Properties.IsBayered) {
                    if (SelectedTab == null) {
                        SelectedTab = tab;
                    }
                    StatusUpdate("Calibrating frame", item);
                    using (var reader = new CFitsioFITSReader(item.Path)) {
                        var theImageArray = calibrationManager.ApplyLightFrameCalibrationInPlace(reader, item.Width, item.Height, item.ExposureTime, item.Gain, item.Offset, item.Filter, item.IsBayered, token);
                        if (!item.IsBayered && LivestackMediator.PluginSettings.GetValueBoolean(nameof(Livestack.HotpixelRemoval), true)) {
                            applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = "Removing hot pixels in frame" });
                            ImageMath.RemoveHotPixelOutliers(theImageArray, item.Width, item.Height);
                        }
                        tab.AddImage(theImageArray);
                    }

                    StatusUpdate("Rendering stack", item);
                    await tab.Refresh(token);

                    return;
                }

                if (item.IsBayered) {
                    await StackOSC(item, tab, calibrationManager, token);
                } else {
                    await StackMono(item, tab, calibrationManager, token);
                }

                var colorTab = Tabs.Where(x => x is ColorCombinationTab && x.Target == tab.Target).FirstOrDefault() as ColorCombinationTab;
                if (colorTab != null) {
                    StatusUpdate("Refreshing color combined stack", item);
                    await colorTab.Refresh(token);
                }
            } finally {
                tab.Locked = false;
            }
        }

        private void StatusUpdate(string status, LiveStackItem item) {
            if (!string.IsNullOrEmpty(status)) {
                Logger.Info($"{status} - {item.Path}");
            }
            applicationStatusMediator.StatusUpdate(new ApplicationStatus() { Source = "Live Stack", Status = status });
        }

        private void RegisterCalibrationMasters(CalibrationManager calibrationManager) {
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
    }
}