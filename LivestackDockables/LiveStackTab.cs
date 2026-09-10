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
        private LiveStackPreview.Settings? renderedSettings;
        private long renderedRevision;

        partial void OnStackImageChanged(BitmapSource value) {
            renderedSettings = null;
        }

        internal BitmapSource GetRenderedPreview(LiveStackPreview.Settings settings) {
            return renderedSettings == settings && renderedRevision == bag.Revision && (renderedRevision & 1) == 0
                ? StackImage : null;
        }

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
        public Task Refresh(CancellationToken token) {
            return LiveStackPreview.RenderAsync(() => {
                LiveStackPreview.Settings settings = new(StretchFactor, BlackClipping, EnableBackgroundExtraction, BackgroundExtractionAmount, Downsample);
                long revision = bag.Revision;
                BitmapSource source = Render(settings.StretchFactor, settings.BlackClipping, settings.EnableBackgroundExtraction, settings.BackgroundExtractionAmount, settings.Downsample, token);
                token.ThrowIfCancellationRequested();
                StackImage = source;
                renderedRevision = revision;
                renderedSettings = settings;
                StackCount = bag.ImageCount;
            }, token);
        }

        private BitmapSource Render(double stretchFactor, double blackClipping, bool enableBackgroundExtraction, double backgroundExtractionAmount, int downsample, CancellationToken token) {
            using ImageBufferLease preview = enableBackgroundExtraction ? ImageBufferPool.Shared.Rent(Stack.Length) : null;
            if (preview != null) {
                LivestackMediator.GetImageMath().CreateBackgroundExtractedPreviewInto(Stack, preview.Buffer, Properties.Width, Properties.Height, backgroundExtractionAmount);
            }
            float[] previewData = preview?.Buffer ?? Stack;
            using var bitmap = LiveStackPreview.CreateStretchedBitmap(previewData, Properties, stretchFactor, blackClipping, downsample, token);
            BitmapSource source = ImageUtility.ConvertBitmap(bitmap.Bitmap);
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

        public bool IsCompatible(ImageProperties properties) {
            return bag.IsCompatible(properties);
        }

        internal AlignmentResult AlignAndAdd(ImageBufferLease data, ImageProperties properties, List<Accord.Point> stars, CancellationToken token) {
            return bag.AlignAndAdd(data, properties, stars, token);
        }

        public AlignmentResult AlignAndAdd(float[] data, ImageProperties properties, List<Accord.Point> stars, CancellationToken token) {
            return bag.AlignAndAdd(data, properties, stars, token);
        }

        public AlignmentResult AlignAndAdd(ushort[] data, ImageProperties properties, List<Accord.Point> stars, CancellationToken token) {
            return bag.AlignAndAdd(data, properties, stars, token);
        }

        public void AddTransformedImage(float[] data, double[,] affineMatrix, bool flippedImage) {
            bag.AddTransformed(data, affineMatrix, flippedImage);
        }

        public void AddTransformedImage(ushort[] data, double[,] affineMatrix, bool flippedImage) {
            bag.AddTransformed(data, affineMatrix, flippedImage);
        }

        public void SaveToDisk() {
            bag.AutoSaveToDisk();
        }
    }
}
