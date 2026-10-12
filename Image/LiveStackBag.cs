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
        private AlignmentStarCatalog referenceCatalog;
        private IReadOnlyList<Accord.Point> referenceCatalogSource;
        private double[,] lastCommittedMatrix;

        public AlignmentStarCatalog ReferenceCatalog {
            get {
                if (ReferenceImageStars == null) {
                    return null;
                }
                if (referenceCatalog == null || !ReferenceCatalogMatchesLegacyStars()) {
                    referenceCatalog = new AlignmentStarCatalog(ReferenceImageStars, ReferenceImageStars);
                    referenceCatalogSource = referenceCatalog.SupportStars;
                    lastCommittedMatrix = null;
                }
                return referenceCatalog;
            }
        }

        private bool ReferenceCatalogMatchesLegacyStars() {
            // Normal production lists are already distinct and bounded, so avoid a set allocation.
            return referenceCatalogSource.SequenceEqual(ReferenceImageStars)
                || referenceCatalogSource.SequenceEqual(ReferenceImageStars.Distinct().Take(AlignmentStarCatalog.MaximumSupportStars));
        }

        internal void CommitAlignment(AlignmentResult result) {
            if (result.Success) {
                lastCommittedMatrix = result.Matrix;
            }
        }

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
            return CompleteLeaseAlignment(image, AlignAndAdd(image.Buffer, properties, stars, token));
        }

        public AlignmentResult AlignAndAdd(float[] image, ImageProperties properties, List<Accord.Point> stars, CancellationToken token = default) {
            AlignmentResult result = PlanAlignment(image, properties, stars, token);
            CommitAlignedPixels(image, properties, stars, null, result, token);
            return result;
        }

        public AlignmentResult AlignAndAdd(ushort[] image, ImageProperties properties, List<Accord.Point> stars, CancellationToken token = default) {
            AlignmentResult result = PlanAlignment(image, properties, stars, token);
            CommitAlignedPixels(image, properties, stars, null, result, token);
            return result;
        }

        internal AlignmentResult AlignAndAdd(ImageBufferLease image, ImageProperties properties, AlignmentStarCatalog stars, CancellationToken token = default) {
            return CompleteLeaseAlignment(image, AlignAndAdd(image.Buffer, properties, stars, token));
        }

        private AlignmentResult CompleteLeaseAlignment(ImageBufferLease image, AlignmentResult result) {
            if (result.Success && ReferenceEquals(Stack, image.Buffer)) {
                image.Detach();
            }
            return result;
        }

        public AlignmentResult AlignAndAdd(float[] image, ImageProperties properties, AlignmentStarCatalog stars, CancellationToken token = default) {
            AlignmentResult result = PlanAlignment(image, properties, stars, token);
            CommitAlignedPixels(image, properties, null, stars, result, token);
            return result;
        }

        public AlignmentResult AlignAndAdd(ushort[] image, ImageProperties properties, AlignmentStarCatalog stars, CancellationToken token = default) {
            AlignmentResult result = PlanAlignment(image, properties, stars, token);
            CommitAlignedPixels(image, properties, null, stars, result, token);
            return result;
        }

        private void CommitAlignedPixels<T>(T[] image, ImageProperties properties, List<Accord.Point> stars, AlignmentStarCatalog catalog, AlignmentResult result, CancellationToken token)
                where T : unmanaged, INumberBase<T> {
            if (result.Success) {
                token.ThrowIfCancellationRequested();
                if (Stack == null) {
                    ForcePushReference(properties, stars ?? catalog.MatchStars.ToList(), NormalizeReference(image));
                    if (catalog != null) {
                        referenceCatalog = catalog;
                        referenceCatalogSource = catalog.MatchStars;
                    }
                } else {
                    AddTransformedCore(image, result.Matrix, false);
                }
                if (catalog != null) {
                    CommitAlignment(result);
                }
            }
        }

        private static float[] NormalizeReference<T>(T[] image) where T : unmanaged, INumberBase<T> {
            if (image is float[] pixels) {
                return pixels;
            }
            float[] reference = new float[image.Length];
            for (int i = 0; i < image.Length; i++) {
                reference[i] = float.CreateChecked(image[i]) / ushort.MaxValue;
            }
            return reference;
        }

        private AlignmentResult PlanAlignment(Array image, ImageProperties properties, AlignmentStarCatalog stars, CancellationToken token) {
            string rejection = ValidateIncomingFrame(image, properties, token);
            if (rejection != null) {
                return AlignmentResult.Rejected(rejection);
            }
            AlignmentStarCatalog reference = Stack == null ? stars : ReferenceCatalog;
            return LivestackMediator.GetImageTransformer().ComputeAlignment(stars, reference, properties.Width, properties.Height, lastCommittedMatrix, token);
        }

        private AlignmentResult PlanAlignment(Array image, ImageProperties properties, List<Accord.Point> stars, CancellationToken token) {
            string rejection = ValidateIncomingFrame(image, properties, token);
            return rejection == null
                ? LivestackMediator.GetImageTransformer().ComputeAlignment(stars, Stack == null ? stars : ReferenceImageStars, properties.Width, properties.Height, token)
                : AlignmentResult.Rejected(rejection);
        }

        private string ValidateIncomingFrame(Array image, ImageProperties properties, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            if (properties == null || image == null || properties.Width <= 0 || properties.Height <= 0
                || image.Length != (long)properties.Width * properties.Height) {
                return "Incoming pixels do not match the frame dimensions.";
            }
            if (!IsCompatible(properties)) {
                return "Frame dimensions, bit depth, sensor mode, gain or offset differ from the reference. Start a new stack for this capture setup.";
            }
            return null;
        }

        internal AlignmentResult AlignAndAddOsc(LiveStackBag green, LiveStackBag blue, ushort[] redPixels, ushort[] greenPixels, ushort[] bluePixels,
                ImageProperties properties, AlignmentStarCatalog stars, CancellationToken token) {
            AlignmentResult result = PlanAlignment(redPixels, properties, stars, token);
            if (!result.Success) {
                return result;
            }
            string rejection = green.ValidateIncomingFrame(greenPixels, properties, token) ?? blue.ValidateIncomingFrame(bluePixels, properties, token);
            if (rejection != null) {
                return AlignmentResult.Rejected(rejection);
            }
            double[,] matrix = result.Matrix;
            bool firstRed = Stack == null;
            bool firstGreen = green.Stack == null;
            bool firstBlue = blue.Stack == null;
            AlignmentStarCatalog catalog = firstRed ? stars : ReferenceCatalog;
            List<Accord.Point> redReference = firstRed ? catalog.MatchStars.ToList() : null;
            List<Accord.Point> greenReference = firstGreen ? catalog.MatchStars.ToList() : null;
            List<Accord.Point> blueReference = firstBlue ? catalog.MatchStars.ToList() : null;
            PreparedAddition redAddition = firstRed
                ? new PreparedAddition(NormalizeReference(redPixels), new byte[redPixels.Length])
                : PrepareAddition(redPixels, matrix);
            PreparedAddition greenAddition = green.PrepareAddition(greenPixels, matrix);
            PreparedAddition blueAddition = blue.PrepareAddition(bluePixels, matrix);

            // Reserve all stack/count buffers before any channel writes. Resampling itself is
            // noncancelable; this is preflight, not rollback after arbitrary worker failures.
            token.ThrowIfCancellationRequested();
            if (firstRed) {
                CommitReference(properties, redReference, redAddition.Pixels, (byte[])redAddition.Counts, catalog);
            } else {
                CommitAddition(redPixels, matrix, false, redAddition);
            }
            green.CommitAddition(greenPixels, matrix, false, greenAddition);
            blue.CommitAddition(bluePixels, matrix, false, blueAddition);
            if (firstGreen) {
                green.SetReferenceCatalog(catalog, greenReference);
            }
            if (firstBlue) {
                blue.SetReferenceCatalog(catalog, blueReference);
            }
            lastCommittedMatrix = matrix;
            return result;
        }

        private void SetReferenceCatalog(AlignmentStarCatalog catalog, List<Accord.Point> stars) {
            referenceCatalog = catalog;
            referenceCatalogSource = catalog.MatchStars;
            ReferenceImageStars = stars;
            lastCommittedMatrix = null;
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
            CommitAddition(image, affineMatrix, flippedImage, PrepareAddition(image, affineMatrix));
        }

        private readonly record struct PreparedAddition(float[] Pixels, Array Counts);

        private PreparedAddition PrepareAddition(Array image, double[,] affineMatrix) {
            ValidateAddition(image, affineMatrix);
            return new PreparedAddition(Stack ?? new float[image.Length], GetContributionCounts(image.Length));
        }

        private void CommitAddition<T>(T[] image, double[,] affineMatrix, bool flippedImage, PreparedAddition addition)
                where T : unmanaged, INumberBase<T> {
            float[] stack = addition.Pixels;
            Array counts = addition.Counts;
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
            CommitReference(properties, referenceStars, stack, counts);
        }

        private void CommitReference(ImageProperties properties, List<Accord.Point> referenceStars, float[] stack, byte[] counts, AlignmentStarCatalog catalog = null) {
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
                referenceCatalog = catalog;
                referenceCatalogSource = catalog?.MatchStars;
                lastCommittedMatrix = null;
                Stack = stack;
                contributionCounts = counts;
                ImageCount = 1;
            } finally {
                Interlocked.Increment(ref revision);
            }
        }

        public void ForcePushReference(ImageProperties properties, AlignmentStarCatalog stars, float[] stack) {
            ArgumentNullException.ThrowIfNull(stars);
            ForcePushReference(properties, stars.MatchStars.ToList(), stack);
            referenceCatalog = stars;
            referenceCatalogSource = stars.MatchStars;
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
