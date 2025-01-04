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

        public ColorCombinationTab(IProfileService profileService, LiveStackTab red, LiveStackTab green, LiveStackTab blue) : base(profileService) {
            this.Target = red.Target;
            this.profileService = profileService;
            this.red = red;
            this.green = green;
            this.blue = blue;
            redStretchFactor = LivestackMediator.Plugin.DefaultStretchAmount;
            greenStretchFactor = LivestackMediator.Plugin.DefaultStretchAmount;
            blueStretchFactor = LivestackMediator.Plugin.DefaultStretchAmount;
            redBlackClipping = LivestackMediator.Plugin.DefaultBlackClipping;
            greenBlackClipping = LivestackMediator.Plugin.DefaultBlackClipping;
            blueBlackClipping = LivestackMediator.Plugin.DefaultBlackClipping;
            enableGreenDeNoise = LivestackMediator.Plugin.DefaultEnableGreenDeNoise;
            greenDeNoiseAmount = LivestackMediator.Plugin.DefaultGreenDeNoiseAmount;
            imageRotation = 0;
            imageFlipValue = 1;
            downsample = LivestackMediator.Plugin.DefaultDownsample;
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

        [RelayCommand]
        public async Task Refresh(CancellationToken token) {
            try {
                await Task.Run(() => {
                    Locked = true;
                    try {
                        StackCountRed = red.StackCount;
                        StackCountGreen = green.StackCount;
                        StackCountBlue = blue.StackCount;

                        var greenData = AlignTab(red, green);
                        var blueData = AlignTab(red, blue);

                        using var redBitmap = ImageMath.CreateGrayBitmap(red.Stack, red.Properties.Width, red.Properties.Height);
                        var filter = ImageUtility.GetColorRemappingFilter(new MedianOnlyStatistics(redBitmap.Median, redBitmap.MedianAbsoluteDeviation, red.Properties.BitDepth), RedStretchFactor, RedBlackClipping, PixelFormats.Gray16);
                        filter.ApplyInPlace(redBitmap.Bitmap);
                        token.ThrowIfCancellationRequested();

                        using var blueBitmap = ImageMath.CreateGrayBitmap(blueData, blue.Properties.Width, blue.Properties.Height);
                        var filterBlue = ImageUtility.GetColorRemappingFilter(new MedianOnlyStatistics(blueBitmap.Median, blueBitmap.MedianAbsoluteDeviation, blue.Properties.BitDepth), BlueStretchFactor, BlueBlackClipping, PixelFormats.Gray16);
                        filterBlue.ApplyInPlace(blueBitmap.Bitmap);
                        token.ThrowIfCancellationRequested();

                        using var greenBitmap = ImageMath.CreateGrayBitmap(greenData, green.Properties.Width, green.Properties.Height);
                        var filterGreen = ImageUtility.GetColorRemappingFilter(new MedianOnlyStatistics(greenBitmap.Median, greenBitmap.MedianAbsoluteDeviation, green.Properties.BitDepth), GreenStretchFactor, GreenBlackClipping, PixelFormats.Gray16);
                        filterGreen.ApplyInPlace(greenBitmap.Bitmap);
                        token.ThrowIfCancellationRequested();

                        BitmapSource source;
                        if (Downsample > 1) {
                            using var downsampledRed = ImageMath.DownsampleGray16(redBitmap.Bitmap, Downsample);
                            using var downsampledGreen = ImageMath.DownsampleGray16(greenBitmap.Bitmap, Downsample);
                            using var downsampledBlue = ImageMath.DownsampleGray16(blueBitmap.Bitmap, Downsample);

                            using var colorBitmap = ImageMath.MergeGray16ToRGB48(downsampledRed, downsampledGreen, downsampledBlue);
                            if (EnableGreenDeNoise) {
                                ImageMath.ApplyGreenDeNoiseInPlace(colorBitmap, GreenDeNoiseAmount);
                            }
                            source = ImageUtility.ConvertBitmap(colorBitmap, PixelFormats.Rgb48);
                        } else {
                            using var colorBitmap = ImageMath.MergeGray16ToRGB48(redBitmap.Bitmap, greenBitmap.Bitmap, blueBitmap.Bitmap);
                            if (EnableGreenDeNoise) {
                                ImageMath.ApplyGreenDeNoiseInPlace(colorBitmap, GreenDeNoiseAmount);
                            }
                            source = ImageUtility.ConvertBitmap(colorBitmap, PixelFormats.Rgb48);
                        }

                        source.Freeze();
                        StackImage = source;
                    } finally {
                        Locked = false;
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    }
                }, token);
            } catch { }
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

        private float[] AlignTab(LiveStackTab reference, LiveStackTab target) {
            var stars = target.ReferenceStars;
            var affineTransformationMatrix = ImageTransformer.ComputeAffineTransformation(stars, reference.ReferenceStars);
            var flipped = ImageTransformer.IsFlippedImage(affineTransformationMatrix);
            if (flipped) {
                // The reference is flipped - most likely a meridian flip happend. Rotate starlist by 180° and recompute the affine transform for a tighter fit. The apply method will then account for the indexing switch
                stars = ImageMath.Flip(stars, target.Properties.Width, target.Properties.Height);
                affineTransformationMatrix = ImageTransformer.ComputeAffineTransformation(stars, reference.ReferenceStars);
            }
            return ImageTransformer.ApplyAffineTransformation(target.Stack, target.Properties.Width, target.Properties.Height, affineTransformationMatrix, flipped);
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