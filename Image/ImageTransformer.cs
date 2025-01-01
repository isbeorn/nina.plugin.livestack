using Accord;
using MathNet.Numerics.LinearAlgebra.Double;
using NINA.Image.ImageAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Plugin.Livestack.Image {

    public class ImageTransformer {

        public static List<Accord.Point> GetStars(List<DetectedStar> starList, int width, int height) {
            const int maxStars = 100;

            // Filter stars by brightness
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

        private static double GetDistanceToCenter(Accord.Point position, double centerX, double centerY) {
            return Math.Sqrt(Math.Pow(position.X - centerX, 2) + Math.Pow(position.Y - centerY, 2));
        }

        public static double[,] ComputeAffineTransformation(List<Point> stars, List<Point> referenceStars) {
            var referenceTriangles = ComputeTriangleList(referenceStars);
            var targetTriangles = ComputeTriangleList(stars);

            var votingMatrix = ComputeVotingMatrix(referenceStars.Count, stars.Count, referenceTriangles, targetTriangles, 0.02);

            var triangleMatches = ComputeMatchList(ref votingMatrix, 150);
            int voteThreshold = (int)triangleMatches.Average(x => x.Votes);
            triangleMatches = triangleMatches.Where(x => x.Votes > voteThreshold).ToList();

            double[,] sourcePoints = new double[triangleMatches.Count, 2];
            double[,] targetPoints = new double[triangleMatches.Count, 2];
            for (int i = 0; i < triangleMatches.Count; i++) {
                var match = triangleMatches[i];
                var star1 = referenceStars[match.Index1];
                var star2 = stars[match.Index2];
                sourcePoints[i, 0] = star1.X;
                sourcePoints[i, 1] = star1.Y;

                targetPoints[i, 0] = star2.X;
                targetPoints[i, 1] = star2.Y;
            }
            var matrix = ComputeAffineTransformationMatrix(sourcePoints, targetPoints);
            return matrix;
        }

        public static float[] ApplyAffineTransformation(float[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            // Create a new output array to hold the transformed image
            float[] transformedImageData = new float[width * height];

            // Loop through all pixels in the source image
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    // Apply the affine transformation to each pixel's coordinates
                    var transformedCoords = ApplyAffineMatrix(x, y, affineMatrix);

                    // Map transformed coordinates back to the new image
                    int newX = (int)transformedCoords.X;
                    int newY = (int)transformedCoords.Y;
                    if (flippedImage) {
                        newX = width - 1 - newX;
                        newY = height - 1 - newY;
                    }

                    // Ensure we stay within the image bounds
                    if (newX >= 0 && newX < width && newY >= 0 && newY < height) {
                        // Get the interpolated pixel value from the source image data at the transformed coordinates
                        float newPixelValue = GetInterpolatedPixelValue(newX, newY, sourceImageData, width, height);

                        // Set the pixel in the transformed image data
                        transformedImageData[y * width + x] = newPixelValue;
                    }
                }
            }

            return transformedImageData;
        }

        public static float[] ApplyAffineTransformation(ushort[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            // Create a new output array to hold the transformed image
            float[] transformedImageData = new float[width * height];

            // Loop through all pixels in the source image
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    // Apply the affine transformation to each pixel's coordinates
                    var transformedCoords = ApplyAffineMatrix(x, y, affineMatrix);

                    // Map transformed coordinates back to the new image
                    int newX = (int)transformedCoords.X;
                    int newY = (int)transformedCoords.Y;
                    if (flippedImage) {
                        newX = width - 1 - newX;
                        newY = height - 1 - newY;
                    }

                    // Ensure we stay within the image bounds
                    if (newX >= 0 && newX < width && newY >= 0 && newY < height) {
                        // Get the interpolated pixel value from the source image data at the transformed coordinates
                        float newPixelValue = GetInterpolatedPixelValue(newX, newY, sourceImageData, width, height);

                        // Set the pixel in the transformed image data
                        transformedImageData[y * width + x] = newPixelValue;
                    }
                }
            }

            return transformedImageData;
        }

        public static ushort[] ApplyAffineTransformationAsUshort(float[] sourceImageData, int width, int height, double[,] affineMatrix, bool flippedImage = false) {
            // Create a new output array to hold the transformed image
            ushort[] transformedImageData = new ushort[width * height];

            // Loop through all pixels in the source image
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    // Apply the affine transformation to each pixel's coordinates
                    var transformedCoords = ApplyAffineMatrix(x, y, affineMatrix);

                    // Map transformed coordinates back to the new image
                    int newX = (int)transformedCoords.X;
                    int newY = (int)transformedCoords.Y;
                    if (flippedImage) {
                        newX = width - 1 - newX;
                        newY = height - 1 - newY;
                    }

                    // Ensure we stay within the image bounds
                    if (newX >= 0 && newX < width && newY >= 0 && newY < height) {
                        // Get the interpolated pixel value from the source image data at the transformed coordinates
                        float newPixelValue = GetInterpolatedPixelValue(newX, newY, sourceImageData, width, height);

                        // Set the pixel in the transformed image data
                        transformedImageData[y * width + x] = (ushort)Math.Clamp(newPixelValue * ushort.MaxValue, 0, ushort.MaxValue);
                    }
                }
            }

            return transformedImageData;
        }

        public static bool IsFlippedImage(double[,] affineMatrix) {
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

        private static double[,] ComputeAffineTransformationMatrix(double[,] sourcePoints, double[,] targetPoints) {
            int numPoints = sourcePoints.GetLength(0);
            if (numPoints < 3) {
                throw new ArgumentException("At least 3 points are required for affine transformation.");
            }

            var A = DenseMatrix.OfArray(new double[numPoints * 2, 6]);
            var B = DenseVector.OfArray(new double[numPoints * 2]);

            for (int i = 0; i < numPoints; i++) {
                A[i * 2, 0] = sourcePoints[i, 0];
                A[i * 2, 1] = sourcePoints[i, 1];
                A[i * 2, 2] = 1;
                A[i * 2, 3] = 0;
                A[i * 2, 4] = 0;
                A[i * 2, 5] = 0;

                A[i * 2 + 1, 0] = 0;
                A[i * 2 + 1, 1] = 0;
                A[i * 2 + 1, 2] = 0;
                A[i * 2 + 1, 3] = sourcePoints[i, 0];
                A[i * 2 + 1, 4] = sourcePoints[i, 1];
                A[i * 2 + 1, 5] = 1;

                B[i * 2] = targetPoints[i, 0];
                B[i * 2 + 1] = targetPoints[i, 1];
            }

            // Solve for X using least squares
            var AtA = A.TransposeThisAndMultiply(A); // Equivalent to At * A
            var AtB = A.TransposeThisAndMultiply(B); // Equivalent to At * B
            var X = AtA.Solve(AtB); // Solve the system AtA * X = AtB

            return new double[3, 3] {
                { X[0], X[1], X[2] },
                { X[3], X[4], X[5] },
                { 0, 0, 1 }
            };
        }

        private static Point ApplyAffineMatrix(int x, int y, double[,] matrix) {
            double newX = matrix[0, 0] * x + matrix[0, 1] * y + matrix[0, 2];
            double newY = matrix[1, 0] * x + matrix[1, 1] * y + matrix[1, 2];

            return new Point((float)newX, (float)newY);
        }

        private static float GetInterpolatedPixelValue(int x, int y, float[] imageData, int width, int height) {
            if (x < 0 || y < 0 || x >= width || y >= height) {
                return 0; // Out-of-bounds pixels return 0 (or any appropriate default value)
            }

            // Bilinear interpolation
            int x0 = x;
            int y0 = y;
            int x1 = Math.Min(x0 + 1, width - 1);
            int y1 = Math.Min(y0 + 1, height - 1);

            // Get pixel values for interpolation
            float c00 = imageData[y0 * width + x0];
            float c01 = imageData[y0 * width + x1];
            float c10 = imageData[y1 * width + x0];
            float c11 = imageData[y1 * width + x1];

            float xFraction = x - x0;
            float yFraction = y - y0;

            // Calculate the interpolated pixel value
            float interpolatedValue = (float)(
                c00 * (1 - xFraction) * (1 - yFraction) +
                c01 * xFraction * (1 - yFraction) +
                c10 * (1 - xFraction) * yFraction +
                c11 * xFraction * yFraction
            );

            return interpolatedValue;
        }

        private static float GetInterpolatedPixelValue(int x, int y, ushort[] imageData, int width, int height) {
            if (x < 0 || y < 0 || x >= width || y >= height) {
                return 0; // Out-of-bounds pixels return 0 (or any appropriate default value)
            }

            // Bilinear interpolation
            int x0 = x;
            int y0 = y;
            int x1 = Math.Min(x0 + 1, width - 1);
            int y1 = Math.Min(y0 + 1, height - 1);

            // Get pixel values for interpolation
            ushort c00 = imageData[y0 * width + x0];
            ushort c01 = imageData[y0 * width + x1];
            ushort c10 = imageData[y1 * width + x0];
            ushort c11 = imageData[y1 * width + x1];

            float xFraction = x - x0;
            float yFraction = y - y0;

            // Calculate the interpolated pixel value
            float interpolatedValue = (float)(
                c00 * (1 - xFraction) * (1 - yFraction) +
                c01 * xFraction * (1 - yFraction) +
                c10 * (1 - xFraction) * yFraction +
                c11 * xFraction * yFraction
            );

            return interpolatedValue;
        }

        private static List<Triangle> ComputeTriangleList(List<Point> starList) {
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
                        if (side1 > 0.0) {
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

        private static int[,] ComputeVotingMatrix(int starCount1, int starCount2, List<Triangle> triangles1, List<Triangle> triangles2, double maxTriangleDistance) {
            double distanceSquared = maxTriangleDistance * maxTriangleDistance;
            int[,] votingMatrix = new int[starCount1, starCount2];

            bool isTriangleSet1Smaller = triangles1.Count <= triangles2.Count;
            var smallerTriangleSet = isTriangleSet1Smaller ? triangles1 : triangles2;
            var largerTriangleSet = isTriangleSet1Smaller ? triangles2 : triangles1;

            Parallel.For(0, smallerTriangleSet.Count, smallerIndex => {
                double smallerSide3 = smallerTriangleSet[smallerIndex].Side3;
                double smallerSide2 = smallerTriangleSet[smallerIndex].Side2;

                // Binary search for range in `larger`
                int rangeStart = largerTriangleSet.BinarySearch(new Triangle { Side3 = smallerSide3 - maxTriangleDistance }, new TriangleComparer());
                int rangeEnd = largerTriangleSet.BinarySearch(new Triangle { Side3 = smallerSide3 + maxTriangleDistance }, new TriangleComparer());
                rangeStart = rangeStart < 0 ? ~rangeStart : rangeStart;
                rangeEnd = rangeEnd < 0 ? ~rangeEnd : rangeEnd;

                double minimumDistanceSquared = double.MaxValue;
                int closestTriangleDistance = -1;

                for (int largerIndex = rangeStart; largerIndex < rangeEnd; largerIndex++) {
                    double distanceSquared = (smallerSide3 - largerTriangleSet[largerIndex].Side3) * (smallerSide3 - largerTriangleSet[largerIndex].Side3) + (smallerSide2 - largerTriangleSet[largerIndex].Side2) * (smallerSide2 - largerTriangleSet[largerIndex].Side2);
                    if (distanceSquared < minimumDistanceSquared) {
                        minimumDistanceSquared = distanceSquared;
                        closestTriangleDistance = largerIndex;
                    }
                }

                if (minimumDistanceSquared < distanceSquared && closestTriangleDistance != -1) {
                    var smallerTriangle = smallerTriangleSet[smallerIndex];
                    var largerTriangle = largerTriangleSet[closestTriangleDistance];

                    lock (votingMatrix) {
                        if (isTriangleSet1Smaller) {
                            votingMatrix[smallerTriangle.Index1, largerTriangle.Index1]++;
                            votingMatrix[smallerTriangle.Index2, largerTriangle.Index2]++;
                            votingMatrix[smallerTriangle.Index3, largerTriangle.Index3]++;
                        } else {
                            votingMatrix[largerTriangle.Index1, smallerTriangle.Index1]++;
                            votingMatrix[largerTriangle.Index2, smallerTriangle.Index2]++;
                            votingMatrix[largerTriangle.Index3, smallerTriangle.Index3]++;
                        }
                    }
                }
            });

            return votingMatrix;
        }

        private static List<Match> ComputeMatchList(ref int[,] votingMatrix, double voteThreshold) {
            List<Match> matchList = new List<Match>();

            int matrixSize = Math.Min(votingMatrix.GetLength(0), votingMatrix.GetLength(1));

            double threshold = (matrixSize - 1) * (matrixSize - 2) / voteThreshold;
            threshold = Math.Round(Math.Max(4.0, threshold));

            int bestMatchIndex = 0;
            for (int rowIndex = 0; rowIndex < votingMatrix.GetLength(0); rowIndex++) {
                int maxVotes = 0;
                for (int columnIndex = 0; columnIndex < votingMatrix.GetLength(1); columnIndex++) {
                    if (votingMatrix[rowIndex, columnIndex] > maxVotes) {
                        maxVotes = votingMatrix[rowIndex, columnIndex];
                        bestMatchIndex = columnIndex;
                    }
                }

                if (maxVotes >= threshold) {
                    matchList.Add(new Match { Index1 = rowIndex, Index2 = bestMatchIndex, Votes = maxVotes });
                    for (int columnIndex1 = 0; columnIndex1 < votingMatrix.GetLength(1); columnIndex1++) {
                        votingMatrix[rowIndex, columnIndex1] = 0;
                    }
                    for (int rowIndex1 = 0; rowIndex1 < votingMatrix.GetLength(0); rowIndex1++) {
                        votingMatrix[rowIndex1, bestMatchIndex] = 0;
                    }
                }
            }

            SortMatchArray(ref matchList);

            return matchList;
        }

        private static void SortMatchArray(ref List<Match> m) {
            m = m.AsParallel().OrderByDescending(a => a.Votes).ToList();
        }

        private static void SortTriangleArray(ref List<Triangle> t) {
            t = t.AsParallel().OrderBy(a => a.Side3).ToList();
        }

        private struct Match {
            public int Index1 { get; set; }
            public int Index2 { get; set; }
            public int Votes { get; set; }
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