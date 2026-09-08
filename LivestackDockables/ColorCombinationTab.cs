using NINA.Image.ImageAnalysis;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack.Image;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using NINA.WPF.Base.ViewModel;
using System.Windows;
using System.Drawing.Imaging;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Collections.Immutable;
using OxyPlot;
using NINA.Core.Utility;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using NINA.Astrometry;
using Microsoft.Win32;

namespace NINA.Plugin.Livestack.LivestackDockables {

    public partial class ColorCombinationTab : BaseVM, IStackTab {

        public ColorCombinationTab(IProfileService profileService, LiveStackTab red, LiveStackTab green, LiveStackTab blue, bool channelsAlreadyAligned = false) : base(profileService) {
            this.Target = red.Target;
            this.profileService = profileService;
            this.red = red;
            this.green = green;
            this.blue = blue;
            this.channelsAlreadyAligned = channelsAlreadyAligned;
            redStretchFactor = LivestackMediator.Plugin.DefaultStretchAmount;
            greenStretchFactor = LivestackMediator.Plugin.DefaultStretchAmount;
            blueStretchFactor = LivestackMediator.Plugin.DefaultStretchAmount;
            redBlackClipping = LivestackMediator.Plugin.DefaultBlackClipping;
            greenBlackClipping = LivestackMediator.Plugin.DefaultBlackClipping;
            blueBlackClipping = LivestackMediator.Plugin.DefaultBlackClipping;
            enableBackgroundExtraction = LivestackMediator.Plugin.DefaultEnableBackgroundExtraction;
            backgroundExtractionAmount = LivestackMediator.Plugin.DefaultBackgroundExtractionAmount;
            enableGreenDeNoise = LivestackMediator.Plugin.DefaultEnableGreenDeNoise;
            greenDeNoiseAmount = LivestackMediator.Plugin.DefaultGreenDeNoiseAmount;
            imageRotation = 0;
            imageFlipValue = 1;
            downsample = LivestackMediator.Plugin.DefaultDownsample;
        }

        [RelayCommand]
        public void ResetSettings() {
            RedStretchFactor = LivestackMediator.Plugin.DefaultStretchAmount;
            GreenStretchFactor = LivestackMediator.Plugin.DefaultStretchAmount;
            BlueStretchFactor = LivestackMediator.Plugin.DefaultStretchAmount;
            RedBlackClipping = LivestackMediator.Plugin.DefaultBlackClipping;
            GreenBlackClipping = LivestackMediator.Plugin.DefaultBlackClipping;
            BlueBlackClipping = LivestackMediator.Plugin.DefaultBlackClipping;
            EnableBackgroundExtraction = LivestackMediator.Plugin.DefaultEnableBackgroundExtraction;
            BackgroundExtractionAmount = LivestackMediator.Plugin.DefaultBackgroundExtractionAmount;
            EnableGreenDeNoise = LivestackMediator.Plugin.DefaultEnableGreenDeNoise;
            GreenDeNoiseAmount = LivestackMediator.Plugin.DefaultGreenDeNoiseAmount;
            Downsample = LivestackMediator.Plugin.DefaultDownsample;
        }

        [ObservableProperty]
        private BitmapSource stackImage;

        public string Target { get; }

        public string Filter => "RGB";

        [ObservableProperty]
        private bool locked;

        public bool NotLocked => !Locked;

        [ObservableProperty]
        private double redStretchFactor;

        [ObservableProperty]
        private double greenStretchFactor;

        [ObservableProperty]
        private double blueStretchFactor;

        [ObservableProperty]
        private double redBlackClipping;

        [ObservableProperty]
        private double greenBlackClipping;

        [ObservableProperty]
        private double blueBlackClipping;

        [ObservableProperty]
        private bool enableBackgroundExtraction;

        [ObservableProperty]
        private double backgroundExtractionAmount;

        [ObservableProperty]
        private bool enableGreenDeNoise;

        [ObservableProperty]
        private double greenDeNoiseAmount;

        [ObservableProperty]
        private int imageRotation;

        [ObservableProperty]
        private int imageFlipValue;

        [ObservableProperty]
        private int downsample;

        [ObservableProperty]
        private int stackCountRed;

        [ObservableProperty]
        private int stackCountGreen;

        [ObservableProperty]
        private int stackCountBlue;

        private readonly LiveStackTab red;
        private readonly LiveStackTab green;
        private readonly LiveStackTab blue;
        private readonly bool channelsAlreadyAligned;

        public bool NeedsRefresh { get; private set; } = true;

        internal bool UsesSource(LiveStackTab tab) => ReferenceEquals(red, tab) || ReferenceEquals(green, tab) || ReferenceEquals(blue, tab);

        [RelayCommand]
        public Task Refresh(CancellationToken token) {
            return LiveStackPreview.RenderAsync(() => Render(token), token);
        }

        private void Render(CancellationToken token) {
            Locked = true;
            try {
                StackCountRed = red.StackCount;
                StackCountGreen = green.StackCount;
                StackCountBlue = blue.StackCount;

                using var redBitmap = RenderChannel(red, RedStretchFactor, RedBlackClipping, token);
                using var greenBitmap = RenderChannel(green, GreenStretchFactor, GreenBlackClipping, token);
                if (greenBitmap == null) {
                    return;
                }
                using var blueBitmap = RenderChannel(blue, BlueStretchFactor, BlueBlackClipping, token);
                if (blueBitmap == null) {
                    return;
                }
                using var colorBitmap = LivestackMediator.GetImageMath().MergeGray16ToRGB48(redBitmap.Bitmap, greenBitmap.Bitmap, blueBitmap.Bitmap);
                if (EnableGreenDeNoise) {
                    LivestackMediator.GetImageMath().ApplyGreenDeNoiseInPlace(colorBitmap, GreenDeNoiseAmount);
                }
                BitmapSource source = ImageUtility.ConvertBitmap(colorBitmap, PixelFormats.Rgb48);
                source.Freeze();
                StackImage = source;
                NeedsRefresh = false;
            } finally {
                Locked = false;
            }
        }

        public void MarkDirty() {
            NeedsRefresh = true;
        }

        [RelayCommand]
        public void RotateImage() {
            ImageRotation = (int)AstroUtil.EuclidianModulus(ImageRotation + 90, 360);
        }

        [RelayCommand]
        public void ImageFlip() {
            ImageFlipValue *= -1;
        }

        [RelayCommand]
        public async Task SaveWithDialog() {
            var dialog = new SaveFileDialog();
            dialog.Title = "Save stack";
            dialog.FileName = $"{Target}-{Filter}.png";
            dialog.DefaultExt = ".png";
            dialog.Filter = "Portable Network Graphics|*.png;";

            if (dialog.ShowDialog() == true) {
                await Task.Run(() => SaveToDisk(dialog.FileName));
            }
        }

        private ImageBufferLease AlignTab(LiveStackTab reference, LiveStackTab target, CancellationToken token) {
            if (reference.Properties.Width != target.Properties.Width || reference.Properties.Height != target.Properties.Height) {
                Logger.Warning("Live Stack color combination skipped: channel dimensions differ.");
                return null;
            }
            AlignmentResult alignment = LivestackMediator.GetImageTransformer().ComputeAlignment(target.ReferenceStars, reference.ReferenceStars, reference.Properties.Width, reference.Properties.Height, token);
            if (!alignment.Success) {
                Logger.Warning($"Live Stack color combination skipped: {alignment}");
                return null;
            }
            Logger.Info($"Live Stack color combination alignment: {alignment}");
            token.ThrowIfCancellationRequested();
            ImageBufferLease pixels = ImageBufferPool.Shared.Rent(target.Stack.Length);
            try {
                LivestackMediator.GetImageTransformer().ApplyAffineTransformationInto(target.Stack, pixels.Buffer, target.Properties.Width, target.Properties.Height, alignment.Matrix);
                return pixels;
            } catch {
                pixels.Dispose();
                throw;
            }
        }

        private ImageMath.BitmapWithMedian RenderChannel(LiveStackTab tab, double stretchFactor, double blackClipping, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            bool needsAlignment = !channelsAlreadyAligned && !ReferenceEquals(tab, red);
            using ImageBufferLease aligned = needsAlignment ? AlignTab(red, tab, token) : null;
            if (needsAlignment && aligned == null) {
                return null;
            }
            using ImageBufferLease background = EnableBackgroundExtraction && aligned == null ? ImageBufferPool.Shared.Rent(tab.Stack.Length) : null;
            float[] pixels = aligned?.Buffer ?? tab.Stack;
            if (EnableBackgroundExtraction) {
                float[] output = background?.Buffer ?? aligned.Buffer;
                LivestackMediator.GetImageMath().CreateBackgroundExtractedPreviewInto(pixels, output, tab.Properties.Width, tab.Properties.Height, BackgroundExtractionAmount);
                pixels = output;
            }
            return LiveStackPreview.CreateStretchedBitmap(pixels, tab.Properties, stretchFactor, blackClipping, Downsample, token);
        }

        private string GetStackFilePath() {
            var destinationFolder = Path.Combine(LivestackMediator.Plugin.WorkingDirectory, "stacks");
            if (!Directory.Exists(destinationFolder)) { Directory.CreateDirectory(destinationFolder); }

            var destinationFile = Path.Combine(destinationFolder, CoreUtil.ReplaceAllInvalidFilenameChars($"{Target}-{Filter}.png"));
            return destinationFile;
        }

        public void AutoSaveToDisk() {
            SaveToDisk(GetStackFilePath());
        }

        private void SaveToDisk(string path) {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(StackImage));

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write)) {
                encoder.Save(stream);
            }
        }
    }
}
