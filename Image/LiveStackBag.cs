using NINA.Core.Utility;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.Plugin.Livestack.Image {

    public partial class LiveStackBag {
        public static readonly string NOTARGET = "No_target";
        public static readonly string NOFILTER = "No_filter";

        public LiveStackBag(string target, string filter, ImageProperties properties, List<Accord.Point> referenceStars) {
            Filter = filter;
            Target = target;
            Properties = properties;
            ReferenceImageStars = referenceStars;
            ImageCount = 0;
        }

        public ImageProperties Properties { get; private set; }

        public List<Accord.Point> ReferenceImageStars { get; private set; }
        public ushort[] Stack { get; private set; }
        public string Filter { get; }
        public string Target { get; }
        public int ImageCount { get; private set; }

        public void Add(ushort[] image) {
            if (Stack == null) {
                Stack = image;
            } else {
                ImageMath.SequentialStack(image, Stack, ImageCount);
            }
            ImageCount++;
        }

        public BitmapSource Render(double stretchFactor, double blackClipping, int downsample) {
            using var bmp = ImageMath.CreateGrayBitmap(Stack, Properties.Width, Properties.Height);
            var (redMedian, redMAD) = ImageMath.CalculateMedianAndMAD(Stack);
            var filter = ImageUtility.GetColorRemappingFilter(new MedianOnlyStatistics(redMedian, redMAD, Properties.BitDepth), stretchFactor, blackClipping, PixelFormats.Gray16);
            filter.ApplyInPlace(bmp);

            BitmapSource source;
            if (downsample > 1) {
                using var downsampledBmp = ImageMath.DownsampleGray16(bmp, downsample);
                source = ImageUtility.ConvertBitmap(downsampledBmp);
            } else {
                source = ImageUtility.ConvertBitmap(bmp);
            }

            source.Freeze();
            return source;
        }

        public void ForcePushReference(IImageData reference, List<Accord.Point> referenceStars, ushort[] stack) {
            Properties = reference.Properties;
            ReferenceImageStars = referenceStars;
            Stack = stack;
            ImageCount = 1;
        }

        private string GetStackFilePath() {
            var destinationFolder = Path.Combine(LivestackMediator.Plugin.WorkingDirectory, "stacks");
            if (!Directory.Exists(destinationFolder)) { Directory.CreateDirectory(destinationFolder); }

            var destinationFile = Path.Combine(destinationFolder, CoreUtil.ReplaceAllInvalidFilenameChars($"{Target}-{Filter}.fits"));
            return destinationFile;
        }

        public void SaveToDisk() {
            var destinationFile = GetStackFilePath();
            var tempFile = Path.Combine(destinationFile + ".tmp");

            if (File.Exists(tempFile)) {
                File.Delete(tempFile);
            }

            var stackFits = new CFitsioFITSExtendedWriter(tempFile, Stack, Properties.Width, Properties.Height, CfitsioNative.COMPRESSION.NOCOMPRESS);
            stackFits.AddHeader("IMGCOUNT", ImageCount, "");
            stackFits.Close();

            if (File.Exists(destinationFile)) {
                File.Delete(destinationFile);
            }
            File.Move(tempFile, destinationFile);
        }
    }
}