using Accord;
using MathNet.Numerics.LinearAlgebra.Double;
using MathNet.Numerics.Statistics;
using NINA.Core.Utility;
using NINA.Image.ImageAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugin.Livestack.Image {

    public partial class ImageTransformer2 : IImageTransformer {
        private const int MaxStars = 1000;
        private const int MaxTriangleFallbackStars = 72;
        private const int MaxTriangleCandidates = 3;
        private const int QuadNeighborCount = 7;
        private const int MaxReferenceQuads = 16000;
        private const int MaxTargetQuads = 10000;
        private const int MaxQuadCandidates = 8;
        private const int MaxQuadMatchPairs = 160;
        private const int MaxStableSelectionCandidates = 2000;
        private const int StableSelectionGridSize = 32;
        private const int MinimumTriangleSetForParallelVoting = 1024;
        private const double SaturationThreshold = 65000d;
        private const double MaxTriangleScaleDelta = 0.12d;
        private const double MinTriangleLongestSide = 12d;
        private const double MinQuadLongestSide = 12d;
        private const double MinQuadShortestSideRatio = 0.05d;
        private const double QuadHashBinSize = 0.025d;
        private const double MaxQuadDescriptorDistanceSquared = 0.006d;

        private static readonly Lazy<ImageTransformer2> lazy = new Lazy<ImageTransformer2>(() => new ImageTransformer2());

        private readonly object quadReferenceCacheSyncRoot = new object();
        private QuadReferenceCache quadReferenceCache;

        public static ImageTransformer2 Instance => lazy.Value;

        private ImageTransformer2() {
        }

        /// <summary>
        /// Selects a bounded, high-quality star catalog for alignment from the raw detector output.
        /// </summary>
        /// <remarks>
        /// The first portion preserves the grid-based bright-star ordering when those stars pass the
        /// stricter quality checks. The remaining slots are filled with a farthest-point sample so
        /// the catalog remains spatially stable when star brightness ranks change between frames.
        /// </remarks>
        /// <param name="starList">Stars detected in the current frame.</param>
        /// <param name="width">Frame width in pixels.</param>
        /// <param name="height">Frame height in pixels.</param>
        /// <returns>A stable list of star centroids to use for reference and target matching.</returns>
        public List<Accord.Point> GetStars(List<DetectedStar> starList, int width, int height) {
            if (starList == null || starList.Count == 0) {
                return new List<Point>();
            }

            int rawStarCount = starList.Count;
            var candidateStars = BuildScoredStars(starList, width, height, strictFiltering: true, out StarFilterDiagnostics strictDiagnostics);
            int strictCandidateCount = candidateStars.Count;
            int looseCandidateCount = -1;
            StarFilterDiagnostics looseDiagnostics = default;
            if (candidateStars.Count < MinimumAlignmentInliers) {
                candidateStars = BuildScoredStars(starList, width, height, strictFiltering: false, out looseDiagnostics);
                looseCandidateCount = candidateStars.Count;
            }

            if (candidateStars.Count == 0) {
                Logger.Warning($"Live Stack star filtering produced no alignment candidates. Raw detector stars={rawStarCount}; Strict candidates={strictCandidateCount}; Loose candidates={FormatCandidateCount(looseCandidateCount)}; Strict rejects=[{strictDiagnostics}]; Loose rejects=[{FormatDiagnostics(looseCandidateCount, looseDiagnostics)}]; Required={MinimumAlignmentInliers}; Frame size={width}x{height}");
                return new List<Point>();
            }

            int targetStarCount = Math.Min(MaxStars, candidateStars.Count);
            HashSet<(float X, float Y)> candidatePositions = candidateStars
                .Select(candidate => (candidate.Position.X, candidate.Position.Y))
                .ToHashSet();

            List<Point> selectedStars = SelectBrightStarSeeds(starList, width, height)
                .Where(point => IsPointWellInsideFrame(point, width, height))
                .Where(point => candidatePositions.Contains((point.X, point.Y)))
                .Take(targetStarCount)
                .ToList();

            HashSet<(float X, float Y)> selectedPositions = selectedStars
                .Select(point => (point.X, point.Y))
                .ToHashSet();

            foreach (Point point in SelectGeometricallyStableStars(candidateStars, targetStarCount)) {
                if (!selectedPositions.Add((point.X, point.Y))) {
                    continue;
                }

                selectedStars.Add(point);
                if (selectedStars.Count >= targetStarCount) {
                    break;
                }
            }

            if (selectedStars.Count < MinimumAlignmentInliers) {
                Logger.Warning($"Live Stack star selection produced too few alignment stars. Raw detector stars={rawStarCount}; Strict candidates={strictCandidateCount}; Loose candidates={FormatCandidateCount(looseCandidateCount)}; Strict rejects=[{strictDiagnostics}]; Loose rejects=[{FormatDiagnostics(looseCandidateCount, looseDiagnostics)}]; Selected alignment stars={selectedStars.Count}; Required={MinimumAlignmentInliers}; Frame size={width}x{height}");
            }

            return selectedStars;
        }

        private List<Point> SelectBrightStarSeeds(List<DetectedStar> starList, int width, int height) {
            const int maxStars = 100;

            // Preserve the seed order used by the ordered triangle proposal.
            var filteredStars = starList
                .Where(x => x.MaxBrightness < 65000) // Exclude saturated stars
                .OrderByDescending(x => x.MaxBrightness) // Prioritize brighter stars
                .ToList();

            // Divide the image into a grid and select the brightest stars in each grid cell
            int gridSize = 5;
            double cellWidth = width / (double)gridSize;
            double cellHeight = height / (double)gridSize;
            int maxStarsPerCell = Math.Max(1, maxStars / (gridSize * gridSize));

            var selectedStars = new List<Accord.Point>();

            for (int i = 0; i < gridSize; i++) {
                for (int j = 0; j < gridSize; j++) {
                    // Get the stars in the current grid cell
                    var starsInCell = filteredStars
                        .Where(s => s.Position.X >= i * cellWidth && s.Position.X < (i + 1) * cellWidth
                                 && s.Position.Y >= j * cellHeight && s.Position.Y < (j + 1) * cellHeight)
                        .OrderByDescending(s => s.MaxBrightness) // Pick the brightest stars in the cell
                        .Take(maxStarsPerCell)
                        .ToList();

                    selectedStars.AddRange(starsInCell.Select(s => s.Position));
                }
            }

            // If we still need more stars, fall back to the closest ones to the center
            if (selectedStars.Count < maxStars) {
                var centerX = width / 2.0;
                var centerY = height / 2.0;

                selectedStars.AddRange(
                    filteredStars
                        .Where(s => !selectedStars.Contains(s.Position))
                        .OrderBy(s => GetDistanceToCenter(s.Position, centerX, centerY))
                        .Take(maxStars - selectedStars.Count)
                        .Select(s => s.Position)
                );
            }

            return selectedStars.Take(maxStars).ToList();
        }

        private static string FormatCandidateCount(int count) {
            return count >= 0 ? count.ToString() : "not used";
        }

        private static string FormatDiagnostics(int candidateCount, StarFilterDiagnostics diagnostics) {
            return candidateCount >= 0 ? diagnostics.ToString() : "not used";
        }

        /// <summary>
        /// Chooses stars that cover the frame instead of only the brightest or most central stars.
        /// </summary>
        /// <remarks>
        /// This is a deterministic farthest-point sampler. It starts from an approximate frame-scale
        /// baseline and repeatedly adds the candidate farthest from the current selected set. That
        /// makes the chosen catalog less sensitive to small brightness, HFR, or detection-order changes.
        /// </remarks>
        /// <param name="candidateStars">Quality-scored candidates sorted by reviewable detector score.</param>
        /// <param name="targetStarCount">Maximum number of stars to return.</param>
        /// <returns>A spatially distributed subset of the input candidates.</returns>
        private List<Point> SelectGeometricallyStableStars(List<ScoredStar> candidateStars, int targetStarCount) {
            if (candidateStars.Count <= targetStarCount) {
                return candidateStars.Select(x => x.Position).ToList();
            }

            if (targetStarCount == MaxStars && candidateStars.Count > MaxStableSelectionCandidates) {
                candidateStars = LimitStableSelectionCandidates(candidateStars, MaxStableSelectionCandidates);
            }

            var selectedStars = new List<Point>(targetStarCount);
            var selected = new bool[candidateStars.Count];
            var nearestSelectedDistanceSquared = new double[candidateStars.Count];
            Array.Fill(nearestSelectedDistanceSquared, double.PositiveInfinity);

            var seedPair = FindMostSeparatedPair(candidateStars);
            AddStableSelection(seedPair.First, candidateStars, selected, nearestSelectedDistanceSquared, selectedStars);
            if (selectedStars.Count < targetStarCount) {
                AddStableSelection(seedPair.Second, candidateStars, selected, nearestSelectedDistanceSquared, selectedStars);
            }

            while (selectedStars.Count < targetStarCount) {
                int bestIndex = -1;
                double bestDistanceSquared = double.NegativeInfinity;

                for (int i = 0; i < candidateStars.Count; i++) {
                    if (selected[i]) {
                        continue;
                    }

                    double distanceSquared = nearestSelectedDistanceSquared[i];
                    if (bestIndex == -1
                        || distanceSquared > bestDistanceSquared
                        || (Math.Abs(distanceSquared - bestDistanceSquared) <= 1e-9d && IsBetterTieBreak(candidateStars[i], candidateStars[bestIndex]))) {
                        bestIndex = i;
                        bestDistanceSquared = distanceSquared;
                    }
                }

                if (bestIndex == -1) {
                    break;
                }

                AddStableSelection(bestIndex, candidateStars, selected, nearestSelectedDistanceSquared, selectedStars);
            }

            return selectedStars;
        }

        /// <summary>
        /// Reduces very dense catalogs before farthest-point sampling while keeping local high-quality stars.
        /// </summary>
        /// <remarks>
        /// Farthest-point sampling is intentionally deterministic but scales with candidate count. Dense
        /// medium-format frames can contain many thousands of valid detections, so this prefilter keeps
        /// the best stars per spatial cell and then fills any remaining budget by score. The downstream
        /// sampler still performs the final coverage selection from this bounded, spatially broad set.
        /// </remarks>
        /// <param name="candidateStars">Quality-sorted candidate stars.</param>
        /// <param name="maxCandidateCount">Maximum candidates to pass to the stable sampler.</param>
        /// <returns>A deterministic, spatially distributed candidate subset.</returns>
        private static List<ScoredStar> LimitStableSelectionCandidates(List<ScoredStar> candidateStars, int maxCandidateCount) {
            if (candidateStars.Count <= maxCandidateCount) {
                return candidateStars;
            }

            float minX = candidateStars[0].Position.X;
            float maxX = minX;
            float minY = candidateStars[0].Position.Y;
            float maxY = minY;

            for (int i = 1; i < candidateStars.Count; i++) {
                Point position = candidateStars[i].Position;
                if (position.X < minX) {
                    minX = position.X;
                } else if (position.X > maxX) {
                    maxX = position.X;
                }

                if (position.Y < minY) {
                    minY = position.Y;
                } else if (position.Y > maxY) {
                    maxY = position.Y;
                }
            }

            int cellCount = StableSelectionGridSize * StableSelectionGridSize;
            int maxPerCell = Math.Max(1, (int)Math.Ceiling(maxCandidateCount / (double)cellCount));
            int[] selectedPerCell = new int[cellCount];
            bool[] selected = new bool[candidateStars.Count];
            var limited = new List<ScoredStar>(maxCandidateCount);
            double xScale = StableSelectionGridSize / Math.Max(1d, maxX - minX);
            double yScale = StableSelectionGridSize / Math.Max(1d, maxY - minY);

            for (int i = 0; i < candidateStars.Count && limited.Count < maxCandidateCount; i++) {
                int cellIndex = GetStableSelectionCellIndex(candidateStars[i].Position, minX, minY, xScale, yScale);
                if (selectedPerCell[cellIndex] >= maxPerCell) {
                    continue;
                }

                selectedPerCell[cellIndex]++;
                selected[i] = true;
                limited.Add(candidateStars[i]);
            }

            for (int i = 0; i < candidateStars.Count && limited.Count < maxCandidateCount; i++) {
                if (selected[i]) {
                    continue;
                }

                selected[i] = true;
                limited.Add(candidateStars[i]);
            }

            return limited;
        }

        private static int GetStableSelectionCellIndex(Point position, float minX, float minY, double xScale, double yScale) {
            int cellX = Math.Clamp((int)((position.X - minX) * xScale), 0, StableSelectionGridSize - 1);
            int cellY = Math.Clamp((int)((position.Y - minY) * yScale), 0, StableSelectionGridSize - 1);
            return (cellY * StableSelectionGridSize) + cellX;
        }

        /// <summary>
        /// Finds an approximate diameter pair to seed the stable star sampler.
        /// </summary>
        /// <param name="candidateStars">Candidate stars available for selection.</param>
        /// <returns>Indices for two stars that are far apart in the frame.</returns>
        private static (int First, int Second) FindMostSeparatedPair(List<ScoredStar> candidateStars) {
            if (candidateStars.Count < 2) {
                return (0, 0);
            }

            int first = FindFarthestStarIndex(candidateStars, 0);
            int second = FindFarthestStarIndex(candidateStars, first);
            if (first == second) {
                second = first == 0 ? 1 : 0;
            }

            return (first, second);
        }

        /// <summary>
        /// Finds the candidate farthest from a given anchor star.
        /// </summary>
        /// <param name="candidateStars">Candidate stars available for selection.</param>
        /// <param name="anchorIndex">Index of the anchor star.</param>
        /// <returns>The index of the farthest candidate from the anchor.</returns>
        private static int FindFarthestStarIndex(List<ScoredStar> candidateStars, int anchorIndex) {
            int farthestIndex = anchorIndex == 0 && candidateStars.Count > 1 ? 1 : 0;
            double farthestDistanceSquared = DistanceSquared(candidateStars[anchorIndex].Position, candidateStars[farthestIndex].Position);

            for (int i = 0; i < candidateStars.Count; i++) {
                if (i == anchorIndex) {
                    continue;
                }

                double distanceSquared = DistanceSquared(candidateStars[anchorIndex].Position, candidateStars[i].Position);
                if (distanceSquared > farthestDistanceSquared
                    || (Math.Abs(distanceSquared - farthestDistanceSquared) <= 1e-9d && IsBetterTieBreak(candidateStars[i], candidateStars[farthestIndex]))) {
                    farthestIndex = i;
                    farthestDistanceSquared = distanceSquared;
                }
            }

            return farthestIndex;
        }

        /// <summary>
        /// Adds one star to the farthest-point sample and updates each candidate's nearest selected distance.
        /// </summary>
        /// <param name="selectedIndex">Index of the star to add.</param>
        /// <param name="candidateStars">All candidate stars.</param>
        /// <param name="selected">Flags indicating candidates already selected.</param>
        /// <param name="nearestSelectedDistanceSquared">Nearest selected-star distance per candidate.</param>
        /// <param name="selectedStars">Output list being built.</param>
        private static void AddStableSelection(
                int selectedIndex,
                List<ScoredStar> candidateStars,
                bool[] selected,
                double[] nearestSelectedDistanceSquared,
                List<Point> selectedStars) {
            selected[selectedIndex] = true;
            Point anchor = candidateStars[selectedIndex].Position;
            selectedStars.Add(anchor);

            int i = 0;
            for (; i <= candidateStars.Count - 4; i += 4) {
                Point p0 = candidateStars[i].Position, p1 = candidateStars[i + 1].Position;
                Point p2 = candidateStars[i + 2].Position, p3 = candidateStars[i + 3].Position;
                // DistanceSquared subtracts float coordinates before widening. Preserve that rounding.
                Vector128<float> dx = Vector128.Create(anchor.X) - Vector128.Create(p0.X, p1.X, p2.X, p3.X);
                Vector128<float> dy = Vector128.Create(anchor.Y) - Vector128.Create(p0.Y, p1.Y, p2.Y, p3.Y);
                Vector256<double> x = Vector256.WidenLower(dx.ToVector256()), y = Vector256.WidenLower(dy.ToVector256());
                Vector256<double> distance = x * x + y * y;
                Vector256<double> previous = Vector256.Create<double>(nearestSelectedDistanceSquared.AsSpan(i));
                // Selected entries are never read again, so updating their distance needs no mask.
                Vector256.Min(previous, distance).CopyTo(nearestSelectedDistanceSquared.AsSpan(i));
            }
            for (; i < candidateStars.Count; i++) {
                if (selected[i]) {
                    continue;
                }

                double distanceSquared = DistanceSquared(candidateStars[selectedIndex].Position, candidateStars[i].Position);
                if (distanceSquared < nearestSelectedDistanceSquared[i]) {
                    nearestSelectedDistanceSquared[i] = distanceSquared;
                }
            }
        }

        private static bool IsBetterTieBreak(ScoredStar candidate, ScoredStar bestCandidate) {
            return candidate.SourceIndex < bestCandidate.SourceIndex;
        }

        private static double DistanceSquared(Point first, Point second) {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return (dx * dx) + (dy * dy);
        }

        private bool IsWellInsideFrame(System.Drawing.Rectangle bb, int width, int height) {
            const int margin = 4;
            if (bb.Width <= 0 || bb.Height <= 0) {
                return false;
            }
            return bb.Left >= margin && bb.Top >= margin && bb.Right <= (width - margin) && bb.Bottom <= (height - margin);
        }

        private double GetDistanceToCenter(Accord.Point position, double centerX, double centerY) {
            return Math.Sqrt(Math.Pow(position.X - centerX, 2) + Math.Pow(position.Y - centerY, 2));
        }

        /// <summary>
        /// Converts detected stars into alignment candidates with quality filtering and a deterministic score.
        /// </summary>
        /// <remarks>
        /// Strict mode rejects near-edge, saturated, and HFR-outlier detections. Loose mode
        /// keeps the same finite/inside-frame requirements and can fall back to brightness-independent
        /// scoring when detector brightness metadata would otherwise reject every alignment candidate.
        /// </remarks>
        /// <param name="starList">Raw detector results.</param>
        /// <param name="width">Frame width in pixels.</param>
        /// <param name="height">Frame height in pixels.</param>
        /// <param name="strictFiltering">Whether to apply the full quality gate.</param>
        /// <returns>Quality-scored stars sorted from best to worst.</returns>
        private List<ScoredStar> BuildScoredStars(List<DetectedStar> starList, int width, int height, bool strictFiltering, out StarFilterDiagnostics diagnostics) {
            double centerX = width / 2d;
            double centerY = height / 2d;

            var validHfrValues = starList
                .Select(x => x.HFR)
                .Where(IsPositiveFinite)
                .ToArray();

            double medianHfr = validHfrValues.Length > 0 ? validHfrValues.Median() : double.NaN;
            double hfrMad = validHfrValues.Length > 0 ? ComputeMedianAbsoluteDeviation(validHfrValues, medianHfr) : 0d;
            double hfrTolerance = double.IsNaN(medianHfr)
                ? double.PositiveInfinity
                : Math.Max(0.75d, hfrMad > 0 ? 3d * hfrMad : medianHfr * 0.6d);

            diagnostics = new StarFilterDiagnostics();
            var scoredStars = new List<ScoredStar>(starList.Count);
            var brightnessFallbackStars = strictFiltering ? null : new List<ScoredStar>(starList.Count);
            for (int i = 0; i < starList.Count; i++) {
                var star = starList[i];
                if (!IsFinite(star.Position.X) || !IsFinite(star.Position.Y)) {
                    diagnostics.InvalidPosition++;
                    continue;
                }

                bool pointInsideFrame = IsPointWellInsideFrame(star.Position, width, height);
                bool insideFrame = pointInsideFrame
                    && (IsWellInsideFrame(star.BoundingBox, width, height) || !strictFiltering);
                if (!insideFrame) {
                    diagnostics.OutsideFrame++;
                    continue;
                }

                if (!IsFinite(star.MaxBrightness) || star.MaxBrightness <= 0) {
                    diagnostics.InvalidBrightness++;
                    if (!strictFiltering) {
                        double fallbackScore = ComputeBrightnessFallbackStarScore(star, medianHfr);
                        fallbackScore *= 1d / (1d + (GetDistanceToCenter(star.Position, centerX, centerY) / Math.Max(width, height)) * 0.1d);
                        brightnessFallbackStars.Add(new ScoredStar(star.Position, fallbackScore, i));
                    }
                    continue;
                }

                if (star.MaxBrightness >= SaturationThreshold) {
                    diagnostics.SaturatedBrightness++;
                    if (!strictFiltering) {
                        double fallbackScore = ComputeBrightnessFallbackStarScore(star, medianHfr);
                        fallbackScore *= 1d / (1d + (GetDistanceToCenter(star.Position, centerX, centerY) / Math.Max(width, height)) * 0.1d);
                        brightnessFallbackStars.Add(new ScoredStar(star.Position, fallbackScore, i));
                    }
                    continue;
                }

                if (strictFiltering && IsPositiveFinite(star.HFR) && !double.IsNaN(medianHfr) && Math.Abs(star.HFR - medianHfr) > hfrTolerance) {
                    diagnostics.HfrOutlier++;
                    continue;
                }

                double score = ComputeStarScore(star, medianHfr);
                score *= 1d / (1d + (GetDistanceToCenter(star.Position, centerX, centerY) / Math.Max(width, height)) * 0.1d);
                scoredStars.Add(new ScoredStar(star.Position, score, i));
            }

            if (!strictFiltering && scoredStars.Count < MinimumAlignmentInliers && brightnessFallbackStars?.Count > 0) {
                diagnostics.BrightnessFallbackCandidates = brightnessFallbackStars.Count;
                scoredStars.AddRange(brightnessFallbackStars);
            }

            scoredStars.Sort(static (left, right) => {
                int scoreComparison = right.Score.CompareTo(left.Score);
                return scoreComparison != 0 ? scoreComparison : left.SourceIndex.CompareTo(right.SourceIndex);
            });
            return scoredStars;
        }

        private bool IsPointWellInsideFrame(Point point, int width, int height) {
            const int margin = 4;
            return point.X >= margin && point.Y >= margin && point.X < (width - margin) && point.Y < (height - margin);
        }

        /// <summary>
        /// Scores a detector result by contrast while penalizing HFR outliers.
        /// </summary>
        /// <param name="star">Detected star to score.</param>
        /// <param name="medianHfr">Median HFR for the frame, or <see cref="double.NaN"/> when unavailable.</param>
        /// <returns>A higher-is-better quality score.</returns>
        private double ComputeStarScore(DetectedStar star, double medianHfr) {
            double contrast = IsFinite(star.Background)
                ? Math.Max(1d, star.MaxBrightness - star.Background)
                : Math.Max(1d, star.MaxBrightness);

            double brightnessScore = Math.Log10(contrast + 10d);
            double hfrPenalty = 1d;
            if (IsPositiveFinite(star.HFR) && !double.IsNaN(medianHfr) && medianHfr > 0d) {
                double ratio = star.HFR / medianHfr;
                if (ratio < 1d) {
                    ratio = 1d / Math.Max(ratio, 1e-3d);
                }

                hfrPenalty += Math.Max(0d, ratio - 1d) * 0.75d;
            }

            return brightnessScore / hfrPenalty;
        }

        private double ComputeBrightnessFallbackStarScore(DetectedStar star, double medianHfr) {
            double brightnessScore = IsPositiveFinite(star.AverageBrightness)
                ? Math.Log10(Math.Min(star.AverageBrightness, SaturationThreshold - 1d) + 10d)
                : 1d;

            double hfrPenalty = 1d;
            if (IsPositiveFinite(star.HFR) && !double.IsNaN(medianHfr) && medianHfr > 0d) {
                double ratio = star.HFR / medianHfr;
                if (ratio < 1d) {
                    ratio = 1d / Math.Max(ratio, 1e-3d);
                }

                hfrPenalty += Math.Max(0d, ratio - 1d) * 0.75d;
            }

            return brightnessScore / hfrPenalty;
        }

        private static bool IsFinite(double value) {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsPositiveFinite(double value) {
            return IsFinite(value) && value > 0d;
        }

        /// <summary>
        /// Computes the median absolute deviation used for robust HFR outlier detection.
        /// </summary>
        /// <param name="values">Finite sample values.</param>
        /// <param name="median">Median of the sample values.</param>
        /// <returns>The sample median absolute deviation.</returns>
        private static double ComputeMedianAbsoluteDeviation(double[] values, double median) {
            if (values.Length == 0) {
                return 0d;
            }

            var absoluteDeviation = new double[values.Length];
            for (int i = 0; i < values.Length; i++) {
                absoluteDeviation[i] = Math.Abs(values[i] - median);
            }

            return absoluteDeviation.Median();
        }

        /// <summary>Compatibility API. New callers should retain the acceptance diagnostics from ComputeAlignment.</summary>
        public double[,] ComputeAffineTransformation(List<Point> stars, List<Point> referenceStars) {
            List<Point> positions = (stars ?? new List<Point>()).Concat(referenceStars ?? new List<Point>()).ToList();
            int width = InferFrameExtent(positions.Select(point => point.X));
            int height = InferFrameExtent(positions.Select(point => point.Y));
            AlignmentResult result = ComputeAlignment(stars, referenceStars, width, height);
            if (!result.Success) {
                throw new InvalidOperationException(result.RejectionReason);
            }
            return result.Matrix;
        }

        private static int InferFrameExtent(IEnumerable<float> coordinates) {
            double maximum = coordinates.Where(float.IsFinite).Select(value => (double)value).DefaultIfEmpty(0).Max();
            return (int)Math.Clamp(Math.Ceiling(maximum) + 1, 1, int.MaxValue);
        }

        /// <summary>
        /// Computes an affine transform from triangle-similarity votes and RANSAC refinement.
        /// </summary>
        /// <param name="stars">Target-frame stars.</param>
        /// <param name="referenceStars">Reference-frame stars.</param>
        /// <returns>A refined affine transform from reference to target coordinates.</returns>
        private double[,] ComputeTriangleAffineTransformation(List<Point> stars, List<Point> referenceStars, CancellationToken token = default) {
            var referenceTriangles = ComputeTriangleList(referenceStars);
            var targetTriangles = ComputeTriangleList(stars);

            var votingMatrix = ComputeVotingMatrix(
                referenceStars.Count,
                stars.Count,
                referenceTriangles,
                targetTriangles,
                maxTriangleDistance: 0.02);

            var matches = ComputeMatchList(votingMatrix, referenceStars.Count, stars.Count, voteThreshold: 150);

            var pairs = matches
                .Select(m => (Ref: referenceStars[m.Index1], Src: stars[m.Index2], Votes: m.Votes))
                .ToList();

            return EstimateAffineTransformation(pairs, token);
        }

        /// <summary>
        /// Caps the triangle matcher input so its cubic triangle generation remains bounded.
        /// </summary>
        /// <param name="stars">Input star catalog.</param>
        /// <param name="preserveOrder">Whether to keep the leading input stars instead of resampling spatially.</param>
        /// <returns>A bounded star catalog suitable for triangle matching.</returns>
        private List<Point> LimitPointSetForTriangleFallback(List<Point> stars, bool preserveOrder) {
            if (stars.Count <= MaxTriangleFallbackStars) {
                return stars;
            }

            if (preserveOrder) {
                return stars.Take(MaxTriangleFallbackStars).ToList();
            }

            var scoredStars = new List<ScoredStar>(stars.Count);
            for (int i = 0; i < stars.Count; i++) {
                scoredStars.Add(new ScoredStar(stars[i], 1d, i));
            }

            return SelectGeometricallyStableStars(scoredStars, MaxTriangleFallbackStars);
        }

        /// <summary>
        /// Attempts a geometry-only affine solve using local quad hashes.
        /// </summary>
        /// <remarks>
        /// Quad side-length descriptors are invariant to translation, rotation, and uniform scale.
        /// Candidate quads seed affine models, and each model is accepted only after it projects many
        /// reference stars onto target stars. This is the robust path for unknown frame shifts,
        /// ordering changes, and meridian flips.
        /// </remarks>
        /// <param name="referenceStars">Reference-frame stars.</param>
        /// <param name="stars">Target-frame stars.</param>
        /// <param name="affineTransformation">The solved affine transform when matching succeeds.</param>
        /// <returns><c>true</c> when a quad-derived proposal was found; ComputeAlignment performs final acceptance.</returns>
        private bool TryComputeQuadAffineTransformation(List<Point> referenceStars, List<Point> stars, out double[,] affineTransformation, CancellationToken token = default) {
            affineTransformation = null;
            List<Point> referenceCatalog;
            List<Quad> referenceQuads;
            Dictionary<long, List<int>> referenceQuadIndex;
            if (referenceStars.Count > MaxTriangleFallbackStars) {
                var referenceCache = GetQuadReferenceCache(referenceStars, token);
                referenceCatalog = referenceCache.ReferenceCatalog;
                referenceQuads = referenceCache.ReferenceQuads;
                referenceQuadIndex = referenceCache.ReferenceQuadIndex;
            } else {
                referenceCatalog = LimitPointSetForQuadMatcher(referenceStars);
                referenceQuads = BuildQuadList(referenceCatalog, MaxReferenceQuads, token);
                referenceQuadIndex = BuildQuadIndex(referenceQuads);
            }

            var targetCatalog = LimitPointSetForQuadMatcher(stars);

            if (referenceCatalog.Count < 4 || targetCatalog.Count < 4) {
                return false;
            }

            var targetQuads = BuildQuadList(targetCatalog, MaxTargetQuads, token);
            if (referenceQuads.Count == 0 || targetQuads.Count == 0) {
                return false;
            }

            int[] votingMatrix = new int[referenceCatalog.Count * targetCatalog.Count];
            var candidateModels = new List<QuadMatchCandidate>(512);
            int[] bestReferenceQuadIndices = new int[MaxQuadCandidates];
            double[] bestReferenceQuadDistances = new double[MaxQuadCandidates];

            foreach (var targetQuad in targetQuads) {
                token.ThrowIfCancellationRequested();
                int candidateCount = FindQuadCandidates(targetQuad, referenceQuadIndex, referenceQuads, bestReferenceQuadIndices, bestReferenceQuadDistances);

                for (int candidateRank = 0; candidateRank < candidateCount; candidateRank++) {
                    int referenceQuadIndexValue = bestReferenceQuadIndices[candidateRank];
                    if (referenceQuadIndexValue < 0) {
                        continue;
                    }

                    int voteWeight = MaxQuadCandidates - candidateRank;
                    AddQuadVotes(votingMatrix, targetCatalog.Count, referenceQuads[referenceQuadIndexValue], targetQuad, voteWeight);
                    InsertQuadMatchCandidate(candidateModels, referenceQuadIndexValue, targetQuad, bestReferenceQuadDistances[candidateRank], maxCandidates: 512);
                }
            }

            if (TryEstimateAffineFromQuadCandidates(candidateModels, referenceQuads, referenceCatalog, targetCatalog, out affineTransformation, token)) {
                return true;
            }

            var pairs = ComputeQuadMatchList(votingMatrix, referenceCatalog, targetCatalog);
            if (pairs.Count < 3) {
                return false;
            }

            affineTransformation = EstimateAffineTransformation(pairs, token);
            return true;
        }

        /// <summary>
        /// Caps dense catalogs before quad construction while preserving broad spatial coverage.
        /// </summary>
        /// <param name="stars">Input star catalog.</param>
        /// <returns>A bounded catalog for quad matching.</returns>
        private List<Point> LimitPointSetForQuadMatcher(List<Point> stars) {
            if (stars.Count <= MaxStars) {
                return stars;
            }

            var scoredStars = new List<ScoredStar>(stars.Count);
            for (int i = 0; i < stars.Count; i++) {
                scoredStars.Add(new ScoredStar(stars[i], 1d, i));
            }

            return SelectGeometricallyStableStars(scoredStars, MaxStars);
        }

        /// <summary>
        /// Gets or builds the cached reference-side quad catalog and hash index.
        /// </summary>
        /// <remarks>
        /// Live stacking aligns many target frames against the same reference frame. Caching the
        /// reference quads avoids rebuilding the most expensive reference-side structures for every
        /// incoming exposure while still allowing the cache to switch when a new stack reference is used.
        /// </remarks>
        /// <param name="referenceStars">Reference-frame stars before quad-matcher limiting.</param>
        /// <returns>Cached quad matcher state for the reference catalog.</returns>
        private QuadReferenceCache GetQuadReferenceCache(List<Point> referenceStars, CancellationToken token = default) {
            ulong fingerprint = ComputePointCatalogFingerprint(referenceStars);

            lock (quadReferenceCacheSyncRoot) {
                if (quadReferenceCache != null && quadReferenceCache.Fingerprint == fingerprint) {
                    return quadReferenceCache;
                }
            }

            var referenceCatalog = LimitPointSetForQuadMatcher(referenceStars);
            var referenceQuads = BuildQuadList(referenceCatalog, MaxReferenceQuads, token);
            var referenceQuadIndex = BuildQuadIndex(referenceQuads);
            var newCache = new QuadReferenceCache(fingerprint, referenceCatalog, referenceQuads, referenceQuadIndex);

            lock (quadReferenceCacheSyncRoot) {
                quadReferenceCache = newCache;
            }

            return newCache;
        }

        /// <summary>
        /// Computes a deterministic fingerprint for a point catalog so equivalent reference lists can reuse cached quad state.
        /// </summary>
        /// <param name="stars">Star catalog to fingerprint.</param>
        /// <returns>A hash value based on count and exact floating-point centroid coordinates.</returns>
        private static ulong ComputePointCatalogFingerprint(List<Point> stars) {
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            unchecked {
                ulong hash = offsetBasis;
                hash = (hash ^ (uint)stars.Count) * prime;
                for (int i = 0; i < stars.Count; i++) {
                    hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(stars[i].X)) * prime;
                    hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(stars[i].Y)) * prime;
                }

                return hash;
            }
        }

        /// <summary>
        /// Creates local quads from each star and its nearest neighbors.
        /// </summary>
        /// <param name="stars">Catalog from which quads are built.</param>
        /// <param name="maxQuads">Hard cap to keep dense fields bounded.</param>
        /// <returns>A list of non-degenerate quad descriptors.</returns>
        private List<Quad> BuildQuadList(List<Point> stars, int maxQuads, CancellationToken token = default) {
            var quads = new List<Quad>(Math.Min(maxQuads, stars.Count * 8));
            var seenQuads = new HashSet<long>();
            int nearestCapacity = Math.Min(QuadNeighborCount, Math.Max(0, stars.Count - 1));
            int[] nearestIndices = new int[nearestCapacity];
            double[] nearestDistances = new double[nearestCapacity];

            for (int centerIndex = 0; centerIndex < stars.Count && quads.Count < maxQuads; centerIndex++) {
                token.ThrowIfCancellationRequested();
                int nearestCount = FindNearestStarIndices(stars, centerIndex, nearestCapacity, nearestIndices, nearestDistances);
                for (int a = 0; a < nearestCount - 2 && quads.Count < maxQuads; a++) {
                    for (int b = a + 1; b < nearestCount - 1 && quads.Count < maxQuads; b++) {
                        for (int c = b + 1; c < nearestCount && quads.Count < maxQuads; c++) {
                            int index0 = centerIndex;
                            int index1 = nearestIndices[a];
                            int index2 = nearestIndices[b];
                            int index3 = nearestIndices[c];
                            long quadKey = GetQuadIndexKey(index0, index1, index2, index3);
                            if (!seenQuads.Add(quadKey)) {
                                continue;
                            }

                            if (TryCreateQuad(stars, index0, index1, index2, index3, out Quad quad)) {
                                quads.Add(quad);
                            }
                        }
                    }
                }
            }

            return quads;
        }

        /// <summary>
        /// Finds the nearest neighboring stars for local quad construction.
        /// </summary>
        /// <param name="stars">Catalog to search.</param>
        /// <param name="starIndex">Index of the center star.</param>
        /// <param name="requestedCount">Maximum number of neighbors to return.</param>
        /// <param name="nearestIndices">Reusable output buffer for nearest neighbor indices.</param>
        /// <param name="nearestDistances">Reusable output buffer for nearest neighbor squared distances.</param>
        /// <returns>The number of valid neighbor indices written to <paramref name="nearestIndices"/>.</returns>
        private static int FindNearestStarIndices(List<Point> stars, int starIndex, int requestedCount, int[] nearestIndices, double[] nearestDistances) {
            int neighborCount = Math.Min(requestedCount, stars.Count - 1);
            if (neighborCount <= 0) {
                return 0;
            }

            Array.Fill(nearestIndices, -1, 0, neighborCount);
            Array.Fill(nearestDistances, double.MaxValue, 0, neighborCount);
            int filledCount = 0;

            for (int i = 0; i < stars.Count; i++) {
                if (i == starIndex) {
                    continue;
                }

                double distanceSquared = DistanceSquared(stars[starIndex], stars[i]);
                if (neighborCount == 0 || distanceSquared >= nearestDistances[neighborCount - 1]) {
                    continue;
                }

                int insertAt = neighborCount - 1;
                while (insertAt > 0 && distanceSquared < nearestDistances[insertAt - 1]) {
                    nearestDistances[insertAt] = nearestDistances[insertAt - 1];
                    nearestIndices[insertAt] = nearestIndices[insertAt - 1];
                    insertAt--;
                }

                nearestDistances[insertAt] = distanceSquared;
                nearestIndices[insertAt] = i;
                if (filledCount < neighborCount) {
                    filledCount++;
                }
            }

            return filledCount;
        }

        private static long GetQuadIndexKey(int index0, int index1, int index2, int index3) {
            Span<int> indices = stackalloc int[4] { index0, index1, index2, index3 };
            indices.Sort();
            return (long)indices[0]
                | ((long)indices[1] << 10)
                | ((long)indices[2] << 20)
                | ((long)indices[3] << 30);
        }

        /// <summary>
        /// Creates a normalized quad descriptor when four stars form a useful non-degenerate shape.
        /// </summary>
        /// <param name="stars">Catalog containing the four stars.</param>
        /// <param name="index0">First star index.</param>
        /// <param name="index1">Second star index.</param>
        /// <param name="index2">Third star index.</param>
        /// <param name="index3">Fourth star index.</param>
        /// <param name="quad">The normalized quad descriptor when creation succeeds.</param>
        /// <returns><c>true</c> when the quad is large and distinctive enough to use.</returns>
        private bool TryCreateQuad(List<Point> stars, int index0, int index1, int index2, int index3, out Quad quad) {
            double d01 = Math.Sqrt(DistanceSquared(stars[index0], stars[index1]));
            double d02 = Math.Sqrt(DistanceSquared(stars[index0], stars[index2]));
            double d03 = Math.Sqrt(DistanceSquared(stars[index0], stars[index3]));
            double d12 = Math.Sqrt(DistanceSquared(stars[index1], stars[index2]));
            double d13 = Math.Sqrt(DistanceSquared(stars[index1], stars[index3]));
            double d23 = Math.Sqrt(DistanceSquared(stars[index2], stars[index3]));

            Span<double> sortedDistances = stackalloc double[6] { d01, d02, d03, d12, d13, d23 };
            sortedDistances.Sort();
            double longestSide = sortedDistances[5];
            if (longestSide < MinQuadLongestSide || sortedDistances[0] / longestSide < MinQuadShortestSideRatio) {
                quad = default;
                return false;
            }

            if (GetQuadAreaRatio(stars[index0], stars[index1], stars[index2], stars[index3], longestSide) < 0.015d) {
                quad = default;
                return false;
            }

            quad = new Quad(
                index0,
                index1,
                index2,
                index3,
                d01 / longestSide,
                d02 / longestSide,
                d03 / longestSide,
                d12 / longestSide,
                d13 / longestSide,
                d23 / longestSide,
                sortedDistances[0] / longestSide,
                sortedDistances[1] / longestSide,
                sortedDistances[2] / longestSide,
                sortedDistances[3] / longestSide,
                sortedDistances[4] / longestSide);
            return true;
        }

        private static double GetQuadAreaRatio(Point p0, Point p1, Point p2, Point p3, double longestSide) {
            double area1 = Math.Abs(((p1.X - p0.X) * (p2.Y - p0.Y)) - ((p1.Y - p0.Y) * (p2.X - p0.X)));
            double area2 = Math.Abs(((p1.X - p0.X) * (p3.Y - p0.Y)) - ((p1.Y - p0.Y) * (p3.X - p0.X)));
            double area3 = Math.Abs(((p2.X - p0.X) * (p3.Y - p0.Y)) - ((p2.Y - p0.Y) * (p3.X - p0.X)));
            double area4 = Math.Abs(((p2.X - p1.X) * (p3.Y - p1.Y)) - ((p2.Y - p1.Y) * (p3.X - p1.X)));
            return Math.Max(Math.Max(area1, area2), Math.Max(area3, area4)) / (longestSide * longestSide);
        }

        /// <summary>
        /// Indexes reference quads by quantized descriptor features for fast candidate lookup.
        /// </summary>
        /// <param name="quads">Reference quad descriptors.</param>
        /// <returns>A hash index from quantized descriptor key to reference quad indices.</returns>
        private Dictionary<long, List<int>> BuildQuadIndex(List<Quad> quads) {
            var index = new Dictionary<long, List<int>>(quads.Count);
            for (int i = 0; i < quads.Count; i++) {
                long key = GetQuadHashKey(quads[i]);
                if (!index.TryGetValue(key, out var bucket)) {
                    bucket = new List<int>();
                    index[key] = bucket;
                }

                bucket.Add(i);
            }

            return index;
        }

        private static long GetQuadHashKey(Quad quad) {
            return PackQuadHashKey(
                QuantizeQuadFeature(quad.Feature0),
                QuantizeQuadFeature(quad.Feature1),
                QuantizeQuadFeature(quad.Feature2),
                QuantizeQuadFeature(quad.Feature3),
                QuantizeQuadFeature(quad.Feature4));
        }

        private static int QuantizeQuadFeature(double value) {
            return (int)Math.Round(value / QuadHashBinSize);
        }

        private static long PackQuadHashKey(int q0, int q1, int q2, int q3, int q4) {
            return (long)(q0 + 2)
                | ((long)(q1 + 2) << 8)
                | ((long)(q2 + 2) << 16)
                | ((long)(q3 + 2) << 24)
                | ((long)(q4 + 2) << 32);
        }

        private static double GetQuadDescriptorDistanceSquared(Quad first, Quad second) {
            double d0 = first.Feature0 - second.Feature0;
            double d1 = first.Feature1 - second.Feature1;
            double d2 = first.Feature2 - second.Feature2;
            double d3 = first.Feature3 - second.Feature3;
            double d4 = first.Feature4 - second.Feature4;
            return (d0 * d0) + (d1 * d1) + (d2 * d2) + (d3 * d3) + (d4 * d4);
        }

        /// <summary>
        /// Finds the best matching reference quads for one target quad without per-quad allocations.
        /// </summary>
        /// <param name="targetQuad">Target quad descriptor to match.</param>
        /// <param name="referenceQuadIndex">Hash index of reference quads.</param>
        /// <param name="referenceQuads">Reference quad descriptors.</param>
        /// <param name="bestReferenceQuadIndices">Reusable sorted output buffer for reference quad indices.</param>
        /// <param name="bestReferenceQuadDistances">Reusable sorted output buffer for descriptor distances.</param>
        /// <returns>The number of candidate quads written to the output buffers.</returns>
        private static int FindQuadCandidates(
                Quad targetQuad,
                Dictionary<long, List<int>> referenceQuadIndex,
                List<Quad> referenceQuads,
                int[] bestReferenceQuadIndices,
                double[] bestReferenceQuadDistances) {
            for (int i = 0; i < MaxQuadCandidates; i++) {
                bestReferenceQuadIndices[i] = -1;
                bestReferenceQuadDistances[i] = double.MaxValue;
            }

            int candidateCount = 0;
            int q0 = QuantizeQuadFeature(targetQuad.Feature0);
            int q1 = QuantizeQuadFeature(targetQuad.Feature1);
            int q2 = QuantizeQuadFeature(targetQuad.Feature2);
            int q3 = QuantizeQuadFeature(targetQuad.Feature3);
            int q4 = QuantizeQuadFeature(targetQuad.Feature4);

            for (int d0 = -1; d0 <= 1; d0++) {
                for (int d1 = -1; d1 <= 1; d1++) {
                    for (int d2 = -1; d2 <= 1; d2++) {
                        for (int d3 = -1; d3 <= 1; d3++) {
                            for (int d4 = -1; d4 <= 1; d4++) {
                                long key = PackQuadHashKey(q0 + d0, q1 + d1, q2 + d2, q3 + d3, q4 + d4);
                                if (!referenceQuadIndex.TryGetValue(key, out var bucket)) {
                                    continue;
                                }

                                foreach (int referenceQuadIndexValue in bucket) {
                                    double distanceSquared = GetQuadDescriptorDistanceSquared(referenceQuads[referenceQuadIndexValue], targetQuad);
                                    if (distanceSquared > MaxQuadDescriptorDistanceSquared) {
                                        continue;
                                    }

                                    InsertQuadCandidate(referenceQuadIndexValue, distanceSquared, bestReferenceQuadIndices, bestReferenceQuadDistances, ref candidateCount);
                                }
                            }
                        }
                    }
                }
            }

            return candidateCount;
        }

        /// <summary>
        /// Keeps the nearest reference quads for one target quad without allocating a sortable list.
        /// </summary>
        /// <param name="referenceQuadIndex">Reference quad index to insert.</param>
        /// <param name="distanceSquared">Descriptor distance from the target quad.</param>
        /// <param name="bestReferenceQuadIndices">Fixed-size sorted candidate index buffer.</param>
        /// <param name="bestReferenceQuadDistances">Fixed-size sorted candidate distance buffer.</param>
        /// <param name="candidateCount">Current number of buffered candidates.</param>
        private static void InsertQuadCandidate(int referenceQuadIndex, double distanceSquared, Span<int> bestReferenceQuadIndices, Span<double> bestReferenceQuadDistances, ref int candidateCount) {
            if (candidateCount == MaxQuadCandidates && distanceSquared >= bestReferenceQuadDistances[MaxQuadCandidates - 1]) {
                return;
            }

            int insertAt = Math.Min(candidateCount, MaxQuadCandidates - 1);
            if (candidateCount < MaxQuadCandidates) {
                candidateCount++;
            }

            while (insertAt > 0 && distanceSquared < bestReferenceQuadDistances[insertAt - 1]) {
                bestReferenceQuadDistances[insertAt] = bestReferenceQuadDistances[insertAt - 1];
                bestReferenceQuadIndices[insertAt] = bestReferenceQuadIndices[insertAt - 1];
                insertAt--;
            }

            bestReferenceQuadDistances[insertAt] = distanceSquared;
            bestReferenceQuadIndices[insertAt] = referenceQuadIndex;
        }

        /// <summary>
        /// Keeps the globally strongest quad matches for direct affine-model validation.
        /// </summary>
        /// <param name="candidates">Sorted candidate list being maintained.</param>
        /// <param name="referenceQuadIndex">Matched reference quad index.</param>
        /// <param name="targetQuad">Matched target quad.</param>
        /// <param name="descriptorDistanceSquared">Descriptor distance between the two quads.</param>
        /// <param name="maxCandidates">Maximum number of retained candidates.</param>
        private static void InsertQuadMatchCandidate(List<QuadMatchCandidate> candidates, int referenceQuadIndex, Quad targetQuad, double descriptorDistanceSquared, int maxCandidates) {
            if (candidates.Count == maxCandidates && descriptorDistanceSquared >= candidates[candidates.Count - 1].DescriptorDistanceSquared) {
                return;
            }

            int insertAt = candidates.Count;
            if (candidates.Count < maxCandidates) {
                candidates.Add(default);
            } else {
                insertAt = maxCandidates - 1;
            }

            while (insertAt > 0 && descriptorDistanceSquared < candidates[insertAt - 1].DescriptorDistanceSquared) {
                candidates[insertAt] = candidates[insertAt - 1];
                insertAt--;
            }

            candidates[insertAt] = new QuadMatchCandidate(referenceQuadIndex, targetQuad, descriptorDistanceSquared);
        }

        /// <summary>
        /// Validates direct affine hypotheses generated from the best quad matches.
        /// </summary>
        /// <remarks>
        /// Each candidate quad proposes four star pairs. The resulting model must look physically
        /// plausible and must project a meaningful number of reference stars onto target stars before
        /// it is refined with least squares.
        /// </remarks>
        /// <param name="candidates">Best descriptor-level quad matches.</param>
        /// <param name="referenceQuads">Reference quad descriptors.</param>
        /// <param name="referenceCatalog">Reference star catalog.</param>
        /// <param name="targetCatalog">Target star catalog.</param>
        /// <param name="affineTransformation">The refined transform when a candidate validates.</param>
        /// <returns><c>true</c> when a validated affine model was found.</returns>
        private bool TryEstimateAffineFromQuadCandidates(
                List<QuadMatchCandidate> candidates,
                List<Quad> referenceQuads,
                List<Point> referenceCatalog,
                List<Point> targetCatalog,
                out double[,] affineTransformation, CancellationToken token = default) {
            affineTransformation = null;
            double bestScore = double.NegativeInfinity;
            List<(Point Ref, Point Src, int Votes)> bestPairs = null;
            const double inlierThresholdPx = 4.0d;
            var targetGrid = BuildTargetSpatialIndex(targetCatalog, inlierThresholdPx);

            foreach (var candidate in candidates) {
                token.ThrowIfCancellationRequested();
                var referenceQuad = referenceQuads[candidate.ReferenceQuadIndex];
                var targetQuad = candidate.TargetQuad;
                int[] permutation = new int[4];
                GetBestQuadPermutation(referenceQuad, targetQuad, permutation);

                var seedPairs = new List<(Point Ref, Point Src, int Votes)>(4);
                for (int i = 0; i < 4; i++) {
                    seedPairs.Add((
                        referenceCatalog[referenceQuad.GetIndex(i)],
                        targetCatalog[targetQuad.GetIndex(permutation[i])],
                        1));
                }

                double[,] model;
                try {
                    model = RefineAffineLeastSquares(seedPairs, new List<int> { 0, 1, 2, 3 });
                } catch {
                    continue;
                }

                if (!IsPlausibleAffineModel(model)) {
                    continue;
                }

                var inlierPairs = CollectProjectedInliers(model, referenceCatalog, targetCatalog, inlierThresholdPx, targetGrid);
                if (inlierPairs.Count < 8) {
                    continue;
                }

                double score = inlierPairs.Count - (candidate.DescriptorDistanceSquared * 100d);
                if (score > bestScore) {
                    bestScore = score;
                    bestPairs = inlierPairs;
                }
            }

            if (bestPairs == null || bestPairs.Count < 8) {
                return false;
            }

            affineTransformation = RefineAffineLeastSquares(bestPairs, Enumerable.Range(0, bestPairs.Count).ToList());
            return true;
        }

        /// <summary>
        /// Projects reference stars through a model and collects nearest target stars within a threshold.
        /// </summary>
        /// <param name="model">Affine transform to validate.</param>
        /// <param name="referenceCatalog">Reference stars to project.</param>
        /// <param name="targetCatalog">Target stars to search.</param>
        /// <param name="inlierThresholdPx">Maximum allowed projection error in pixels.</param>
        /// <returns>Projected inlier pairs suitable for final least-squares refinement.</returns>
        private List<(Point Ref, Point Src, int Votes)> CollectProjectedInliers(double[,] model, List<Point> referenceCatalog, List<Point> targetCatalog, double inlierThresholdPx) {
            var targetGrid = BuildTargetSpatialIndex(targetCatalog, inlierThresholdPx);
            return CollectProjectedInliers(model, referenceCatalog, targetCatalog, inlierThresholdPx, targetGrid);
        }

        /// <summary>
        /// Projects reference stars through a model using a prebuilt target-star spatial index.
        /// </summary>
        /// <param name="model">Affine transform to validate.</param>
        /// <param name="referenceCatalog">Reference stars to project.</param>
        /// <param name="targetCatalog">Target stars to search.</param>
        /// <param name="inlierThresholdPx">Maximum allowed projection error in pixels.</param>
        /// <param name="targetGrid">Prebuilt target-star spatial index using <paramref name="inlierThresholdPx"/> as cell size.</param>
        /// <returns>Projected inlier pairs suitable for final least-squares refinement.</returns>
        private List<(Point Ref, Point Src, int Votes)> CollectProjectedInliers(
                double[,] model,
                List<Point> referenceCatalog,
                List<Point> targetCatalog,
                double inlierThresholdPx,
                Dictionary<long, List<int>> targetGrid) {
            double thresholdSquared = inlierThresholdPx * inlierThresholdPx;
            int[] bestReference = Enumerable.Repeat(-1, targetCatalog.Count).ToArray();
            double[] bestDistances = Enumerable.Repeat(thresholdSquared, targetCatalog.Count).ToArray();
            for (int referenceIndex = 0; referenceIndex < referenceCatalog.Count; referenceIndex++) {
                Point projected = ApplyAffine(referenceCatalog[referenceIndex], model);
                if (!float.IsFinite(projected.X) || !float.IsFinite(projected.Y)) {
                    continue;
                }
                int bestTarget = -1;
                double nearestDistance = thresholdSquared;
                int cellX = (int)Math.Floor(projected.X / inlierThresholdPx);
                int cellY = (int)Math.Floor(projected.Y / inlierThresholdPx);
                for (int y = cellY - 1; y <= cellY + 1; y++) {
                    for (int x = cellX - 1; x <= cellX + 1; x++) {
                        if (!targetGrid.TryGetValue(PackSpatialGridKey(x, y), out List<int> indices)) {
                            continue;
                        }
                        foreach (int targetIndex in indices) {
                            double distance = DistanceSquared(projected, targetCatalog[targetIndex]);
                            if (distance < nearestDistance) {
                                nearestDistance = distance;
                                bestTarget = targetIndex;
                            }
                        }
                    }
                }
                // A target contributes once, using only its closest proposed reference star.
                if (bestTarget >= 0 && nearestDistance < bestDistances[bestTarget]) {
                    bestReference[bestTarget] = referenceIndex;
                    bestDistances[bestTarget] = nearestDistance;
                }
            }
            List<(Point Ref, Point Src, int Votes)> pairs = new();
            for (int i = 0; i < bestReference.Length; i++) {
                if (bestReference[i] >= 0) {
                    pairs.Add((referenceCatalog[bestReference[i]], targetCatalog[i], 1));
                }
            }
            return pairs;
        }

        /// <summary>
        /// Builds a grid index for target stars so projected-inlier checks are near-linear.
        /// </summary>
        /// <param name="targetCatalog">Target stars to index.</param>
        /// <param name="cellSize">Grid cell size in pixels.</param>
        /// <returns>A hash grid mapping cell keys to target star indices.</returns>
        private static Dictionary<long, List<int>> BuildTargetSpatialIndex(List<Point> targetCatalog, double cellSize) {
            var targetGrid = new Dictionary<long, List<int>>(targetCatalog.Count);
            for (int targetIndex = 0; targetIndex < targetCatalog.Count; targetIndex++) {
                int cellX = (int)Math.Floor(targetCatalog[targetIndex].X / cellSize);
                int cellY = (int)Math.Floor(targetCatalog[targetIndex].Y / cellSize);
                long key = PackSpatialGridKey(cellX, cellY);

                if (!targetGrid.TryGetValue(key, out List<int> targetIndices)) {
                    targetIndices = new List<int>(1);
                    targetGrid[key] = targetIndices;
                }

                targetIndices.Add(targetIndex);
            }

            return targetGrid;
        }

        private static long PackSpatialGridKey(int cellX, int cellY) {
            return ((long)cellX << 32) ^ (uint)cellY;
        }

        /// <summary>
        /// Adds star-pair votes from a matched quad after choosing the best vertex permutation.
        /// </summary>
        /// <param name="voteBuffer">Flattened reference-by-source vote matrix.</param>
        /// <param name="sourceStarCount">Number of source stars, used as the matrix stride.</param>
        /// <param name="referenceQuad">Reference quad.</param>
        /// <param name="sourceQuad">Source quad.</param>
        /// <param name="voteWeight">Vote weight for this descriptor match.</param>
        private static void AddQuadVotes(int[] voteBuffer, int sourceStarCount, Quad referenceQuad, Quad sourceQuad, int voteWeight) {
            Span<int> bestPermutation = stackalloc int[4];
            GetBestQuadPermutation(referenceQuad, sourceQuad, bestPermutation);

            for (int i = 0; i < 4; i++) {
                int referenceIndex = referenceQuad.GetIndex(i);
                int sourceIndex = sourceQuad.GetIndex(bestPermutation[i]);
                voteBuffer[(referenceIndex * sourceStarCount) + sourceIndex] += voteWeight;
            }
        }

        /// <summary>
        /// Finds the source-quad vertex ordering with the smallest normalized side-length error.
        /// </summary>
        /// <param name="referenceQuad">Reference quad descriptor.</param>
        /// <param name="sourceQuad">Source quad descriptor.</param>
        /// <param name="bestPermutation">Output mapping from reference vertex slot to source vertex slot.</param>
        private static void GetBestQuadPermutation(Quad referenceQuad, Quad sourceQuad, Span<int> bestPermutation) {
            double bestError = double.MaxValue;

            for (int p0 = 0; p0 < 4; p0++) {
                for (int p1 = 0; p1 < 4; p1++) {
                    if (p1 == p0) {
                        continue;
                    }

                    for (int p2 = 0; p2 < 4; p2++) {
                        if (p2 == p0 || p2 == p1) {
                            continue;
                        }

                        for (int p3 = 0; p3 < 4; p3++) {
                            if (p3 == p0 || p3 == p1 || p3 == p2) {
                                continue;
                            }

                            double error = GetQuadPermutationError(referenceQuad, sourceQuad, p0, p1, p2, p3);
                            if (error < bestError) {
                                bestError = error;
                                bestPermutation[0] = p0;
                                bestPermutation[1] = p1;
                                bestPermutation[2] = p2;
                                bestPermutation[3] = p3;
                            }
                        }
                    }
                }
            }
        }

        private static double GetQuadPermutationError(Quad referenceQuad, Quad sourceQuad, int p0, int p1, int p2, int p3) {
            double d01 = referenceQuad.Distance01 - sourceQuad.GetDistance(p0, p1);
            double d02 = referenceQuad.Distance02 - sourceQuad.GetDistance(p0, p2);
            double d03 = referenceQuad.Distance03 - sourceQuad.GetDistance(p0, p3);
            double d12 = referenceQuad.Distance12 - sourceQuad.GetDistance(p1, p2);
            double d13 = referenceQuad.Distance13 - sourceQuad.GetDistance(p1, p3);
            double d23 = referenceQuad.Distance23 - sourceQuad.GetDistance(p2, p3);

            return (d01 * d01) + (d02 * d02) + (d03 * d03) + (d12 * d12) + (d13 * d13) + (d23 * d23);
        }

        /// <summary>
        /// Converts quad vote totals into one-to-one star correspondences.
        /// </summary>
        /// <param name="votingMatrix">Flattened reference-by-target vote matrix.</param>
        /// <param name="referenceCatalog">Reference star catalog.</param>
        /// <param name="targetCatalog">Target star catalog.</param>
        /// <returns>Distinctive star pairs ordered by vote strength.</returns>
        private List<(Point Ref, Point Src, int Votes)> ComputeQuadMatchList(int[] votingMatrix, List<Point> referenceCatalog, List<Point> targetCatalog) {
            int rowCount = referenceCatalog.Count;
            int columnCount = targetCatalog.Count;
            var matches = new List<Match>();

            int[] rowBestScores = new int[rowCount];
            int[] rowSecondBestScores = new int[rowCount];
            int[] rowBestIndices = Enumerable.Repeat(-1, rowCount).ToArray();

            int[] columnBestScores = new int[columnCount];
            int[] columnSecondBestScores = new int[columnCount];
            int[] columnBestIndices = Enumerable.Repeat(-1, columnCount).ToArray();

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++) {
                int rowOffset = rowIndex * columnCount;
                for (int columnIndex = 0; columnIndex < columnCount; columnIndex++) {
                    int score = votingMatrix[rowOffset + columnIndex];

                    if (score > rowBestScores[rowIndex]) {
                        rowSecondBestScores[rowIndex] = rowBestScores[rowIndex];
                        rowBestScores[rowIndex] = score;
                        rowBestIndices[rowIndex] = columnIndex;
                    } else if (score > rowSecondBestScores[rowIndex]) {
                        rowSecondBestScores[rowIndex] = score;
                    }

                    if (score > columnBestScores[columnIndex]) {
                        columnSecondBestScores[columnIndex] = columnBestScores[columnIndex];
                        columnBestScores[columnIndex] = score;
                        columnBestIndices[columnIndex] = rowIndex;
                    } else if (score > columnSecondBestScores[columnIndex]) {
                        columnSecondBestScores[columnIndex] = score;
                    }
                }
            }

            int bestVoteScore = rowBestScores.Length == 0 ? 0 : rowBestScores.Max();
            int minimumVotes = Math.Max(2, bestVoteScore / 8);

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++) {
                int columnIndex = rowBestIndices[rowIndex];
                if (columnIndex < 0 || rowBestScores[rowIndex] < minimumVotes) {
                    continue;
                }

                if (columnBestIndices[columnIndex] != rowIndex) {
                    continue;
                }

                if (!IsDistinctiveMatch(rowBestScores[rowIndex], rowSecondBestScores[rowIndex])
                    || !IsDistinctiveMatch(columnBestScores[columnIndex], columnSecondBestScores[columnIndex])) {
                    continue;
                }

                matches.Add(new Match { Index1 = rowIndex, Index2 = columnIndex, Votes = rowBestScores[rowIndex] });
            }

            if (matches.Count < 8) {
                AppendGreedyMatches(matches, votingMatrix, rowCount, columnCount, minimumVotes);
            }

            SortMatchArray(ref matches);

            return matches
                .Take(MaxQuadMatchPairs)
                .Select(match => (Ref: referenceCatalog[match.Index1], Src: targetCatalog[match.Index2], Votes: match.Votes))
                .ToList();
        }

        /// <summary>
        /// Fits an affine transform from matched star pairs using sparse least squares or two-pass RANSAC.
        /// </summary>
        /// <param name="pairs">Candidate reference-to-source star correspondences.</param>
        /// <returns>A refined affine transform from reference to source coordinates.</returns>
        private double[,] EstimateAffineTransformation(List<(Point Ref, Point Src, int Votes)> pairs, CancellationToken token = default) {
            token.ThrowIfCancellationRequested();
            if (pairs.Count < 3)
                throw new InvalidOperationException("Not enough matches for affine estimation.");

            if (pairs.Count < 8) {
                // This is only a proposal for full-catalog validation, never an accepted alignment.
                // Final acceptance still requires at least eight independent, distributed matches.
                return RefineAffineLeastSquares(pairs, Enumerable.Range(0, pairs.Count).ToList());
            }

            int minInliers = Math.Clamp((int)Math.Ceiling(pairs.Count * 0.15d), 4, 12);
            int ransacIterations = pairs.Count > 50 ? 3000 : 500;

            // PASS 1: generous threshold to bootstrap
            var (model1, inliers1) = FitAffineRansac(
                pairs,
                iterations: ransacIterations,
                inlierThresholdPx: 5.0,
                minInliers: minInliers, token: token);

            // Robustly estimate residual scale from inliers
            var residuals = ComputeResiduals(model1, pairs, inliers1);
            double sigma = RobustSigmaFromResiduals(residuals);

            // PASS 2: tighter, data-driven threshold
            // Clamp keeps it sane across extreme cases.
            double thr = Math.Clamp(3.0 * sigma, 1.0, 6.0);

            var (model2, inliers2) = FitAffineRansac(
                pairs,
                iterations: ransacIterations,
                inlierThresholdPx: thr,
                minInliers: minInliers, token: token);

            // Final refinement on inliers
            return RefineAffineLeastSquares(pairs, inliers2);
        }

        /// <summary>
        /// Runs bounded affine RANSAC and prioritizes geometric consensus over vote totals.
        /// </summary>
        /// <param name="pairs">Candidate reference-to-source star correspondences.</param>
        /// <param name="iterations">Maximum number of RANSAC samples.</param>
        /// <param name="inlierThresholdPx">Maximum inlier residual in pixels.</param>
        /// <param name="minInliers">Minimum accepted inlier count.</param>
        /// <returns>The best affine model and indices of its inlier pairs.</returns>
        private (double[,] Model, List<int> Inliers) FitAffineRansac(
                List<(Point Ref, Point Src, int Votes)> pairs,
                int iterations,
                double inlierThresholdPx,
                int minInliers, CancellationToken token = default) {
            if (pairs.Count < 3)
                throw new ArgumentException("Need at least 3 correspondences.");

            var rng = new Random(unchecked((int)ComputePointCatalogFingerprint(pairs.Select(pair => pair.Ref).ToList())));
            double bestScore = double.NegativeInfinity;
            double[,] bestModel = null;
            List<int> bestInliers = new();

            double thr2 = inlierThresholdPx * inlierThresholdPx;

            int targetIterations = iterations;
            const double confidence = 0.995d;

            for (int it = 0; it < targetIterations; it++) {
                token.ThrowIfCancellationRequested();
                // sample 3 unique indices
                int i0 = rng.Next(pairs.Count);
                int i1 = rng.Next(pairs.Count);
                int i2 = rng.Next(pairs.Count);
                if (i1 == i0 || i2 == i0 || i2 == i1) { continue; }

                // Every attempt consumes the budget, including degenerate or duplicate samples.
                if (IsDegenerateTriplet(pairs[i0].Ref, pairs[i1].Ref, pairs[i2].Ref)) { continue; }
                if (IsDegenerateTriplet(pairs[i0].Src, pairs[i1].Src, pairs[i2].Src)) { continue; }

                // Fit affine from exactly 3 pairs
                var model = FitAffineFrom3(pairs[i0], pairs[i1], pairs[i2]);
                if (model == null || !IsPlausibleAffineModel(model)) continue;

                // Prefer more geometric inliers; residuals only break ties between equal counts.
                var inliers = new List<int>(capacity: pairs.Count);
                double score = 0;

                for (int i = 0; i < pairs.Count; i++) {
                    var p = pairs[i];
                    var proj = ApplyAffine(p.Ref, model);

                    double dx = proj.X - p.Src.X;
                    double dy = proj.Y - p.Src.Y;
                    double e2 = dx * dx + dy * dy;

                    if (e2 <= thr2) {
                        inliers.Add(i);

                        // The total residual penalty stays below one inlier.
                        score += 1.0 - e2 / (thr2 * pairs.Count + 1);

                    }
                }

                if (inliers.Count >= minInliers && score > bestScore) {
                    bestScore = score;
                    bestModel = model;
                    bestInliers = inliers;

                    double inlierRatio = inliers.Count / (double)pairs.Count;
                    int requiredIterations = EstimateRequiredRansacIterations(inlierRatio, confidence, sampleSize: 3, maxIterations: iterations);
                    if (requiredIterations < targetIterations) {
                        targetIterations = requiredIterations;
                    }
                }
            }

            if (bestModel == null)
                throw new InvalidOperationException("RANSAC failed to find a valid affine model.");

            return (bestModel, bestInliers);
        }

        /// <summary>
        /// Rejects nearly collinear three-star samples that cannot define a stable affine model.
        /// </summary>
        /// <param name="a">First point.</param>
        /// <param name="b">Second point.</param>
        /// <param name="c">Third point.</param>
        /// <returns><c>true</c> when the points are too close to collinear.</returns>
        private bool IsDegenerateTriplet(Point a, Point b, Point c) {
            double abX = b.X - a.X;
            double abY = b.Y - a.Y;
            double acX = c.X - a.X;
            double acY = c.Y - a.Y;
            double bcX = c.X - b.X;
            double bcY = c.Y - b.Y;

            double area2 = Math.Abs((abX * acY) - (abY * acX));
            double maxSideSquared = Math.Max(
                (abX * abX) + (abY * abY),
                Math.Max((acX * acX) + (acY * acY), (bcX * bcX) + (bcY * bcY)));

            if (maxSideSquared <= 1e-6) {
                return true;
            }

            return (area2 / maxSideSquared) < 0.01d;
        }

        /// <summary>
        /// Solves the exact affine transform implied by three matched point pairs.
        /// </summary>
        /// <param name="p0">First correspondence.</param>
        /// <param name="p1">Second correspondence.</param>
        /// <param name="p2">Third correspondence.</param>
        /// <returns>A 3x3 affine matrix, or <c>null</c> when the reference triangle is singular.</returns>
        private double[,] FitAffineFrom3(
                (Point Ref, Point Src, int Votes) p0,
                (Point Ref, Point Src, int Votes) p1,
                (Point Ref, Point Src, int Votes) p2) {
            double x0 = p0.Ref.X;
            double y0 = p0.Ref.Y;
            double x1 = p1.Ref.X;
            double y1 = p1.Ref.Y;
            double x2 = p2.Ref.X;
            double y2 = p2.Ref.Y;

            double det = (x0 * (y1 - y2)) + (x1 * (y2 - y0)) + (x2 * (y0 - y1));
            if (Math.Abs(det) < 1e-6d) {
                return null;
            }

            double invDet = 1d / det;

            double m00 = (y1 - y2) * invDet;
            double m01 = (y2 - y0) * invDet;
            double m02 = (y0 - y1) * invDet;

            double m10 = (x2 - x1) * invDet;
            double m11 = (x0 - x2) * invDet;
            double m12 = (x1 - x0) * invDet;

            double m20 = ((x1 * y2) - (x2 * y1)) * invDet;
            double m21 = ((x2 * y0) - (x0 * y2)) * invDet;
            double m22 = ((x0 * y1) - (x1 * y0)) * invDet;

            double u0 = p0.Src.X;
            double v0 = p0.Src.Y;
            double u1 = p1.Src.X;
            double v1 = p1.Src.Y;
            double u2 = p2.Src.X;
            double v2 = p2.Src.Y;

            return new double[3, 3] {
                {
                    (m00 * u0) + (m01 * u1) + (m02 * u2),
                    (m10 * u0) + (m11 * u1) + (m12 * u2),
                    (m20 * u0) + (m21 * u1) + (m22 * u2)
                },
                {
                    (m00 * v0) + (m01 * v1) + (m02 * v2),
                    (m10 * v0) + (m11 * v1) + (m12 * v2),
                    (m20 * v0) + (m21 * v1) + (m22 * v2)
                },
                { 0d, 0d, 1d }
            };
        }

        private Point ApplyAffine(Point p, double[,] m) {
            double x = m[0, 0] * p.X + m[0, 1] * p.Y + m[0, 2];
            double y = m[1, 0] * p.X + m[1, 1] * p.Y + m[1, 2];
            return new Point((float)x, (float)y);
        }

        /// <summary>
        /// Refines an affine transform from selected inlier pairs with least squares.
        /// </summary>
        /// <param name="pairs">All candidate correspondences.</param>
        /// <param name="inliers">Indices of correspondences to use for refinement.</param>
        /// <returns>A 3x3 affine matrix mapping reference coordinates to source coordinates.</returns>
        private double[,] RefineAffineLeastSquares(
                List<(Point Ref, Point Src, int Votes)> pairs,
                List<int> inliers) {
            int n = inliers.Count;
            if (n < 3)
                throw new InvalidOperationException("Need at least 3 inliers to refine.");

            double[,] source = new double[n, 2];
            double[,] target = new double[n, 2];

            for (int i = 0; i < n; i++) {
                var p = pairs[inliers[i]];
                source[i, 0] = p.Ref.X;
                source[i, 1] = p.Ref.Y;
                target[i, 0] = p.Src.X;
                target[i, 1] = p.Src.Y;
            }

            return ComputeAffineTransformationMatrix(source, target);
        }

        /// <summary>
        /// Detects whether an affine transform is close to a 180 degree image rotation.
        /// </summary>
        /// <param name="affineMatrix">Affine matrix to inspect.</param>
        /// <returns><c>true</c> when the rotation angle is approximately upside down.</returns>
        public bool IsFlippedImage(double[,] affineMatrix) {
            // Extract the top-left 2x2 part of the affine transformation matrix
            double a = affineMatrix[0, 0];
            double b = affineMatrix[0, 1];
            double c = affineMatrix[1, 0];
            double d = affineMatrix[1, 1];

            // Calculate the angle of rotation (in radians)
            double angleRad = Math.Atan2(b, a);  // atan2 handles both sin and cos components
            double angleDeg = angleRad * (180.0 / Math.PI);  // Convert radians to degrees

            // Normalize the angle to be within 0 to 360 degrees
            if (angleDeg < 0)
                angleDeg += 360;

            // Check if the angle is between 160° and 200°
            return angleDeg >= 160 && angleDeg <= 200;
        }

        /// <summary>Resamples an incoming frame into reference coordinates with bilinear interpolation.</summary>
        public float[] ApplyAffineTransformation(float[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            float[] destination = new float[AffineResampler.GetLength(width, height)];
            ApplyAffineTransformationInto(sourceImageData, destination, width, height, affineMatrix, flippedImage);
            return destination;
        }

        public void ApplyAffineTransformationInto(float[] sourceImageData, float[] destinationImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            AffineResampler.Transform(sourceImageData, destinationImageData, width, height, affineMatrix, flippedImage);
        }

        /// <summary>Compatibility overload for stacks with a uniform prior contribution count.</summary>
        public void ApplyAffineTransformationAndStack(float[] sourceImageData, float[] stackImageData, int stackImageCount, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            AffineResampler.Accumulate(sourceImageData, stackImageData, null, stackImageCount, width, height, affineMatrix, flippedImage);
        }

        public void ApplyAffineTransformationAndStack(float[] sourceImageData, float[] stackImageData, uint[] contributionCounts, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            ArgumentNullException.ThrowIfNull(contributionCounts);
            AffineResampler.Accumulate(sourceImageData, stackImageData, contributionCounts, 0, width, height, affineMatrix, flippedImage);
        }

        public float[] ApplyAffineTransformation(ushort[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            float[] destination = new float[AffineResampler.GetLength(width, height)];
            ApplyAffineTransformationInto(sourceImageData, destination, width, height, affineMatrix, flippedImage);
            return destination;
        }

        public void ApplyAffineTransformationInto(ushort[] sourceImageData, float[] destinationImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            AffineResampler.Transform(sourceImageData, destinationImageData, width, height, affineMatrix, flippedImage);
        }

        /// <summary>Compatibility overload for stacks with a uniform prior contribution count.</summary>
        public void ApplyAffineTransformationAndStack(ushort[] sourceImageData, float[] stackImageData, int stackImageCount, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            AffineResampler.Accumulate(sourceImageData, stackImageData, null, stackImageCount, width, height, affineMatrix, flippedImage);
        }

        public void ApplyAffineTransformationAndStack(ushort[] sourceImageData, float[] stackImageData, uint[] contributionCounts, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            ArgumentNullException.ThrowIfNull(contributionCounts);
            AffineResampler.Accumulate(sourceImageData, stackImageData, contributionCounts, 0, width, height, affineMatrix, flippedImage);
        }

        public ushort[] ApplyAffineTransformationAsUshort(float[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            ushort[] destination = new ushort[AffineResampler.GetLength(width, height)];
            ApplyAffineTransformationAsUshortInto(sourceImageData, destination, width, height, affineMatrix, flippedImage);
            return destination;
        }

        public void ApplyAffineTransformationAsUshortInto(float[] sourceImageData, ushort[] destinationImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            AffineResampler.Transform(sourceImageData, destinationImageData, width, height, affineMatrix, flippedImage);
        }
        /// <summary>
        /// Solves the least-squares affine matrix that maps source points to target points.
        /// </summary>
        /// <param name="sourcePoints">Source coordinate array with one x/y pair per row.</param>
        /// <param name="targetPoints">Target coordinate array with one x/y pair per row.</param>
        /// <returns>A 3x3 affine transformation matrix.</returns>
        private double[,] ComputeAffineTransformationMatrix(double[,] sourcePoints, double[,] targetPoints) {
            int count = sourcePoints.GetLength(0);
            if (count < 3) {
                throw new InvalidOperationException("At least three pairs are needed to propose an affine model.");
            }
            double meanX = 0, meanY = 0, meanU = 0, meanV = 0;
            for (int i = 0; i < count; i++) {
                meanX += sourcePoints[i, 0] / count;
                meanY += sourcePoints[i, 1] / count;
                meanU += targetPoints[i, 0] / count;
                meanV += targetPoints[i, 1] / count;
            }
            double scale = 0;
            for (int i = 0; i < count; i++) {
                scale += Math.Pow(sourcePoints[i, 0] - meanX, 2) + Math.Pow(sourcePoints[i, 1] - meanY, 2);
            }
            scale = Math.Sqrt(scale / count);
            if (!double.IsFinite(scale) || scale < 1e-6) {
                throw new InvalidOperationException("Degenerate affine correspondences.");
            }
            DenseMatrix design = DenseMatrix.Create(count, 3, (i, j) => j == 0 ? (sourcePoints[i, 0] - meanX) / scale : j == 1 ? (sourcePoints[i, 1] - meanY) / scale : 1);
            DenseMatrix target = DenseMatrix.Create(count, 2, (i, j) => targetPoints[i, j] - (j == 0 ? meanU : meanV));
            var decomposition = design.Svd();
            if (decomposition.S[2] <= decomposition.S[0] * 1e-6) {
                throw new InvalidOperationException("Nearly collinear affine correspondences.");
            }
            var solution = decomposition.Solve(target);
            double a = solution[0, 0] / scale, b = solution[1, 0] / scale;
            double c = solution[0, 1] / scale, d = solution[1, 1] / scale;
            return new double[,] {
                { a, b, meanU + solution[2, 0] - a * meanX - b * meanY },
                { c, d, meanV + solution[2, 1] - c * meanX - d * meanY },
                { 0, 0, 1 }
            };
        }

        /// <summary>
        /// Builds normalized triangle descriptors used by the fallback triangle matcher.
        /// </summary>
        /// <param name="starList">Star catalog to convert into triangle descriptors.</param>
        /// <returns>Triangle descriptors sorted for range-based matching.</returns>
        private List<Triangle> ComputeTriangleList(List<Point> starList) {
            double[,] distanceMatrix = new double[starList.Count + 1, starList.Count + 1];
            List<Triangle> triangles = new List<Triangle>();

            for (int i = 0; i < starList.Count; i++) {
                for (int j = 0; j < starList.Count; j++) {
                    var star1 = starList[i];
                    var star2 = starList[j];
                    distanceMatrix[i, j] = Math.Sqrt(Math.Pow(star1.X - star2.X, 2.0) + Math.Pow(star1.Y - star2.Y, 2.0));
                }
            }

            const double maxRelativeSideLength = 0.95;
            const double minSumOfSides = 1.3;
            const double minSideDifference = 0.05;
            for (int i = 0; i < starList.Count; i++) {
                for (int j = i + 1; j < starList.Count; j++) {
                    for (int k = j + 1; k < starList.Count; k++) {
                        double side1 = distanceMatrix[i, j];
                        double side2 = distanceMatrix[i, k];
                        double side3 = distanceMatrix[j, k];
                        int index1 = i;
                        int index2 = j;
                        int index3 = k;

                        if (side1 < side2) {
                            (side1, side2) = (side2, side1);
                            (index2, index3) = (index3, index2);
                        }
                        if (side2 < side3) {
                            (side2, side3) = (side3, side2);
                            (index1, index2) = (index2, index1);
                        }
                        if (side1 < side2) {
                            (side1, side2) = (side2, side1);
                            (index2, index3) = (index3, index2);
                        }
                        if (side1 >= MinTriangleLongestSide) {
                            side2 /= side1;
                            side3 /= side1;
                            if (side2 < maxRelativeSideLength && side3 < maxRelativeSideLength && side2 + side3 > minSumOfSides && Math.Abs(side2 - side3) > minSideDifference) {
                                Triangle triangle = new Triangle(side1, side2, side3, index1, index2, index3);
                                triangles.Add(triangle);
                            }
                        }
                    }
                }
            }
            SortTriangleArray(ref triangles);
            return triangles;
        }

        /// <summary>
        /// Accumulates star-pair votes from compatible triangle descriptors.
        /// </summary>
        /// <param name="starCount1">Number of reference stars.</param>
        /// <param name="starCount2">Number of target stars.</param>
        /// <param name="triangles1">Reference triangle descriptors.</param>
        /// <param name="triangles2">Target triangle descriptors.</param>
        /// <param name="maxTriangleDistance">Maximum normalized descriptor distance.</param>
        /// <returns>A flattened reference-by-target vote matrix.</returns>
        private int[] ComputeVotingMatrix(int starCount1, int starCount2, List<Triangle> triangles1, List<Triangle> triangles2, double maxTriangleDistance) {
            double maxTriangleDistanceSquared = maxTriangleDistance * maxTriangleDistance;
            int[] votingMatrix = new int[starCount1 * starCount2];

            bool isTriangleSet1Smaller = triangles1.Count <= triangles2.Count;
            var smallerTriangleSet = isTriangleSet1Smaller ? triangles1 : triangles2;
            var largerTriangleSet = isTriangleSet1Smaller ? triangles2 : triangles1;
            var triangleComparer = new TriangleComparer();

            if (smallerTriangleSet.Count < MinimumTriangleSetForParallelVoting || Environment.ProcessorCount == 1) {
                for (int smallerIndex = 0; smallerIndex < smallerTriangleSet.Count; smallerIndex++) {
                    AccumulateTriangleVotes(smallerTriangleSet[smallerIndex], largerTriangleSet, isTriangleSet1Smaller, starCount2, maxTriangleDistance, maxTriangleDistanceSquared, triangleComparer, votingMatrix);
                }
                return votingMatrix;
            }

            object mergeLock = new object();
            Parallel.For(0, smallerTriangleSet.Count,
                () => new int[votingMatrix.Length],
                (smallerIndex, _, localVotes) => {
                    AccumulateTriangleVotes(smallerTriangleSet[smallerIndex], largerTriangleSet, isTriangleSet1Smaller, starCount2, maxTriangleDistance, maxTriangleDistanceSquared, triangleComparer, localVotes);
                    return localVotes;
                },
                localVotes => {
                    lock (mergeLock) {
                        for (int i = 0; i < votingMatrix.Length; i++) {
                            votingMatrix[i] += localVotes[i];
                        }
                    }
                });

            return votingMatrix;
        }

        /// <summary>
        /// Adds weighted votes from the nearest compatible triangle descriptors.
        /// </summary>
        /// <param name="smallerTriangle">Triangle descriptor from the smaller descriptor set.</param>
        /// <param name="largerTriangleSet">Sorted larger descriptor set.</param>
        /// <param name="isTriangleSet1Smaller">Whether the smaller descriptor set belongs to the reference stars.</param>
        /// <param name="starCount2">Target-star count used as the flattened matrix stride.</param>
        /// <param name="maxTriangleDistance">Maximum normalized descriptor distance.</param>
        /// <param name="maxTriangleDistanceSquared">Squared maximum normalized descriptor distance.</param>
        /// <param name="triangleComparer">Comparer used for range searches in the sorted descriptor set.</param>
        /// <param name="voteBuffer">Vote matrix to update.</param>
        private void AccumulateTriangleVotes(Triangle smallerTriangle, List<Triangle> largerTriangleSet, bool isTriangleSet1Smaller, int starCount2, double maxTriangleDistance, double maxTriangleDistanceSquared, TriangleComparer triangleComparer, int[] voteBuffer) {
            double smallerSide3 = smallerTriangle.Side3;
            double smallerSide2 = smallerTriangle.Side2;

            int rangeStart = largerTriangleSet.BinarySearch(new Triangle { Side3 = smallerSide3 - maxTriangleDistance }, triangleComparer);
            int rangeEnd = largerTriangleSet.BinarySearch(new Triangle { Side3 = smallerSide3 + maxTriangleDistance }, triangleComparer);
            rangeStart = rangeStart < 0 ? ~rangeStart : rangeStart;
            rangeEnd = rangeEnd < 0 ? ~rangeEnd : rangeEnd;

            Span<int> bestTriangleIndices = stackalloc int[MaxTriangleCandidates];
            Span<double> bestTriangleDistances = stackalloc double[MaxTriangleCandidates];
            for (int i = 0; i < MaxTriangleCandidates; i++) {
                bestTriangleIndices[i] = -1;
                bestTriangleDistances[i] = double.MaxValue;
            }

            int candidateCount = 0;
            for (int largerIndex = rangeStart; largerIndex < rangeEnd; largerIndex++) {
                var largerTriangle = largerTriangleSet[largerIndex];
                double scaleDelta = Math.Abs(Math.Log((smallerTriangle.Side1 + 1e-6d) / (largerTriangle.Side1 + 1e-6d)));
                if (scaleDelta > MaxTriangleScaleDelta) {
                    continue;
                }

                double distanceSquared =
                    ((smallerSide3 - largerTriangle.Side3) * (smallerSide3 - largerTriangle.Side3)) +
                    ((smallerSide2 - largerTriangle.Side2) * (smallerSide2 - largerTriangle.Side2));

                if (distanceSquared >= maxTriangleDistanceSquared) {
                    continue;
                }

                InsertTriangleCandidate(largerIndex, distanceSquared, bestTriangleIndices, bestTriangleDistances, ref candidateCount);
            }

            for (int candidateRank = 0; candidateRank < candidateCount; candidateRank++) {
                int largerIndex = bestTriangleIndices[candidateRank];
                if (largerIndex < 0) {
                    continue;
                }

                int voteWeight = MaxTriangleCandidates - candidateRank;
                AddTriangleVotes(voteBuffer, starCount2, smallerTriangle, largerTriangleSet[largerIndex], isTriangleSet1Smaller, voteWeight);
            }
        }

        private static void InsertTriangleCandidate(int largerIndex, double distanceSquared, Span<int> bestTriangleIndices, Span<double> bestTriangleDistances, ref int candidateCount) {
            if (candidateCount == MaxTriangleCandidates && distanceSquared >= bestTriangleDistances[MaxTriangleCandidates - 1]) {
                return;
            }

            int insertAt = Math.Min(candidateCount, MaxTriangleCandidates - 1);
            if (candidateCount < MaxTriangleCandidates) {
                candidateCount++;
            }

            while (insertAt > 0 && distanceSquared < bestTriangleDistances[insertAt - 1]) {
                bestTriangleDistances[insertAt] = bestTriangleDistances[insertAt - 1];
                bestTriangleIndices[insertAt] = bestTriangleIndices[insertAt - 1];
                insertAt--;
            }

            bestTriangleDistances[insertAt] = distanceSquared;
            bestTriangleIndices[insertAt] = largerIndex;
        }

        private static void AddTriangleVotes(int[] voteBuffer, int starCount2, Triangle smallerTriangle, Triangle largerTriangle, bool isTriangleSet1Smaller, int voteWeight) {
            if (isTriangleSet1Smaller) {
                voteBuffer[(smallerTriangle.Index1 * starCount2) + largerTriangle.Index1] += voteWeight;
                voteBuffer[(smallerTriangle.Index2 * starCount2) + largerTriangle.Index2] += voteWeight;
                voteBuffer[(smallerTriangle.Index3 * starCount2) + largerTriangle.Index3] += voteWeight;
            } else {
                voteBuffer[(largerTriangle.Index1 * starCount2) + smallerTriangle.Index1] += voteWeight;
                voteBuffer[(largerTriangle.Index2 * starCount2) + smallerTriangle.Index2] += voteWeight;
                voteBuffer[(largerTriangle.Index3 * starCount2) + smallerTriangle.Index3] += voteWeight;
            }
        }

        /// <summary>
        /// Converts triangle vote totals into distinctive one-to-one star matches.
        /// </summary>
        /// <param name="votingMatrix">Flattened reference-by-target vote matrix.</param>
        /// <param name="rowCount">Number of reference stars.</param>
        /// <param name="columnCount">Number of target stars.</param>
        /// <param name="voteThreshold">Scale factor for the minimum vote threshold.</param>
        /// <returns>Distinctive matches sorted by vote strength.</returns>
        private List<Match> ComputeMatchList(int[] votingMatrix, int rowCount, int columnCount, double voteThreshold) {
            List<Match> matchList = new List<Match>();

            int matrixSize = Math.Min(rowCount, columnCount);

            double threshold = (matrixSize - 1) * (matrixSize - 2) / voteThreshold;
            int minimumVotes = (int)Math.Round(Math.Max(4.0, threshold));

            int[] rowBestScores = new int[rowCount];
            int[] rowSecondBestScores = new int[rowCount];
            int[] rowBestIndices = Enumerable.Repeat(-1, rowCount).ToArray();

            int[] columnBestScores = new int[columnCount];
            int[] columnSecondBestScores = new int[columnCount];
            int[] columnBestIndices = Enumerable.Repeat(-1, columnCount).ToArray();

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++) {
                int rowOffset = rowIndex * columnCount;
                for (int columnIndex = 0; columnIndex < columnCount; columnIndex++) {
                    int score = votingMatrix[rowOffset + columnIndex];

                    if (score > rowBestScores[rowIndex]) {
                        rowSecondBestScores[rowIndex] = rowBestScores[rowIndex];
                        rowBestScores[rowIndex] = score;
                        rowBestIndices[rowIndex] = columnIndex;
                    } else if (score > rowSecondBestScores[rowIndex]) {
                        rowSecondBestScores[rowIndex] = score;
                    }

                    if (score > columnBestScores[columnIndex]) {
                        columnSecondBestScores[columnIndex] = columnBestScores[columnIndex];
                        columnBestScores[columnIndex] = score;
                        columnBestIndices[columnIndex] = rowIndex;
                    } else if (score > columnSecondBestScores[columnIndex]) {
                        columnSecondBestScores[columnIndex] = score;
                    }
                }
            }

            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++) {
                int columnIndex = rowBestIndices[rowIndex];
                if (columnIndex < 0) {
                    continue;
                }

                int bestScore = rowBestScores[rowIndex];
                if (bestScore < minimumVotes) {
                    continue;
                }

                if (columnBestIndices[columnIndex] != rowIndex) {
                    continue;
                }

                if (!IsDistinctiveMatch(bestScore, rowSecondBestScores[rowIndex])) {
                    continue;
                }

                if (!IsDistinctiveMatch(bestScore, columnSecondBestScores[columnIndex])) {
                    continue;
                }

                matchList.Add(new Match { Index1 = rowIndex, Index2 = columnIndex, Votes = bestScore });
            }

            if (matchList.Count < 3) {
                AppendGreedyMatches(matchList, votingMatrix, rowCount, columnCount, Math.Max(4, (int)Math.Floor(minimumVotes * 0.75)));
            }

            SortMatchArray(ref matchList);

            return matchList;
        }

        private void SortMatchArray(ref List<Match> m) {
            m.Sort(static (left, right) => right.Votes.CompareTo(left.Votes));
        }

        private void SortTriangleArray(ref List<Triangle> t) {
            t.Sort(new TriangleComparer());
        }

        private static bool IsDistinctiveMatch(int bestScore, int secondBestScore) {
            if (bestScore <= 0) {
                return false;
            }

            if (secondBestScore <= 0) {
                return true;
            }

            return bestScore >= secondBestScore + 2 || (bestScore * 10) >= (secondBestScore * 13);
        }

        private void AppendGreedyMatches(List<Match> matchList, int[] votingMatrix, int rowCount, int columnCount, int minimumVotes) {
            var usedRows = new bool[rowCount];
            var usedColumns = new bool[columnCount];

            foreach (var match in matchList) {
                usedRows[match.Index1] = true;
                usedColumns[match.Index2] = true;
            }

            var candidates = new List<Match>();
            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++) {
                int rowOffset = rowIndex * columnCount;
                for (int columnIndex = 0; columnIndex < columnCount; columnIndex++) {
                    int score = votingMatrix[rowOffset + columnIndex];
                    if (score >= minimumVotes) {
                        candidates.Add(new Match { Index1 = rowIndex, Index2 = columnIndex, Votes = score });
                    }
                }
            }

            SortMatchArray(ref candidates);

            foreach (var candidate in candidates) {
                if (!usedRows[candidate.Index1] && !usedColumns[candidate.Index2]) {
                    matchList.Add(candidate);
                    usedRows[candidate.Index1] = true;
                    usedColumns[candidate.Index2] = true;
                }
            }
        }

        /// <summary>
        /// Rejects affine models with unrealistic scale or near-singular determinants.
        /// </summary>
        /// <param name="model">Affine matrix to validate.</param>
        /// <returns><c>true</c> when the matrix is plausible for live-stack frame alignment.</returns>
        private bool IsPlausibleAffineModel(double[,] model) {
            return AlignmentGeometry.IsPlausible(model);
        }

        /// <summary>
        /// Estimates how many RANSAC samples are still needed for the requested confidence.
        /// </summary>
        /// <param name="inlierRatio">Current observed inlier ratio.</param>
        /// <param name="confidence">Desired probability of sampling an all-inlier set.</param>
        /// <param name="sampleSize">Number of correspondences per RANSAC sample.</param>
        /// <param name="maxIterations">Upper bound on the iteration count.</param>
        /// <returns>A clamped RANSAC iteration target.</returns>
        private static int EstimateRequiredRansacIterations(double inlierRatio, double confidence, int sampleSize, int maxIterations) {
            double clampedInlierRatio = Math.Clamp(inlierRatio, 1e-3d, 0.999d);
            double successProbability = Math.Pow(clampedInlierRatio, sampleSize);
            double denominator = Math.Log(1d - successProbability);
            if (double.IsNaN(denominator) || double.IsInfinity(denominator) || denominator >= 0d) {
                return maxIterations;
            }

            int requiredIterations = (int)Math.Ceiling(Math.Log(1d - confidence) / denominator);
            return Math.Clamp(requiredIterations, sampleSize, maxIterations);
        }

        /// <summary>
        /// Estimates residual scale using median absolute deviation.
        /// </summary>
        /// <param name="residuals">Inlier residual distances in pixels.</param>
        /// <returns>A robust sigma estimate for tightening the second RANSAC pass.</returns>
        private static double RobustSigmaFromResiduals(double[] residuals) {
            // sigma ≈ 1.4826 * MAD, MAD = median(|r - median(r)|)
            if (residuals == null || residuals.Length == 0) return 0.0;
            double med = residuals.Median();

            var absDev = new double[residuals.Length];
            for (int i = 0; i < residuals.Length; i++)
                absDev[i] = Math.Abs(residuals[i] - med);

            double mad = absDev.Median();
            return 1.4826 * mad;
        }

        /// <summary>
        /// Computes projection residuals for a set of inlier correspondences.
        /// </summary>
        /// <param name="model">Affine model to evaluate.</param>
        /// <param name="pairs">All candidate correspondences.</param>
        /// <param name="inlierIdx">Indices of correspondences to measure.</param>
        /// <returns>Euclidean residual distances in pixels.</returns>
        private double[] ComputeResiduals(
            double[,] model,
            List<(Point Ref, Point Src, int Votes)> pairs,
            List<int> inlierIdx) {
            var r = new double[inlierIdx.Count];

            for (int k = 0; k < inlierIdx.Count; k++) {
                var p = pairs[inlierIdx[k]];
                var proj = ApplyAffine(p.Ref, model);

                double dx = proj.X - p.Src.X;
                double dy = proj.Y - p.Src.Y;

                r[k] = Math.Sqrt(dx * dx + dy * dy);
            }

            return r;
        }

        private readonly struct ScoredStar {
            public ScoredStar(Point position, double score, int sourceIndex) {
                Position = position;
                Score = score;
                SourceIndex = sourceIndex;
            }

            public Point Position { get; }
            public double Score { get; }
            public int SourceIndex { get; }
        }

        private struct StarFilterDiagnostics {
            public int InvalidPosition;
            public int OutsideFrame;
            public int InvalidBrightness;
            public int SaturatedBrightness;
            public int HfrOutlier;
            public int BrightnessFallbackCandidates;

            public override string ToString() {
                return $"invalid-position={InvalidPosition}, outside-frame={OutsideFrame}, invalid-brightness={InvalidBrightness}, saturated-brightness={SaturatedBrightness}, hfr-outlier={HfrOutlier}, brightness-fallback-candidates={BrightnessFallbackCandidates}";
            }
        }

        private struct Match {
            public int Index1 { get; set; }
            public int Index2 { get; set; }
            public int Votes { get; set; }
        };

        private readonly struct QuadMatchCandidate {
            public QuadMatchCandidate(int referenceQuadIndex, Quad targetQuad, double descriptorDistanceSquared) {
                ReferenceQuadIndex = referenceQuadIndex;
                TargetQuad = targetQuad;
                DescriptorDistanceSquared = descriptorDistanceSquared;
            }

            public int ReferenceQuadIndex { get; }
            public Quad TargetQuad { get; }
            public double DescriptorDistanceSquared { get; }
        };

        private sealed class QuadReferenceCache {
            public QuadReferenceCache(
                    ulong fingerprint,
                    List<Point> referenceCatalog,
                    List<Quad> referenceQuads,
                    Dictionary<long, List<int>> referenceQuadIndex) {
                Fingerprint = fingerprint;
                ReferenceCatalog = referenceCatalog;
                ReferenceQuads = referenceQuads;
                ReferenceQuadIndex = referenceQuadIndex;
            }

            public ulong Fingerprint { get; }
            public List<Point> ReferenceCatalog { get; }
            public List<Quad> ReferenceQuads { get; }
            public Dictionary<long, List<int>> ReferenceQuadIndex { get; }
        }

        private readonly struct Quad {
            public Quad(
                    int index0,
                    int index1,
                    int index2,
                    int index3,
                    double distance01,
                    double distance02,
                    double distance03,
                    double distance12,
                    double distance13,
                    double distance23,
                    double feature0,
                    double feature1,
                    double feature2,
                    double feature3,
                    double feature4) {
                Index0 = index0;
                Index1 = index1;
                Index2 = index2;
                Index3 = index3;
                Distance01 = distance01;
                Distance02 = distance02;
                Distance03 = distance03;
                Distance12 = distance12;
                Distance13 = distance13;
                Distance23 = distance23;
                Feature0 = feature0;
                Feature1 = feature1;
                Feature2 = feature2;
                Feature3 = feature3;
                Feature4 = feature4;
            }

            public int Index0 { get; }
            public int Index1 { get; }
            public int Index2 { get; }
            public int Index3 { get; }
            public double Distance01 { get; }
            public double Distance02 { get; }
            public double Distance03 { get; }
            public double Distance12 { get; }
            public double Distance13 { get; }
            public double Distance23 { get; }
            public double Feature0 { get; }
            public double Feature1 { get; }
            public double Feature2 { get; }
            public double Feature3 { get; }
            public double Feature4 { get; }

            public int GetIndex(int index) {
                return index switch {
                    0 => Index0,
                    1 => Index1,
                    2 => Index2,
                    3 => Index3,
                    _ => throw new ArgumentOutOfRangeException(nameof(index))
                };
            }

            public double GetDistance(int first, int second) {
                if (first > second) {
                    (first, second) = (second, first);
                }

                return (first, second) switch {
                    (0, 1) => Distance01,
                    (0, 2) => Distance02,
                    (0, 3) => Distance03,
                    (1, 2) => Distance12,
                    (1, 3) => Distance13,
                    (2, 3) => Distance23,
                    _ => throw new ArgumentOutOfRangeException(nameof(first))
                };
            }
        };

        private struct Triangle {

            public Triangle(double side1, double side2, double side3, int index1, int index2, int index3) {
                Side1 = side1;
                Side2 = side2;
                Side3 = side3;
                Index1 = index1;
                Index2 = index2;
                Index3 = index3;
            }

            public double Side1 { get; set; }
            public double Side2 { get; set; }
            public double Side3 { get; set; }
            public int Index1 { get; set; }
            public int Index2 { get; set; }
            public int Index3 { get; set; }
        };

        private class TriangleComparer : IComparer<Triangle> {

            public int Compare(Triangle x, Triangle y) {
                return x.Side3.CompareTo(y.Side3);
            }
        }
    }
}
