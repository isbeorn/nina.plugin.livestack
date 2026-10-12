using Accord;
using System;
using System.Collections.Generic;

namespace NINA.Plugin.Livestack.Image {
    public partial class ImageTransformer2 {
        private sealed class AlignmentSearch {
            private const double CellSize = 4;
            private readonly long[] keys;
            private readonly int[] heads;
            private readonly int[] next;
            private readonly int[] bestReference;
            private readonly double[] bestDistances;

            public AlignmentSearch(List<Point> reference, List<Point> current, bool fixedReference, int? currentMatchCount = null) {
                Reference = reference;
                Current = current;
                FixedReference = fixedReference;
                Minimum = Math.Max(MinimumAlignmentInliers, (int)Math.Ceiling(Math.Min(reference.Count, currentMatchCount ?? current.Count) * MinimumMatchFraction));
                int capacity = 1;
                while (capacity < current.Count * 2) {
                    capacity <<= 1;
                }
                keys = new long[capacity];
                heads = new int[capacity];
                Array.Fill(heads, -1);
                next = new int[current.Count];
                bestReference = new int[current.Count];
                bestDistances = new double[current.Count];
                for (int i = 0; i < current.Count; i++) {
                    Point point = current[i];
                    long key = Key((int)Math.Floor(point.X / CellSize), (int)Math.Floor(point.Y / CellSize));
                    int slot = FindSlot(key);
                    keys[slot] = key;
                    next[i] = heads[slot];
                    heads[slot] = i;
                }
            }

            public List<Point> Reference { get; }
            public List<Point> Current { get; }
            public bool FixedReference { get; }
            public int Denominator => FixedReference ? Reference.Count : Math.Min(Reference.Count, Current.Count);
            public int Minimum { get; }

            public bool PassesLocalChanceGate(double[,] model, List<(Point Ref, Point Src, int Votes)> pairs, int width, int height) {
                ReadOnlySpan<double> radiiSquared = [0.25, 1, 4];
                Span<double> expected = stackalloc double[3];
                Span<int> observed = stackalloc int[3];
                expected.Clear();
                observed.Clear();
                foreach (Point reference in Reference) {
                    Point projected = ApplyAffine(reference, model);
                    double density = EstimateLocalDensity(projected, width, height);
                    double outsideX = Math.Max(0, Math.Max(-0.5 - projected.X, projected.X - width + 0.5));
                    double outsideY = Math.Max(0, Math.Max(-0.5 - projected.Y, projected.Y - height + 0.5));
                    double outsideSquared = outsideX * outsideX + outsideY * outsideY;
                    for (int i = 0; i < radiiSquared.Length; i++) {
                        if (outsideSquared < radiiSquared[i]) {
                            expected[i] += 1 - Math.Exp(-density * Math.PI * radiiSquared[i]);
                        }
                    }
                }
                foreach ((Point Ref, Point Src, int Votes) pair in pairs) {
                    double dx = model[0, 0] * pair.Ref.X + model[0, 1] * pair.Ref.Y + model[0, 2] - pair.Src.X;
                    double dy = model[1, 0] * pair.Ref.X + model[1, 1] * pair.Ref.Y + model[1, 2] - pair.Src.Y;
                    double residualSquared = dx * dx + dy * dy;
                    for (int i = 0; i < radiiSquared.Length; i++) {
                        if (residualSquared <= radiiSquared[i]) {
                            observed[i]++;
                        }
                    }
                }
                // This is a conservative local Poisson heuristic, not a calibrated probability.
                // The union correction covers three radii and the bounded recovery hypotheses:
                // three two-pass 3000-iteration RANSAC solves, 512 quads and their final fits.
                double maximumLogTail = Math.Log(1e-5 / (3 * 32768));
                for (int i = 0; i < radiiSquared.Length; i++) {
                    if (observed[i] <= expected[i]) {
                        continue;
                    }
                    double logTail = expected[i] > 0
                        ? observed[i] - expected[i] - observed[i] * Math.Log(observed[i] / expected[i])
                        : double.NegativeInfinity;
                    if (logTail <= maximumLogTail) {
                        return true;
                    }
                }
                return false;
            }

            private double EstimateLocalDensity(Point projected, int width, int height) {
                const double radius = 16;
                int cellX = (int)Math.Floor(projected.X / CellSize);
                int cellY = (int)Math.Floor(projected.Y / CellSize);
                int count = 0;
                for (int y = cellY - 4; y <= cellY + 4; y++) {
                    for (int x = cellX - 4; x <= cellX + 4; x++) {
                        int slot = FindSlot(Key(x, y));
                        for (int target = heads[slot]; target >= 0; target = next[target]) {
                            if (DistanceSquared(projected, Current[target]) <= radius * radius) {
                                count++;
                            }
                        }
                    }
                }
                // An inscribed square is a lower bound on the disk's area even at frame edges.
                // Counting the entire disk over this smaller area overestimates chance density.
                double halfSide = radius / Math.Sqrt(2);
                double xLength = Math.Max(0, Math.Min(width - 0.5, projected.X + halfSide) - Math.Max(-0.5, projected.X - halfSide));
                double yLength = Math.Max(0, Math.Min(height - 0.5, projected.Y + halfSide) - Math.Max(-0.5, projected.Y - halfSide));
                double area = xLength * yLength;
                return area > 0 ? count / area : double.PositiveInfinity;
            }

            public List<(Point Ref, Point Src, int Votes)> Collect(double[,] model, double radius) {
                double thresholdSquared = radius * radius;
                Array.Fill(bestReference, -1);
                Array.Fill(bestDistances, thresholdSquared);
                for (int referenceIndex = 0; referenceIndex < Reference.Count; referenceIndex++) {
                    Point projected = ApplyAffine(Reference[referenceIndex], model);
                    if (!float.IsFinite(projected.X) || !float.IsFinite(projected.Y)) {
                        continue;
                    }
                    int bestTarget = -1;
                    double nearestDistance = thresholdSquared;
                    int cellX = (int)Math.Floor(projected.X / CellSize);
                    int cellY = (int)Math.Floor(projected.Y / CellSize);
                    for (int y = cellY - 1; y <= cellY + 1; y++) {
                        for (int x = cellX - 1; x <= cellX + 1; x++) {
                            long key = Key(x, y);
                            int slot = FindSlot(key);
                            if (heads[slot] < 0) {
                                continue;
                            }
                            for (int targetIndex = heads[slot]; targetIndex >= 0; targetIndex = next[targetIndex]) {
                                double distance = DistanceSquared(projected, Current[targetIndex]);
                                if (distance < nearestDistance || (distance == nearestDistance && bestTarget >= 0 && targetIndex < bestTarget)) {
                                    nearestDistance = distance;
                                    bestTarget = targetIndex;
                                }
                            }
                        }
                    }
                    if (bestTarget >= 0 && nearestDistance < bestDistances[bestTarget]) {
                        bestReference[bestTarget] = referenceIndex;
                        bestDistances[bestTarget] = nearestDistance;
                    }
                }
                List<(Point Ref, Point Src, int Votes)> pairs = new(Reference.Count);
                for (int i = 0; i < bestReference.Length; i++) {
                    if (bestReference[i] >= 0) {
                        pairs.Add((Reference[bestReference[i]], Current[i], 1));
                    }
                }
                return pairs;
            }

            private int FindSlot(long key) {
                ulong hash = unchecked((ulong)key * 11400714819323198485UL);
                hash ^= hash >> 32;
                int slot = (int)(hash & (uint)(heads.Length - 1));
                while (heads[slot] >= 0 && keys[slot] != key) {
                    slot = (slot + 1) & (heads.Length - 1);
                }
                return slot;
            }

            private static long Key(int x, int y) {
                return ((long)x << 32) ^ (uint)y;
            }
        }
    }
}
