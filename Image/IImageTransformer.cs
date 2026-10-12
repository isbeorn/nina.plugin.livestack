using Accord;
using NINA.Image.ImageAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace NINA.Plugin.Livestack.Image {

    public interface IImageTransformer {

        float[] ApplyAffineTransformation(float[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        void ApplyAffineTransformationInto(float[] sourceImageData, float[] destinationImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        void ApplyAffineTransformationAndStack(float[] sourceImageData, float[] stackImageData, int stackImageCount, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        void ApplyAffineTransformationAndStack(float[] sourceImageData, float[] stackImageData, uint[] contributionCounts, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        float[] ApplyAffineTransformation(ushort[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        void ApplyAffineTransformationInto(ushort[] sourceImageData, float[] destinationImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        void ApplyAffineTransformationAndStack(ushort[] sourceImageData, float[] stackImageData, int stackImageCount, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        void ApplyAffineTransformationAndStack(ushort[] sourceImageData, float[] stackImageData, uint[] contributionCounts, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        ushort[] ApplyAffineTransformationAsUshort(float[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        void ApplyAffineTransformationAsUshortInto(float[] sourceImageData, ushort[] destinationImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false);

        double[,] ComputeAffineTransformation(List<Point> stars, List<Point> referenceStars);

        AlignmentResult ComputeAlignment(List<Point> stars, List<Point> referenceStars, int width, int height, CancellationToken token = default);

        AlignmentResult ComputeAlignment(AlignmentStarCatalog stars, AlignmentStarCatalog referenceStars, int width, int height, double[,] predictedMatrix = null, CancellationToken token = default) {
            List<Point> current = stars.MatchStars.ToList();
            List<Point> reference = object.ReferenceEquals(stars, referenceStars) ? current : referenceStars.MatchStars.ToList();
            return ComputeAlignment(current, reference, width, height, token);
        }

        AlignmentStarCatalog PrepareAlignmentStars(List<DetectedStar> starList, int width, int height) {
            List<Point> stars = GetStars(starList, width, height);
            return new AlignmentStarCatalog(stars, stars);
        }

        List<Point> GetStars(List<DetectedStar> starList, int width, int height);

        bool IsFlippedImage(double[,] affineMatrix);
    }
}
