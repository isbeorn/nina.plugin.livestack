using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
            imageRotation = 0;
            imageFlipValue = 1;
            downsample = LivestackMediator.Plugin.DefaultDownsample;
        }

        [RelayCommand]
        public async Task Refresh(CancellationToken token) {
            try {
                await Task.Run(() => {
                    StackImage = Render(StretchFactor, BlackClipping, Downsample);
                    StackCount = bag.ImageCount;
                }, token);
            } catch {
            } finally {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private BitmapSource Render(double stretchFactor, double blackClipping, int downsample) {
            var ushortStack = Stack.ToUShortArray();
            using var bmp = ImageMath.CreateGrayBitmap(ushortStack, Properties.Width, Properties.Height);
            var filter = ImageUtility.GetColorRemappingFilter(new MedianOnlyStatistics(bmp.Median, bmp.MedianAbsoluteDeviation, Properties.BitDepth), stretchFactor, blackClipping, PixelFormats.Gray16);
            filter.ApplyInPlace(bmp.Bitmap);

            BitmapSource source;
            if (downsample > 1) {
                using var downsampledBmp = ImageMath.DownsampleGray16(bmp.Bitmap, downsample);
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

        public void AddImage(float[] data) {
            bag.Add(data);
        }

        public void ForcePushReference(ImageProperties properties, List<Accord.Point> referenceStars, float[] stack) {
            bag.ForcePushReference(properties, referenceStars, stack);
        }

        public void SaveToDisk() {
            bag.SaveToDisk();
        }
    }
}