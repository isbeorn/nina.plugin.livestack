using System;
using System.Collections.Generic;

namespace NINA.Plugin.Livestack.Image {
    internal static class AlignmentGeometry {
        // Live-stack frames share an optical setup. Permit small scale/distortion changes,
        // while requiring substantial overlap and forbidding reflections or large shear.
        internal const double MinimumOverlap = 0.25;
        private const double MinimumScale = 0.98;
        private const double MaximumScale = 1.02;
        private const double MaximumAxisRatio = 1.02;

        internal static bool IsFiniteAffine(double[,] matrix) {
            if (matrix == null || matrix.GetLength(0) != 3 || matrix.GetLength(1) != 3) {
                return false;
            }
            foreach (double value in matrix) {
                if (!double.IsFinite(value)) {
                    return false;
                }
            }
            return matrix[2, 0] == 0 && matrix[2, 1] == 0 && matrix[2, 2] == 1;
        }

        internal static bool IsPlausible(double[,] matrix) {
            if (!IsFiniteAffine(matrix)) {
                return false;
            }
            double a = matrix[0, 0], b = matrix[0, 1], c = matrix[1, 0], d = matrix[1, 1];
            double determinant = a * d - b * c;
            double xx = a * a + c * c, yy = b * b + d * d, xy = a * b + c * d;
            double discriminant = Math.Sqrt((xx - yy) * (xx - yy) + 4 * xy * xy);
            double largest = Math.Sqrt(Math.Max(0, (xx + yy + discriminant) / 2));
            double smallest = Math.Sqrt(Math.Max(0, (xx + yy - discriminant) / 2));
            const double epsilon = 1e-9;
            return determinant > 0
                && smallest >= MinimumScale - epsilon
                && largest <= MaximumScale + epsilon
                && largest <= smallest * MaximumAxisRatio + epsilon;
        }

        internal static void ValidateForStack(double[,] matrix, int width, int height) {
            if (!IsPlausible(matrix) || GetOverlapFraction(matrix, width, height) < MinimumOverlap) {
                throw new ArgumentException("Alignment has invalid geometry or insufficient frame overlap.", nameof(matrix));
            }
        }

        /// <summary>Area of the source footprint inside the reference frame, in reference pixels.</summary>
        internal static double GetOverlapFraction(double[,] matrix, int width, int height) {
            if (!IsFiniteAffine(matrix) || width <= 0 || height <= 0) {
                return 0;
            }
            double a = matrix[0, 0], b = matrix[0, 1], tx = matrix[0, 2];
            double c = matrix[1, 0], d = matrix[1, 1], ty = matrix[1, 2];
            double determinant = a * d - b * c;
            if (Math.Abs(determinant) < 1e-12) {
                return 0;
            }
            // Pixel centers are integers, so the footprint extends half a pixel beyond them.
            List<(double X, double Y)> polygon = new(4);
            foreach ((double x, double y) in new[] { (-0.5, -0.5), (width - 0.5, -0.5), (width - 0.5, height - 0.5), (-0.5, height - 0.5) }) {
                polygon.Add(((d * (x - tx) - b * (y - ty)) / determinant, (-c * (x - tx) + a * (y - ty)) / determinant));
            }
            polygon = Clip(polygon, true, -0.5, true);
            polygon = Clip(polygon, true, width - 0.5, false);
            polygon = Clip(polygon, false, -0.5, true);
            polygon = Clip(polygon, false, height - 0.5, false);
            double area = 0;
            for (int i = 0; i < polygon.Count; i++) {
                (double x0, double y0) = polygon[i];
                (double x1, double y1) = polygon[(i + 1) % polygon.Count];
                area += x0 * y1 - x1 * y0;
            }
            return double.IsFinite(area) ? Math.Clamp(Math.Abs(area) / (2d * width * height), 0, 1) : 0;
        }

        private static List<(double X, double Y)> Clip(List<(double X, double Y)> input, bool xAxis, double boundary, bool minimum) {
            List<(double X, double Y)> output = new();
            if (input.Count == 0) {
                return output;
            }
            (double X, double Y) previous = input[input.Count - 1];
            double previousCoordinate = xAxis ? previous.X : previous.Y;
            bool previousInside = minimum ? previousCoordinate >= boundary : previousCoordinate <= boundary;
            foreach ((double X, double Y) current in input) {
                double currentCoordinate = xAxis ? current.X : current.Y;
                bool currentInside = minimum ? currentCoordinate >= boundary : currentCoordinate <= boundary;
                if (currentInside != previousInside) {
                    double fraction = (boundary - previousCoordinate) / (currentCoordinate - previousCoordinate);
                    output.Add((previous.X + fraction * (current.X - previous.X), previous.Y + fraction * (current.Y - previous.Y)));
                }
                if (currentInside) {
                    output.Add(current);
                }
                previous = current;
                previousCoordinate = currentCoordinate;
                previousInside = currentInside;
            }
            return output;
        }
    }
}
