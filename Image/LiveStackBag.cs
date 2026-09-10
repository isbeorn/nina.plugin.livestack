using NINA.Core.Utility;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.Plugin.Livestack.Image {

    public partial class LiveStackBag {
        public static readonly string NOTARGET = "No_target";
        public static readonly string NOFILTER = "No_filter";
        public static readonly string RED_OSC = "R_OSC";
        public static readonly string GREEN_OSC = "G_OSC";
        public static readonly string BLUE_OSC = "B_OSC";

        public LiveStackBag(string target, string filter, ImageProperties properties, ImageMetaData metaData, List<Accord.Point> referenceStars) {
            Filter = filter;
            Target = target;
            Properties = properties;
            MetaData = metaData;
            ReferenceImageStars = referenceStars;
            ImageCount = 0;
        }

        public ImageProperties Properties { get; private set; }
        public ImageMetaData MetaData { get; }
        public List<Accord.Point> ReferenceImageStars { get; private set; }
        public float[] Stack { get; private set; }

        public string Filter { get; }

        public string Target { get; }
        public int ImageCount { get; private set; }

        // Odd while pixels are changing, even when stable. Previews from an older or active write cannot be reused.
        private long revision;
        internal long Revision => Volatile.Read(ref revision);

        private Array contributionCounts;

        private static readonly double[,] identity = { { 1d, 0d, 0d }, { 0d, 1d, 0d }, { 0d, 0d, 1d } };

        public bool IsCompatible(ImageProperties properties) {
            return properties != null && (Stack == null
                || (Properties.Width == properties.Width && Properties.Height == properties.Height
                    && Properties.BitDepth == properties.BitDepth && Properties.IsBayered == properties.IsBayered
                    && Properties.Gain == properties.Gain && Properties.Offset == properties.Offset));
        }

        /// <summary>Validates the incoming frame and alignment before committing its pixels.</summary>
        internal AlignmentResult AlignAndAdd(ImageBufferLease image, ImageProperties properties, List<Accord.Point> stars, CancellationToken token = default) {
            float[] pixels = image.Buffer;
            AlignmentResult result = AlignAndAdd(pixels, properties, stars, token);
            if (result.Success && ReferenceEquals(Stack, pixels)) {
                image.Detach();
            }
            return result;
        }

        public AlignmentResult AlignAndAdd(float[] image, ImageProperties properties, List<Accord.Point> stars, CancellationToken token = default) {
            AlignmentResult result = PlanAlignment(image, properties, stars, token);
            if (result.Success) {
                token.ThrowIfCancellationRequested();
                if (Stack == null) {
                    ForcePushReference(properties, stars, image);
                } else {
                    AddTransformed(image, result.Matrix, false);
                }
            }
            return result;
        }

        public AlignmentResult AlignAndAdd(ushort[] image, ImageProperties properties, List<Accord.Point> stars, CancellationToken token = default) {
            AlignmentResult result = PlanAlignment(image, properties, stars, token);
            if (result.Success) {
                token.ThrowIfCancellationRequested();
                if (Stack == null) {
                    float[] reference = new float[image.Length];
                    for (int i = 0; i < image.Length; i++) {
                        reference[i] = image[i] / (float)ushort.MaxValue;
                    }
                    ForcePushReference(properties, stars, reference);
                } else {
                    AddTransformed(image, result.Matrix, false);
                }
            }
            return result;
        }

        private AlignmentResult PlanAlignment(Array image, ImageProperties properties, List<Accord.Point> stars, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            if (properties == null || image == null || properties.Width <= 0 || properties.Height <= 0
                || image.Length != (long)properties.Width * properties.Height) {
                return AlignmentResult.Rejected("Incoming pixels do not match the frame dimensions.");
            }
            if (!IsCompatible(properties)) {
                return AlignmentResult.Rejected("Frame dimensions, bit depth, sensor mode, gain or offset differ from the reference. Start a new stack for this capture setup.");
            }
            return LivestackMediator.GetImageTransformer().ComputeAlignment(stars, Stack == null ? stars : ReferenceImageStars, properties.Width, properties.Height, token);
        }

        public void Add(float[] image) {
            if (Stack == null) {
                ForcePushReference(Properties, ReferenceImageStars, image);
            } else {
                AddTransformed(image, identity, false);
            }
        }

        public void AddTransformed(float[] image, double[,] affineMatrix, bool flippedImage) {
            AddTransformedCore(image, affineMatrix, flippedImage);
        }

        public void AddTransformed(ushort[] image, double[,] affineMatrix, bool flippedImage) {
            AddTransformedCore(image, affineMatrix, flippedImage);
        }

        private void AddTransformedCore<T>(T[] image, double[,] affineMatrix, bool flippedImage)
                where T : unmanaged, INumberBase<T> {
            ValidateAddition(image, affineMatrix);
            float[] stack = Stack ?? new float[image.Length];
            Array counts = GetContributionCounts(image.Length);
            Interlocked.Increment(ref revision);
            try {
                switch (counts) {
                    case byte[] small:
                        AffineResampler.AccumulateValidated(image, stack, small, 0, Properties.Width, Properties.Height, affineMatrix, flippedImage);
                        break;
                    case ushort[] medium:
                        AffineResampler.AccumulateValidated(image, stack, medium, 0, Properties.Width, Properties.Height, affineMatrix, flippedImage);
                        break;
                    case uint[] large:
                        AffineResampler.AccumulateValidated(image, stack, large, 0, Properties.Width, Properties.Height, affineMatrix, flippedImage);
                        break;
                }
                Stack = stack;
                contributionCounts = counts;
                ImageCount++;
            } finally {
                Interlocked.Increment(ref revision);
            }
        }

        private Array GetContributionCounts(int length) {
            // ImageCount bounds every pixel's count, including pixels with missing coverage.
            // Validation checks its int limit before this method, so uint counts cannot overflow.
            return contributionCounts switch {
                null => new byte[length],
                byte[] small when ImageCount == byte.MaxValue => PromoteCounts<byte, ushort>(small),
                ushort[] medium when ImageCount == ushort.MaxValue => PromoteCounts<ushort, uint>(medium),
                _ => contributionCounts
            };
        }

        private static TDestination[] PromoteCounts<TSource, TDestination>(TSource[] source)
                where TSource : unmanaged, INumberBase<TSource>
                where TDestination : unmanaged, INumberBase<TDestination> {
            TDestination[] destination = GC.AllocateUninitializedArray<TDestination>(source.Length);
            for (int i = 0; i < source.Length; i++) {
                destination[i] = TDestination.CreateChecked(source[i]);
            }
            return destination;
        }

        private void ValidateAddition(Array image, double[,] matrix) {
            ArgumentNullException.ThrowIfNull(image);
            if (image.Length != AffineResampler.GetLength(Properties.Width, Properties.Height) || ReferenceEquals(image, Stack)) {
                throw new ArgumentException("Incoming pixels must match the stack dimensions and use a separate buffer.", nameof(image));
            }
            if (ImageCount == int.MaxValue) {
                throw new InvalidOperationException("The stack frame count would overflow.");
            }
            AlignmentGeometry.ValidateForStack(matrix, Properties.Width, Properties.Height);
        }

        public void ForcePushReference(ImageProperties properties, List<Accord.Point> referenceStars, float[] stack) {
            ArgumentNullException.ThrowIfNull(properties);
            ArgumentNullException.ThrowIfNull(stack);
            if (stack.Length != AffineResampler.GetLength(properties.Width, properties.Height)) {
                throw new ArgumentException("Reference pixels do not match the frame dimensions.", nameof(stack));
            }
            byte[] counts = new byte[stack.Length];
            Interlocked.Increment(ref revision);
            try {
                for (int i = 0; i < stack.Length; i++) {
                    if (float.IsFinite(stack[i])) {
                        counts[i] = 1;
                    } else {
                        stack[i] = 0;
                    }
                }
                Properties = properties;
                ReferenceImageStars = referenceStars;
                Stack = stack;
                contributionCounts = counts;
                ImageCount = 1;
            } finally {
                Interlocked.Increment(ref revision);
            }
        }

        private string GetStackFilePath() {
            var destinationFolder = Path.Combine(LivestackMediator.Plugin.WorkingDirectory, "stacks");
            if (!Directory.Exists(destinationFolder)) { Directory.CreateDirectory(destinationFolder); }

            var destinationFile = Path.Combine(destinationFolder, CoreUtil.ReplaceAllInvalidFilenameChars($"{Target}-{Filter}.fits"));
            return destinationFile;
        }

        public void AutoSaveToDisk() {
            var destinationFile = GetStackFilePath();
            var tempFile = Path.Combine(destinationFile + ".tmp");

            if (File.Exists(tempFile)) {
                File.Delete(tempFile);
            }

            SaveToDisk(tempFile);

            if (File.Exists(destinationFile)) {
                File.Delete(destinationFile);
            }
            File.Move(tempFile, destinationFile);
        }

        public void SaveToDisk(string path) {
            var stackFits = new CFitsioFITSExtendedWriter(path, Stack, Properties.Width, Properties.Height, CfitsioNative.COMPRESSION.NOCOMPRESS);
            stackFits.PopulateHeaderCards(MetaData);
            stackFits.AddHeader("IMGCOUNT", ImageCount, "");
            stackFits.Close();
        }
    }
}
