using NINA.Core.Utility;
using NINA.Image.Interfaces;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using Settings = NINA.Plugin.Livestack.Properties.Settings;
using NINA.Core.Utility.WindowService;
using System.IO;
using Microsoft.Win32;
using System.Windows.Input;

namespace NINA.Plugin.Livestack {

    /// <summary>
    /// This class exports the IPluginManifest interface and will be used for the general plugin information and options
    /// The base class "PluginBase" will populate all the necessary Manifest Meta Data out of the AssemblyInfo attributes. Please fill these accoringly
    ///
    /// An instance of this class will be created and set as datacontext on the plugin options tab in N.I.N.A. to be able to configure global plugin settings
    /// The user interface for the settings will be defined by a DataTemplate with the key having the naming convention "Livestack_Options" where Livestack corresponds to the AssemblyTitle - In this template example it is found in the Options.xaml
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public partial class Livestack : PluginBase, INotifyPropertyChanged {
        public IPluginOptionsAccessor PluginSettings { get; }

        private readonly IProfileService profileService;
        private readonly IImageDataFactory imageDataFactory;
        private readonly IWindowServiceFactory windowServiceFactory;

        [ImportingConstructor]
        public Livestack(IProfileService profileService, IImageDataFactory imageDataFactory, IWindowServiceFactory windowServiceFactory) {
            if (Settings.Default.UpdateSettings) {
                Settings.Default.Upgrade();
                Settings.Default.UpdateSettings = false;
                CoreUtil.SaveSettings(Settings.Default);
            }

            this.PluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(this.Identifier));

            // This helper class can be used to store plugin settings that are dependent on the current profile
            this.profileService = profileService;
            this.imageDataFactory = imageDataFactory;
            this.windowServiceFactory = windowServiceFactory;
            // React on a changed profile
            profileService.ProfileChanged += ProfileService_ProfileChanged;

            LivestackMediator.RegisterPlugin(this);
            LivestackMediator.RegisterSettings(PluginSettings);
            LivestackMediator.RegisterCalibrationVM(new CalibrationVM(profileService, imageDataFactory, windowServiceFactory));
            OpenWorkingFolderDiagCommand = new GalaSoft.MvvmLight.Command.RelayCommand(OpenWorkingFolderDiag);
        }

        public override Task Teardown() {
            // Make sure to unregister an event when the object is no longer in use. Otherwise garbage collection will be prevented.
            profileService.ProfileChanged -= ProfileService_ProfileChanged;

            return base.Teardown();
        }

        private void ProfileService_ProfileChanged(object sender, EventArgs e) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        public bool HotpixelRemoval {
            get {
                return PluginSettings.GetValueBoolean(nameof(HotpixelRemoval), true);
            }
            set {
                PluginSettings.SetValueBoolean(nameof(HotpixelRemoval), value);
                RaisePropertyChanged();
            }
        }

        public bool UseBiasForLights {
            get {
                return PluginSettings.GetValueBoolean(nameof(UseBiasForLights), true);
            }
            set {
                PluginSettings.SetValueBoolean(nameof(UseBiasForLights), value);
                RaisePropertyChanged();
            }
        }

        public string WorkingDirectory {
            get {
                return PluginSettings.GetValueString(nameof(WorkingDirectory), Path.GetTempPath());
            }
            set {
                PluginSettings.SetValueString(nameof(WorkingDirectory), value);
                RaisePropertyChanged();
            }
        }

        public double DefaultStretchAmount {
            get {
                return PluginSettings.GetValueDouble(nameof(DefaultStretchAmount), profileService.ActiveProfile.ImageSettings.AutoStretchFactor);
            }
            set {
                PluginSettings.SetValueDouble(nameof(DefaultStretchAmount), value);
                RaisePropertyChanged();
            }
        }

        public double DefaultBlackClipping {
            get {
                return PluginSettings.GetValueDouble(nameof(DefaultBlackClipping), profileService.ActiveProfile.ImageSettings.BlackClipping);
            }
            set {
                PluginSettings.SetValueDouble(nameof(DefaultBlackClipping), value);
                RaisePropertyChanged();
            }
        }

        public bool DefaultEnableGreenDeNoise {
            get {
                return PluginSettings.GetValueBoolean(nameof(DefaultEnableGreenDeNoise), false);
            }
            set {
                PluginSettings.SetValueBoolean(nameof(DefaultEnableGreenDeNoise), value);
                RaisePropertyChanged();
            }
        }

        public double DefaultGreenDeNoiseAmount {
            get {
                return PluginSettings.GetValueDouble(nameof(DefaultGreenDeNoiseAmount), 0.7);
            }
            set {
                PluginSettings.SetValueDouble(nameof(DefaultGreenDeNoiseAmount), value);
                RaisePropertyChanged();
            }
        }

        public int DefaultDownsample {
            get {
                return PluginSettings.GetValueInt32(nameof(DefaultDownsample), 1);
            }
            set {
                if (value < 1) { value = 1; }
                if (value > 4) { value = 4; }
                PluginSettings.SetValueInt32(nameof(DefaultDownsample), value);
                RaisePropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void OpenWorkingFolderDiag() {
            var diag = new OpenFolderDialog();

            if (Directory.Exists(WorkingDirectory)) {
                diag.InitialDirectory = WorkingDirectory;
            }

            var result = diag.ShowDialog();
            if (result.HasValue && result.Value) {
                if (Directory.Exists(diag.FolderName)) {
                    WorkingDirectory = diag.FolderName;
                }
            }
        }

        public ICommand OpenWorkingFolderDiagCommand { get; }
    }
}