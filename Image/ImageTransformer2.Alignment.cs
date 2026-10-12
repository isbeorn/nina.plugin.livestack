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
        private const int SparseModelAgreementInliers = 72;

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

            return SearchAlignment(reference, current, new AlignmentSearch(reference, current, false), width, height, null, token);
        }

        public AlignmentResult ComputeAlignment(AlignmentStarCatalog stars, AlignmentStarCatalog referenceStars, int width, int height, double[,] predictedMatrix = null, CancellationToken token = default) {
            token.ThrowIfCancellationRequested();
            if (stars == null || referenceStars == null || width <= 0 || height <= 0) {
                return AlignmentResult.Rejected("Invalid frame dimensions or star catalogs.");
            }
            List<Point> reference = referenceStars.MatchStars.ToList();
            List<Point> current = stars.MatchStars.ToList();
            List<Point> support = stars.SupportStars.ToList();
            if (!IsFiniteCatalog(reference) || !IsFiniteCatalog(current) || !IsFiniteCatalog(support)) {
                return AlignmentResult.Rejected("Invalid star coordinates.");
            }
            if (Math.Min(reference.Count, current.Count) < MinimumAlignmentInliers) {
                return AlignmentResult.Rejected($"At least {MinimumAlignmentInliers} distinct alignment stars are required on both frames. Reference={reference.Count}; Current={current.Count}.");
            }
            if (!HasSpatialCoverage(reference, width, height) || !HasSpatialCoverage(support, width, height)) {
                return AlignmentResult.Rejected("Alignment stars do not cover enough of the frame in two dimensions.");
            }
            AlignmentSearch search = new(reference, support, true, current.Count);
            if (ReferenceEquals(stars, referenceStars)) {
                AlignmentResult first = RefineAndValidate(new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }, search, width, height);
                LogSearch("reference", reference, current, search, first);
                return first;
            }
            return SearchAlignment(reference, current, search, width, height, predictedMatrix ?? new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }, token);
        }

        private AlignmentResult SearchAlignment(List<Point> reference, List<Point> current, AlignmentSearch search, int width, int height, double[,] prediction, CancellationToken token) {
            AlignmentResult result;
            if (prediction != null) {
                result = TryCandidate(() => prediction, search, width, height, token);
                LogSearch("prediction", reference, current, search, result);
                if (result.Success) {
                    return result;
                }
            }
            result = TryCandidate(() => ComputeTriangleAffineTransformation(
                LimitPointSetForTriangleFallback(current, preserveOrder: true),
                LimitPointSetForTriangleFallback(reference, preserveOrder: true), token), search, width, height, token);
            LogSearch("ordered triangles", reference, current, search, result);
            if (result.Success) {
                return result;
            }
            result = TryCandidate(() => TryComputeQuadAffineTransformation(reference, current, out double[,] matrix, token, search.FixedReference ? search : null) ? matrix : null,
                search, width, height, token);
            LogSearch("quads", reference, current, search, result);
            if (result.Success) {
                return result;
            }
            if (Math.Max(reference.Count, current.Count) > MaxTriangleFallbackStars) {
                result = TryCandidate(() => ComputeTriangleAffineTransformation(
                    LimitPointSetForTriangleFallback(current, preserveOrder: false),
                    LimitPointSetForTriangleFallback(reference, preserveOrder: false), token), search, width, height, token);
                LogSearch("spatial triangles", reference, current, search, result);
            }
            return result;
        }

        private static void LogSearch(string path, List<Point> reference, List<Point> current, AlignmentSearch search, AlignmentResult result) {
            NINA.Core.Utility.Logger.Debug($"Live Stack alignment path={path}; Reference match stars={reference.Count}; Current match stars={current.Count}; Current support stars={search.Current.Count}; Required matches={search.Minimum}; {result}");
        }

        private AlignmentResult TryCandidate(Func<double[,]> propose, AlignmentSearch search, int width, int height, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            try {
                double[,] candidate = propose();
                token.ThrowIfCancellationRequested();
                return RefineAndValidate(candidate, search, width, height);
            } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ArithmeticException) {
                return AlignmentResult.Rejected(ex.Message);
            }
        }

        private AlignmentResult RefineAndValidate(double[,] candidate, AlignmentSearch search, int width, int height) {
            if (!AlignmentGeometry.IsPlausible(candidate)) {
                return AlignmentResult.Rejected("No candidate with plausible scale, axis ratio and orientation was found.");
            }
            List<(Point Ref, Point Src, int Votes)> pairs = search.Collect(candidate, 4.0);
            if (pairs.Count < search.Minimum) {
                return AlignmentResult.Rejected($"Insufficient unique matches: {pairs.Count}; Required={search.Minimum}.");
            }
            double[,] similarity = FitSimilarity(pairs);
            AlignmentResult similarityResult = ValidateFinalModel(similarity, search, width, height, false);
            double[,] affine = RefineAffineLeastSquares(pairs, Enumerable.Range(0, pairs.Count).ToList());
            AlignmentResult affineResult = ValidateFinalModel(affine, search, width, height, true);
            if (affineResult.Success && (!similarityResult.Success
                || (affineResult.InlierCount >= similarityResult.InlierCount
                    && affineResult.RmsErrorPixels + 0.2 < similarityResult.RmsErrorPixels
                    && affineResult.RmsErrorPixels < similarityResult.RmsErrorPixels * 0.75))) {
                return affineResult;
            }
            int agreementInliers = Math.Max(SparseModelAgreementInliers, (int)Math.Ceiling(search.Reference.Count * MinimumMatchFraction));
            if (search.FixedReference && similarityResult.Success && affineResult.Success
                && similarityResult.InlierCount < agreementInliers) {
                ReadOnlySpan<(int X, int Y)> corners = [(0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1)];
                foreach ((int x, int y) in corners) {
                    double dx = (similarityResult.Matrix[0, 0] - affineResult.Matrix[0, 0]) * x
                        + (similarityResult.Matrix[0, 1] - affineResult.Matrix[0, 1]) * y
                        + similarityResult.Matrix[0, 2] - affineResult.Matrix[0, 2];
                    double dy = (similarityResult.Matrix[1, 0] - affineResult.Matrix[1, 0]) * x
                        + (similarityResult.Matrix[1, 1] - affineResult.Matrix[1, 1]) * y
                        + similarityResult.Matrix[1, 2] - affineResult.Matrix[1, 2];
                    if (dx * dx + dy * dy > MaximumAlignmentRms * MaximumAlignmentRms) {
                        return AlignmentResult.Rejected("Sparse similarity and affine fits disagree by more than one pixel at a frame corner.");
                    }
                }
            }
            return similarityResult;
        }

        private AlignmentResult ValidateFinalModel(double[,] model, AlignmentSearch search, int width, int height, bool affine) {
            if (!AlignmentGeometry.IsPlausible(model)) {
                return AlignmentResult.Rejected("Refined model exceeds the allowed scale or distortion.");
            }
            List<(Point Ref, Point Src, int Votes)> pairs = search.Collect(model, FinalInlierRadius);
            if (pairs.Count < search.Minimum) {
                return AlignmentResult.Rejected($"Insufficient final matches: {pairs.Count}; Required={search.Minimum}.");
            }
            model = affine ? RefineAffineLeastSquares(pairs, Enumerable.Range(0, pairs.Count).ToList()) : FitSimilarity(pairs);
            if (!AlignmentGeometry.IsPlausible(model)) {
                return AlignmentResult.Rejected("Final refinement exceeds the allowed scale or distortion.");
            }
            pairs = search.Collect(model, FinalInlierRadius);
            if (pairs.Count < search.Minimum || !HasSpatialCoverage(pairs.Select(p => p.Ref).ToList(), width, height)
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
            if (search.FixedReference && !search.HasIndependentSupport(model, pairs, width, height)) {
                return AlignmentResult.Rejected("Final matches are not sufficiently distinct from local chance associations.");
            }
            return AlignmentResult.Accepted(model, pairs.Count, pairs.Count / (double)search.Denominator, rms, overlap, affine);
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
