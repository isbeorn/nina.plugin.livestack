using Accord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace NINA.Plugin.Livestack.Image {
    public partial class ImageTransformer2 {
        private const int MinimumAlignmentInliers = 8;
        private const double MinimumMatchFraction = 0.20;
        private const double FinalInlierRadius = 2.0;
        private const double MaximumAlignmentRms = 1.0;

        /// <summary>
        /// Proposes matches, prefers a similarity fit and validates every final model against
        /// independent stars across the frame. A rejected result must never be accumulated.
        /// </summary>
        public AlignmentResult ComputeAlignment(List<Point> stars, List<Point> referenceStars, int width, int height, CancellationToken token = default) {
            token.ThrowIfCancellationRequested();
            if (width <= 0 || height <= 0 || !IsFiniteCatalog(stars) || !IsFiniteCatalog(referenceStars)) {
                return AlignmentResult.Rejected("Invalid frame dimensions or star coordinates.");
            }
            List<Point> reference = referenceStars.Distinct().ToList();
            List<Point> current = stars.Distinct().ToList();
            if (Math.Min(reference.Count, current.Count) < MinimumAlignmentInliers) {
                return AlignmentResult.Rejected($"At least {MinimumAlignmentInliers} distinct alignment stars are required on both frames. Reference={reference.Count}; Current={current.Count}.");
            }
            if (!HasSpatialCoverage(reference, width, height) || !HasSpatialCoverage(current, width, height)) {
                return AlignmentResult.Rejected("Alignment stars do not cover enough of the frame in two dimensions.");
            }
            if (ReferenceEquals(stars, referenceStars)) {
                return AlignmentResult.Accepted(new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }, reference.Count, 1, 0, 1);
            }

            AlignmentResult result = TryCandidate(() => ComputeTriangleAffineTransformation(
                LimitPointSetForTriangleFallback(current, preserveOrder: true),
                LimitPointSetForTriangleFallback(reference, preserveOrder: true), token), reference, current, width, height, token);
            if (result.Success) {
                return result;
            }

            result = TryCandidate(() => TryComputeQuadAffineTransformation(reference, current, out double[,] matrix, token) ? matrix : null,
                reference, current, width, height, token);
            if (result.Success) {
                return result;
            }

            if (Math.Max(reference.Count, current.Count) > MaxTriangleFallbackStars) {
                result = TryCandidate(() => ComputeTriangleAffineTransformation(
                    LimitPointSetForTriangleFallback(current, preserveOrder: false),
                    LimitPointSetForTriangleFallback(reference, preserveOrder: false), token), reference, current, width, height, token);
            }
            return result;
        }

        private AlignmentResult TryCandidate(Func<double[,]> propose, List<Point> reference, List<Point> current, int width, int height, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            try {
                double[,] candidate = propose();
                token.ThrowIfCancellationRequested();
                return RefineAndValidate(candidate, reference, current, width, height);
            } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ArithmeticException) {
                return AlignmentResult.Rejected(ex.Message);
            }
        }

        private AlignmentResult RefineAndValidate(double[,] candidate, List<Point> reference, List<Point> current, int width, int height) {
            if (!AlignmentGeometry.IsPlausible(candidate)) {
                return AlignmentResult.Rejected("No candidate with plausible scale, axis ratio and orientation was found.");
            }
            List<(Point Ref, Point Src, int Votes)> pairs = CollectProjectedInliers(candidate, reference, current, 4.0);
            int minimum = GetMinimumMatchCount(reference.Count, current.Count);
            if (pairs.Count < minimum) {
                return AlignmentResult.Rejected($"Insufficient unique matches: {pairs.Count}; Required={minimum}.");
            }

            double[,] similarity = FitSimilarity(pairs);
            AlignmentResult similarityResult = ValidateFinalModel(similarity, reference, current, width, height, false);

            // A small affine correction is justified only by broad support and a substantial
            // residual improvement. Sparse evidence can never enable additional distortion.
            double[,] affine = RefineAffineLeastSquares(pairs, Enumerable.Range(0, pairs.Count).ToList());
            AlignmentResult affineResult = ValidateFinalModel(affine, reference, current, width, height, true);
            if (affineResult.Success && (!similarityResult.Success
                || (affineResult.InlierCount >= similarityResult.InlierCount
                    && affineResult.RmsErrorPixels + 0.2 < similarityResult.RmsErrorPixels
                    && affineResult.RmsErrorPixels < similarityResult.RmsErrorPixels * 0.75))) {
                return affineResult;
            }
            return similarityResult;
        }

        private AlignmentResult ValidateFinalModel(double[,] model, List<Point> reference, List<Point> current, int width, int height, bool affine) {
            // Reassociate after fitting, then refine once on the tighter support. Always
            // measure the final matrix again; an earlier candidate's confidence is not reusable.
            if (!AlignmentGeometry.IsPlausible(model)) {
                return AlignmentResult.Rejected("Refined model exceeds the allowed scale or distortion.");
            }
            List<(Point Ref, Point Src, int Votes)> pairs = CollectProjectedInliers(model, reference, current, FinalInlierRadius);
            int minimum = GetMinimumMatchCount(reference.Count, current.Count);
            if (pairs.Count < minimum) {
                return AlignmentResult.Rejected($"Insufficient final matches: {pairs.Count}; Required={minimum}.");
            }
            model = affine ? RefineAffineLeastSquares(pairs, Enumerable.Range(0, pairs.Count).ToList()) : FitSimilarity(pairs);
            if (!AlignmentGeometry.IsPlausible(model)) {
                return AlignmentResult.Rejected("Final refinement exceeds the allowed scale or distortion.");
            }
            pairs = CollectProjectedInliers(model, reference, current, FinalInlierRadius);
            if (pairs.Count < minimum || !HasSpatialCoverage(pairs.Select(p => p.Ref).ToList(), width, height)
                || !HasSpatialCoverage(pairs.Select(p => p.Src).ToList(), width, height)) {
                return AlignmentResult.Rejected("Final matches are insufficient or concentrated in too small a region.");
            }
            double overlap = AlignmentGeometry.GetOverlapFraction(model, width, height);
            double sumSquaredError = 0;
            foreach ((Point Ref, Point Src, int Votes) pair in pairs) {
                double dx = model[0, 0] * pair.Ref.X + model[0, 1] * pair.Ref.Y + model[0, 2] - pair.Src.X;
                double dy = model[1, 0] * pair.Ref.X + model[1, 1] * pair.Ref.Y + model[1, 2] - pair.Src.Y;
                sumSquaredError += dx * dx + dy * dy;
            }
            double rms = Math.Sqrt(sumSquaredError / pairs.Count);
            if (!double.IsFinite(rms) || rms > MaximumAlignmentRms || overlap < AlignmentGeometry.MinimumOverlap) {
                return AlignmentResult.Rejected(FormattableString.Invariant($"Poor final fit or insufficient overlap: RMS={rms:F3}px; Overlap={overlap:P1}."));
            }
            return AlignmentResult.Accepted(model, pairs.Count, pairs.Count / (double)Math.Min(reference.Count, current.Count), rms, overlap, affine);
        }

        private static int GetMinimumMatchCount(int referenceCount, int currentCount) {
            return Math.Max(MinimumAlignmentInliers, (int)Math.Ceiling(Math.Min(referenceCount, currentCount) * MinimumMatchFraction));
        }

        private static bool IsFiniteCatalog(List<Point> stars) {
            return stars != null && stars.All(point => float.IsFinite(point.X) && float.IsFinite(point.Y));
        }

        private static bool HasSpatialCoverage(List<Point> stars, int width, int height) {
            if (stars.Count < MinimumAlignmentInliers) {
                return false;
            }
            double meanX = stars.Average(point => (double)point.X) / width;
            double meanY = stars.Average(point => (double)point.Y) / height;
            double xx = 0, yy = 0, xy = 0;
            foreach (Point point in stars) {
                double dx = point.X / (double)width - meanX;
                double dy = point.Y / (double)height - meanY;
                xx += dx * dx;
                yy += dy * dy;
                xy += dx * dy;
            }
            double minorVariance = (xx + yy - Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy)) / (2 * stars.Count);
            // At least five percent of a frame in RMS spread along the weaker spatial axis.
            return minorVariance >= 0.0025;
        }

        private static double[,] FitSimilarity(List<(Point Ref, Point Src, int Votes)> pairs) {
            double referenceX = pairs.Average(pair => (double)pair.Ref.X), referenceY = pairs.Average(pair => (double)pair.Ref.Y);
            double sourceX = pairs.Average(pair => (double)pair.Src.X), sourceY = pairs.Average(pair => (double)pair.Src.Y);
            double denominator = 0, real = 0, imaginary = 0;
            foreach ((Point Ref, Point Src, int Votes) pair in pairs) {
                double x = pair.Ref.X - referenceX, y = pair.Ref.Y - referenceY;
                double u = pair.Src.X - sourceX, v = pair.Src.Y - sourceY;
                denominator += x * x + y * y;
                real += x * u + y * v;
                imaginary += x * v - y * u;
            }
            if (denominator <= 1e-6) {
                throw new InvalidOperationException("Insufficient separation for a stable similarity fit.");
            }
            double a = real / denominator, c = imaginary / denominator;
            return new double[,] {
                { a, -c, sourceX - a * referenceX + c * referenceY },
                { c, a, sourceY - c * referenceX - a * referenceY },
                { 0, 0, 1 }
            };
        }
    }
}
