using NINA.Core.Utility;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NINA.Core.Utility.Notification;
using NINA.Core.Utility.WindowService;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.LivestackDockables;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using NINA.WPF.Base.ViewModel;
using MathNet.Numerics.Statistics;

namespace NINA.Plugin.Livestack {

    public partial class CalibrationVM : BaseVM {

        public CalibrationVM(IProfileService profileService, IImageDataFactory imageDataFactory, IWindowServiceFactory windowServiceFactory) : base(profileService) {
            this.imageDataFactory = imageDataFactory;
            this.windowServiceFactory = windowServiceFactory;

            InitializeLibraries();

            profileService.ProfileChanged += ProfileService_ProfileChanged;
        }

        private void ProfileService_ProfileChanged(object sender, EventArgs e) {
            InitializeLibraries();
        }

        private void InitializeLibraries() {
            var darkLibrary = new AsyncObservableCollection<CalibrationFrameMeta>(LivestackMediator.PluginSettings.GetValueString(nameof(DarkLibrary), "").FromStringToList<CalibrationFrameMeta>());
            foreach (var item in darkLibrary) {
                if (!File.Exists(item.Path)) {
                    Logger.Warning($"DARK master not found: {item.Path}");
                    darkLibrary.Remove(item);
                }
            }
            DarkLibrary = darkLibrary;
            var biasLibrary = new AsyncObservableCollection<CalibrationFrameMeta>(LivestackMediator.PluginSettings.GetValueString(nameof(BiasLibrary), "").FromStringToList<CalibrationFrameMeta>());
            foreach (var item in biasLibrary) {
                if (!File.Exists(item.Path)) {
                    Logger.Warning($"BIAS master not found: {item.Path}");
                    biasLibrary.Remove(item);
                }
            }
            BiasLibrary = biasLibrary;
            var flatLibrary = new AsyncObservableCollection<CalibrationFrameMeta>(LivestackMediator.PluginSettings.GetValueString(nameof(FlatLibrary), "").FromStringToList<CalibrationFrameMeta>());
            foreach (var item in flatLibrary) {
                if (!File.Exists(item.Path)) {
                    Logger.Warning($"Flat master not found: {item.Path}");
                    flatLibrary.Remove(item);
                } else if (double.IsNaN(item.Mean)) {
                    Logger.Warning($"Flat master meta info does not contain calculated mean value: {item.Path}");
                    flatLibrary.Remove(item);
                }
            }
            FlatLibrary = flatLibrary;
            SessionFlatLibrary = new AsyncObservableCollection<CalibrationFrameMeta>();
        }

        [ObservableProperty]
        private AsyncObservableCollection<CalibrationFrameMeta> biasLibrary;

        [ObservableProperty]
        private AsyncObservableCollection<CalibrationFrameMeta> darkLibrary;

        [ObservableProperty]
        private AsyncObservableCollection<CalibrationFrameMeta> flatLibrary;

        [ObservableProperty]
        private AsyncObservableCollection<CalibrationFrameMeta> sessionFlatLibrary;

        private readonly IImageDataFactory imageDataFactory;
        private readonly IWindowServiceFactory windowServiceFactory;

        [RelayCommand]
        private void DeleteBiasMaster(CalibrationFrameMeta c) {
            BiasLibrary.Remove(c);
            LivestackMediator.PluginSettings.SetValueString(nameof(BiasLibrary), BiasLibrary.FromListToString());
        }

        [RelayCommand]
        private void DeleteDarkMaster(CalibrationFrameMeta c) {
            DarkLibrary.Remove(c);
            LivestackMediator.PluginSettings.SetValueString(nameof(DarkLibrary), DarkLibrary.FromListToString());
        }

        [RelayCommand]
        private void DeleteFlatMaster(CalibrationFrameMeta c) {
            FlatLibrary.Remove(c);
            LivestackMediator.PluginSettings.SetValueString(nameof(FlatLibrary), FlatLibrary.FromListToString());
        }

        [RelayCommand]
        private void DeleteSessionFlatMaster(CalibrationFrameMeta c) {
            SessionFlatLibrary.Remove(c);
        }

        public void AddCalibrationFrame(CalibrationFrameMeta frame) {
            if (frame.Type == CalibrationFrameType.BIAS) {
                BiasLibrary.Add(frame);
                LivestackMediator.PluginSettings.SetValueString(nameof(BiasLibrary), BiasLibrary.FromListToString());
            } else if (frame.Type == CalibrationFrameType.DARK) {
                DarkLibrary.Add(frame);
                LivestackMediator.PluginSettings.SetValueString(nameof(DarkLibrary), DarkLibrary.FromListToString());
            } else if (frame.Type == CalibrationFrameType.FLAT) {
                FlatLibrary.Add(frame);
                LivestackMediator.PluginSettings.SetValueString(nameof(FlatLibrary), FlatLibrary.FromListToString());
            }
        }

        public void AddSessionFlatMaster(CalibrationFrameMeta frame) {
            SessionFlatLibrary.Add(frame);
        }

        [RelayCommand]
        private async Task AddSessionFlatMaster() {
            var dialog = new OpenFileDialog();
            dialog.Title = "Add Calibration Frame";
            dialog.FileName = "";
            dialog.DefaultExt = ".fits";
            dialog.Filter = "Flexible Image Transport System|*.fits;*.fit";

            if (dialog.ShowDialog() == true) {
                var width = 0;
                var height = 0;
                int gain = -1;
                int offset = -1;
                string filter = "";
                double exposureTime = 0;
                float mean = float.NaN;
                string imageType = "";

                var extension = Path.GetExtension(dialog.FileName).ToLower();
                if (extension == ".fits" || extension == ".fit") {
                    using (var fits = new CFitsioFITSReader(dialog.FileName)) {
                        var metaData = fits.ReadHeader().ExtractMetaData();

                        imageType = metaData.Image.ImageType;
                        gain = metaData.Camera.Gain;
                        offset = metaData.Camera.Offset;
                        filter = metaData.FilterWheel.Filter;
                        exposureTime = double.IsNaN(metaData.Image.ExposureTime) ? 0 : metaData.Image.ExposureTime;
                        width = fits.Width;
                        height = fits.Height;

                        mean = (float)fits.ReadAllPixelsAsFloat().Mean();
                    }
                } else {
                    Notification.ShowError("Unsupported file format");
                    return;
                }

                CalibrationFrameMeta frame = new CalibrationFrameMeta();
                frame.Path = dialog.FileName;
                if (imageType.ToLower() == "flat" || imageType.ToLower() == "'flat'") {
                    frame.Type = CalibrationFrameType.FLAT;
                }

                frame.Gain = gain;
                frame.Offset = offset;
                frame.Filter = filter;
                frame.ExposureTime = exposureTime;
                frame.Width = width;
                frame.Height = height;
                frame.Mean = mean;

                var service = windowServiceFactory.Create();
                var prompt = new CalibrationFramePrompt(frame);
                await service.ShowDialog(prompt, "Calibration Frame Wizard", System.Windows.ResizeMode.NoResize, System.Windows.WindowStyle.ToolWindow);

                if (prompt.Continue) {
                    if (string.IsNullOrEmpty(frame.Filter)) { frame.Filter = LiveStackBag.NOFILTER; }
                    if (frame.Type == CalibrationFrameType.FLAT) {
                        SessionFlatLibrary.Add(frame);
                    }
                }
            }
        }

        [RelayCommand]
        private async Task AddCalibrationFrame() {
            var dialog = new OpenFileDialog();
            dialog.Title = "Add Calibration Frame";
            dialog.FileName = "";
            dialog.DefaultExt = ".fits";
            dialog.Filter = "Flexible Image Transport System|*.fits;*.fit;*.fits.fz";

            if (dialog.ShowDialog() == true) {
                var width = 0;
                var height = 0;
                int gain = -1;
                int offset = -1;
                string filter = "";
                double exposureTime = 0;
                float mean = float.NaN;
                string imageType = "";

                var extension = Path.GetExtension(dialog.FileName).ToLower();
                if (extension == ".fits" || extension == ".fit") {
                    using (var fits = new CFitsioFITSReader(dialog.FileName)) {
                        var metaData = fits.ReadHeader().ExtractMetaData();

                        imageType = metaData.Image.ImageType;
                        gain = metaData.Camera.Gain;
                        offset = metaData.Camera.Offset;
                        filter = metaData.FilterWheel.Filter;
                        exposureTime = double.IsNaN(metaData.Image.ExposureTime) ? 0 : metaData.Image.ExposureTime;
                        width = fits.Width;
                        height = fits.Height;

                        mean = (float)fits.ReadAllPixelsAsFloat().Mean();
                    }
                } else {
                    Notification.ShowError("Unsupported file format");
                    return;
                }

                CalibrationFrameMeta frame = new CalibrationFrameMeta();
                frame.Path = dialog.FileName;
                if (imageType.ToLower() == "dark" || imageType.ToLower() == "'dark'") {
                    frame.Type = CalibrationFrameType.DARK;
                } else if (imageType.ToLower() == "bias" || imageType.ToLower() == "'bias'") {
                    frame.Type = CalibrationFrameType.BIAS;
                } else if (imageType.ToLower() == "flat" || imageType.ToLower() == "'flat'") {
                    frame.Type = CalibrationFrameType.FLAT;
                }

                frame.Gain = gain;
                frame.Offset = offset;
                frame.Filter = filter;
                frame.ExposureTime = exposureTime;
                frame.Width = width;
                frame.Height = height;
                frame.Mean = mean;

                var service = windowServiceFactory.Create();
                var prompt = new CalibrationFramePrompt(frame);
                await service.ShowDialog(prompt, "Calibration Frame Wizard", System.Windows.ResizeMode.NoResize, System.Windows.WindowStyle.ToolWindow);

                if (prompt.Continue) {
                    if (string.IsNullOrEmpty(frame.Filter)) { frame.Filter = LiveStackBag.NOFILTER; }
                    if (frame.Type == CalibrationFrameType.BIAS) {
                        BiasLibrary.Add(frame);
                        LivestackMediator.PluginSettings.SetValueString(nameof(BiasLibrary), BiasLibrary.FromListToString());
                    } else if (frame.Type == CalibrationFrameType.DARK) {
                        DarkLibrary.Add(frame);
                        LivestackMediator.PluginSettings.SetValueString(nameof(DarkLibrary), DarkLibrary.FromListToString());
                    } else if (frame.Type == CalibrationFrameType.FLAT) {
                        FlatLibrary.Add(frame);
                        LivestackMediator.PluginSettings.SetValueString(nameof(FlatLibrary), FlatLibrary.FromListToString());
                    }
                }
            }
        }
    }
}