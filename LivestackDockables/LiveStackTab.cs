using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack.Image;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using OxyPlot;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.Plugin.Livestack.LivestackDockables {

    public partial class LiveStackTab : BaseVM, IStackTab {
        private LiveStackBag bag;

        [ObservableProperty]
        private BitmapSource stackImage;

        [ObservableProperty]
        private string target;

        [ObservableProperty]
        private string filter;

        [ObservableProperty]
        private bool locked;

        [ObservableProperty]
        private int stackCount;

        [ObservableProperty]
        private double stretchFactor;

        [ObservableProperty]
        private double blackClipping;

        [ObservableProperty]
        private bool enableBackgroundExtraction;

        [ObservableProperty]
        private double backgroundExtractionAmount;

        [ObservableProperty]
        private int imageRotation;

        [ObservableProperty]
        private int imageFlipValue;

        [ObservableProperty]
        private int downsample;

        public List<Accord.Point> ReferenceStars => bag.ReferenceImageStars;

        public float[] Stack => bag.Stack;

        public ImageProperties Properties => bag.Properties;

        public LiveStackTab(IProfileService profileService, LiveStackBag bag) : base(profileService) {
            this.target = bag.Target;
            this.filter = bag.Filter;
            this.bag = bag;
            stretchFactor = LivestackMediator.Plugin.DefaultStretchAmount;
            blackClipping = LivestackMediator.Plugin.DefaultBlackClipping;
            enableBackgroundExtraction = LivestackMediator.Plugin.DefaultEnableBackgroundExtraction;
            backgroundExtractionAmount = LivestackMediator.Plugin.DefaultBackgroundExtractionAmount;
            imageRotation = 0;
            imageFlipValue = 1;
            downsample = LivestackMediator.Plugin.DefaultDownsample;
        }

        [RelayCommand]
        public void ResetSettings() {
            StretchFactor = LivestackMediator.Plugin.DefaultStretchAmount;
            BlackClipping = LivestackMediator.Plugin.DefaultBlackClipping;
            EnableBackgroundExtraction = LivestackMediator.Plugin.DefaultEnableBackgroundExtraction;
            BackgroundExtractionAmount = LivestackMediator.Plugin.DefaultBackgroundExtractionAmount;
            Downsample = LivestackMediator.Plugin.DefaultDownsample;
        }

        [RelayCommand]
        public async Task Refresh(CancellationToken token) {
            try {
                await Task.Run(() => {
                    if (Stack == null) {
                        // Nothing is stacked yet. This happens when the tab was created but its first frame got skipped before a reference was pushed
                        StackImage = null;
                        StackCount = bag.ImageCount;
                        return;
                    }
                    StackImage = Render(StretchFactor, BlackClipping, EnableBackgroundExtraction, BackgroundExtractionAmount, Downsample);
                    StackCount = bag.ImageCount;
                }, token);
            } catch {
            }
        }

        public void ResetStack() {
            bag.Reset();
            StackCount = 0;
            StackImage = null;
        }

        private BitmapSource Render(double stretchFactor, double blackClipping, bool enableBackgroundExtraction, double backgroundExtractionAmount, int downsample) {
            float[] previewData = enableBackgroundExtraction
                ? LivestackMediator.GetImageMath().CreateBackgroundExtractedPreview(Stack, Properties.Width, Properties.Height, backgroundExtractionAmount)
                : Stack;
            using var bmp = LivestackMediator.GetImageMath().CreateGrayBitmap(previewData, Properties.Width, Properties.Height);
            var filter = ImageUtility.GetColorRemappingFilter(new MedianOnlyStatistics(bmp.Median, bmp.MedianAbsoluteDeviation, Properties.BitDepth), stretchFactor, blackClipping, PixelFormats.Gray16);
            filter.ApplyInPlace(bmp.Bitmap);

            BitmapSource source;
            if (downsample > 1) {
                using var downsampledBmp = LivestackMediator.GetImageMath().DownsampleGray16(bmp.Bitmap, downsample);
                source = ImageUtility.ConvertBitmap(downsampledBmp);
            } else {
                source = ImageUtility.ConvertBitmap(bmp.Bitmap);
            }
            source.Freeze();
            return source;
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
            dialog.FileName = $"{Target}-{Filter}.fits";
            dialog.DefaultExt = ".fits";
            dialog.Filter = "Flexible Image Transport System|*.fits;*.fit;";

            if (dialog.ShowDialog() == true) {
                await Task.Run(() => bag.SaveToDisk(dialog.FileName));
            }
        }

        public void AddImage(float[] data) {
            bag.Add(data);
        }

        public void AddTransformedImage(float[] data, double[,] affineMatrix, bool flippedImage) {
            bag.AddTransformed(data, affineMatrix, flippedImage);
        }

        public void AddTransformedImage(ushort[] data, double[,] affineMatrix, bool flippedImage) {
            bag.AddTransformed(data, affineMatrix, flippedImage);
        }

        public void ForcePushReference(ImageProperties properties, List<Accord.Point> referenceStars, float[] stack) {
            bag.ForcePushReference(properties, referenceStars, stack);
        }

        public void SaveToDisk() {
            bag.AutoSaveToDisk();
        }
    }
}
