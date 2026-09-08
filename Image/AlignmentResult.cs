using System;
using System.Globalization;

namespace NINA.Plugin.Livestack.Image {
    /// <summary>An alignment decision made before any stack pixels are changed.</summary>
    public sealed class AlignmentResult {
        private readonly double[,] matrix;

        private AlignmentResult(double[,] matrix, string rejectionReason, int inlierCount, double matchFraction, double rmsErrorPixels, double overlapFraction, bool usesAffineModel) {
            this.matrix = matrix;
            RejectionReason = rejectionReason;
            InlierCount = inlierCount;
            MatchFraction = matchFraction;
            RmsErrorPixels = rmsErrorPixels;
            OverlapFraction = overlapFraction;
            UsesAffineModel = usesAffineModel;
        }

        public bool Success => matrix != null;
        public string RejectionReason { get; }
        public int InlierCount { get; }
        public double MatchFraction { get; }
        public double RmsErrorPixels { get; }
        public double OverlapFraction { get; }
        public bool UsesAffineModel { get; }
        public double[,] Matrix => matrix == null ? null : (double[,])matrix.Clone();

        internal static AlignmentResult Rejected(string reason) {
            return new AlignmentResult(null, reason, 0, 0, double.NaN, 0, false);
        }

        internal static AlignmentResult Accepted(double[,] matrix, int inlierCount, double matchFraction, double rmsErrorPixels, double overlapFraction, bool usesAffineModel = false) {
            return new AlignmentResult((double[,])matrix.Clone(), null, inlierCount, matchFraction, rmsErrorPixels, overlapFraction, usesAffineModel);
        }

        public override string ToString() {
            return Success
                ? string.Create(CultureInfo.InvariantCulture, $"Model={(UsesAffineModel ? "affine" : "similarity")}; Matches={InlierCount}; Match fraction={MatchFraction:P1}; RMS={RmsErrorPixels:F3}px; Overlap={OverlapFraction:P1}")
                : $"Rejected: {RejectionReason}";
        }
    }
}
