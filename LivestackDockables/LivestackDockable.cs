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

        private LiveStackTab GetOrCreateStackBag(LiveStackItem item) {
            var target = string.IsNullOrWhiteSpace(item.Target) ? LiveStackBag.NOTARGET : item.Target;
            var filter = string.IsNullOrWhiteSpace(item.Filter) ? LiveStackBag.NOFILTER : item.Filter;
            if (item.IsBayered) { filter = LiveStackBag.RED_OSC; }

            var tab = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == filter && x.Target == target);
            if (tab == null) {
                var stars = ImageTransformer.GetStars(item.StarList, item.Width, item.Height);
                if (item.IsBayered) { stars = null; }
                var bag = new LiveStackBag(target, filter, new ImageProperties(item.Width, item.Height, (int)profileService.ActiveProfile.CameraSettings.BitDepth, item.IsBayered, item.Gain, item.Offset), stars);
                tab = new LiveStackTab(profileService, bag);
                Tabs.Add(tab);
                return tab as LiveStackTab;
            }
            return tab as LiveStackTab;
        }

        private async Task StackMono(float[] theImageArray, LiveStackItem item, LiveStackTab tab, CancellationToken token) {
            if (tab.Stack == null) {
                tab.AddImage(theImageArray);
            } else {
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
            }

            StatusUpdate("Rendering stack", item);
            await tab.Refresh(token);

            tab.SaveToDisk();
        }

        private async Task StackOSC(float[] theImageArray, LiveStackItem item, LiveStackTab redTab, CancellationToken token) {
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
            if (redChannelData.StarDetectionAnalysis is null || redChannelData.StarDetectionAnalysis.DetectedStars < 0) {
                var render = channelRender.RawImageData.RenderImage();
                render = await render.Stretch(profileService.ActiveProfile.ImageSettings.AutoStretchFactor, profileService.ActiveProfile.ImageSettings.BlackClipping, profileService.ActiveProfile.ImageSettings.UnlinkedStretch);
                render = await render.DetectStars(false, profileService.ActiveProfile.ImageSettings.StarSensitivity, profileService.ActiveProfile.ImageSettings.NoiseReduction, token, default);
                redChannelData.StarDetectionAnalysis = render.RawImageData.StarDetectionAnalysis;
            }

            var stars = ImageTransformer.GetStars(redChannelData.StarDetectionAnalysis.StarList, item.Width, item.Height);

            double[,] affineTransformationMatrix = null;
            bool flipped = false;
            // Reference Stars are null when no image is registered so far
            if (redTab.ReferenceStars == null) {
                redTab.ForcePushReference(new ImageProperties(item.Width, item.Height, (int)profileService.ActiveProfile.CameraSettings.BitDepth, item.IsBayered, item.Gain, item.Offset), stars, redChannelData.Data.FlatArray.ToFloatArray());
            } else {
                // We only need to compute the transformation in one channel. The others should match.
                affineTransformationMatrix = ImageTransformer.ComputeAffineTransformation(stars, redTab.ReferenceStars);
                flipped = ImageTransformer.IsFlippedImage(affineTransformationMatrix);
                if (flipped) {
                    // The reference is flipped - most likely a meridian flip happend. Rotate starlist by 180° and recompute the affine transform for a tighter fit. The apply method will then account for the indexing switch
                    stars = ImageMath.Flip(stars, item.Width, item.Height);
                    affineTransformationMatrix = ImageTransformer.ComputeAffineTransformation(stars, redTab.ReferenceStars);
                }
                var redAligned = ImageTransformer.ApplyAffineTransformation(debayeredImage.Data.Red.ToFloatArray(), item.Width, item.Height, affineTransformationMatrix, flipped);
                redTab.AddImage(redAligned);
            }

            StatusUpdate("Aligning frame - green channel", item);
            var greenTab = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == LiveStackBag.GREEN_OSC && x.Target == item.Target) as LiveStackTab;
            if (greenTab == null) {
                var bag = new LiveStackBag(item.Target, LiveStackBag.GREEN_OSC, new ImageProperties(item.Width, item.Height, (int)profileService.ActiveProfile.CameraSettings.BitDepth, item.IsBayered, item.Gain, item.Offset), stars);
                bag.Add(debayeredImage.Data.Green.ToFloatArray());
                greenTab = new LiveStackTab(profileService, bag);
                Tabs.Add(greenTab);
            } else {
                var greenAligned = ImageTransformer.ApplyAffineTransformation(debayeredImage.Data.Green.ToFloatArray(), item.Width, item.Height, affineTransformationMatrix, flipped);
                greenTab.AddImage(greenAligned);
            }

            StatusUpdate("Aligning frame - blue channel", item);
            var blueTab = Tabs.FirstOrDefault(x => x is LiveStackTab && x.Filter == LiveStackBag.BLUE_OSC && x.Target == item.Target) as LiveStackTab;
            if (blueTab == null) {
                var bag = new LiveStackBag(item.Target, LiveStackBag.BLUE_OSC, new ImageProperties(item.Width, item.Height, (int)profileService.ActiveProfile.CameraSettings.BitDepth, item.IsBayered, item.Gain, item.Offset), stars);
                bag.Add(debayeredImage.Data.Blue.ToFloatArray());
                blueTab = new LiveStackTab(profileService, bag);
                Tabs.Add(blueTab);
            } else {
                var blueAligned = ImageTransformer.ApplyAffineTransformation(debayeredImage.Data.Blue.ToFloatArray(), item.Width, item.Height, affineTransformationMatrix, flipped);
                blueTab.AddImage(blueAligned);
            }

            await redTab.Refresh(token);
            await greenTab.Refresh(token);
            await blueTab.Refresh(token);

            var colorTab = Tabs.Where(x => x is ColorCombinationTab && x.Target == item.Target).FirstOrDefault() as ColorCombinationTab;
            if (colorTab == null) {
                colorTab = new ColorCombinationTab(profileService, redTab, greenTab, blueTab);
                Tabs.Add(colorTab);
            }

            redTab.SaveToDisk();
            greenTab.SaveToDisk();
            blueTab.SaveToDisk();
        }

        private async Task StackItem(LiveStackItem item, CancellationToken token) {
            var tab = GetOrCreateStackBag(item);
            tab.Locked = true;
            try {
                using var calibrationManager = new CalibrationManager();
                RegisterCalibrationMasters(calibrationManager);
                if (SelectedTab == null) {
                    SelectedTab = tab;
                }

                var calibratedFrame = CalibrateFrame(item, calibrationManager);

                RemoveHotpixelsIfNeeded(calibratedFrame, item);

                if (item.IsBayered) {
                    await StackOSC(calibratedFrame, item, tab, token);
                } else {
                    await StackMono(calibratedFrame, item, tab, token);
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

        private float[] CalibrateFrame(LiveStackItem item, CalibrationManager calibrationManager) {
            float[] theImageArray;
            StatusUpdate("Calibrating frame", item);
            using (CFitsioFITSReader reader = new CFitsioFITSReader(item.Path)) {
                theImageArray = calibrationManager.ApplyLightFrameCalibrationInPlace(reader, item.Width, item.Height, item.ExposureTime, item.Gain, item.Offset, item.Filter, item.IsBayered);
            }
            return theImageArray;
        }

        private void RemoveHotpixelsIfNeeded(float[] theImageArray, LiveStackItem item) {
            if (LivestackMediator.PluginSettings.GetValueBoolean(nameof(Livestack.HotpixelRemoval), true)) {
                StatusUpdate("Removing hot pixels in frame", item);
                ImageMath.RemoveHotPixelOutliers(theImageArray, item.Width, item.Height);
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