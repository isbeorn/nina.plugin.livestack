using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;

namespace NINA.Plugin.Livestack.Image {
    /// <summary>Bilinear inverse mapping shared by previews and fused stack accumulation.</summary>
    internal static partial class AffineResampler {
        internal static int GetLength(int width, int height) {
            if (width <= 0 || height <= 0) {
                throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
            }
            return checked(width * height);
        }

        internal static void Transform<TSource, TDestination>(TSource[] source, TDestination[] destination, int width, int height, double[,] matrix, bool flipped)
                where TSource : unmanaged, INumberBase<TSource>
                where TDestination : unmanaged, INumberBase<TDestination> {
            Validate(source, destination, width, height, matrix);
            double outputScale = typeof(TDestination) == typeof(ushort) ? ushort.MaxValue : 1;
            ProcessRows(width, height, y => {
                // Keep the original operation order so subpixel coordinates and borders round identically.
                double a = matrix[0, 0], by = matrix[0, 1] * y, tx = matrix[0, 2];
                double c = matrix[1, 0], dy = matrix[1, 1] * y, ty = matrix[1, 2];
                for (int x = 0; x < width; x++) {
                    if (typeof(TDestination) == typeof(float) && x <= width - 4
                        && TrySampleFour(source, width, height, x, a, by, tx, c, dy, ty, flipped, out Vector128<float> values)) {
                        values.CopyTo(((float[])(object)destination).AsSpan(y * width + x));
                        x += 3;
                        continue;
                    }
                    bool valid = TrySample(source, width, height, a * x + by + tx, c * x + dy + ty, flipped, out float value);
                    destination[y * width + x] = valid ? TDestination.CreateSaturating(value * outputScale) : TDestination.Zero;
                }
            });
        }

        internal static void Accumulate<TSource>(TSource[] source, float[] stack, uint[] counts, int uniformCount, int width, int height, double[,] matrix, bool flipped)
                where TSource : unmanaged, INumberBase<TSource> {
            Validate(source, stack, width, height, matrix);
            if (counts == null && uniformCount < 1) {
                throw new ArgumentOutOfRangeException(nameof(uniformCount));
            }
            if (counts != null && (counts.Length != stack.Length || Array.IndexOf(counts, uint.MaxValue) >= 0)) {
                throw new ArgumentException("Contribution counts have invalid length or would overflow.", nameof(counts));
            }
            AccumulateValidated(source, stack, counts, uniformCount, width, height, matrix, flipped);
        }

        // Callers validate buffers, geometry and count capacity before this noncancelable write.
        internal static void AccumulateValidated<TSource, TCount>(TSource[] source, float[] stack, TCount[] counts, int uniformCount, int width, int height, double[,] matrix, bool flipped)
                where TSource : unmanaged, INumberBase<TSource>
                where TCount : unmanaged, INumberBase<TCount> {
            ProcessRows(width, height, y => {
                // Hoist row-invariant work without accumulating coordinate drift across the row.
                double a = matrix[0, 0], by = matrix[0, 1] * y, tx = matrix[0, 2];
                double c = matrix[1, 0], dy = matrix[1, 1] * y, ty = matrix[1, 2];
                for (int x = 0; x < width; x++) {
                    if (x <= width - 4 && TrySampleFour(source, width, height, x, a, by, tx, c, dy, ty, flipped, out Vector128<float> values)) {
                        AccumulateFour(stack, counts, uniformCount, y * width + x, values);
                        x += 3;
                        continue;
                    }
                    if (!TrySample(source, width, height, a * x + by + tx, c * x + dy + ty, flipped, out float value)) {
                        continue;
                    }
                    int index = y * width + x;
                    uint count = counts == null ? (uint)uniformCount : uint.CreateChecked(counts[index]);
                    stack[index] = count == 0 ? value : (float)(stack[index] + ((double)value - stack[index]) / (count + 1d));
                    if (counts != null) {
                        counts[index] = TCount.CreateChecked(count + 1);
                    }
                }
            });
        }

        internal static void Validate(Array source, Array destination, int width, int height, double[,] matrix) {
            int length = GetLength(width, height);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);
            if (source.Length != length || destination.Length != length || ReferenceEquals(source, destination)) {
                throw new ArgumentException("Source and destination must be distinct buffers matching the frame dimensions.");
            }
            if (!AlignmentGeometry.IsFiniteAffine(matrix)) {
                throw new ArgumentException("Expected a finite 3x3 affine matrix.", nameof(matrix));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySample<T>(T[] source, int width, int height, double sourceX, double sourceY, bool flipped, out float value)
                where T : unmanaged, INumberBase<T> {
            if (flipped) {
                sourceX = width - 1 - sourceX;
                sourceY = height - 1 - sourceY;
            }
            value = 0;
            // Tolerate solver roundoff at an exact border, but never truncate a missing sample into the image.
            const double epsilon = 1e-7;
            if (!double.IsFinite(sourceX) || !double.IsFinite(sourceY)
                || sourceX < -epsilon || sourceX > width - 1 + epsilon || sourceY < -epsilon || sourceY > height - 1 + epsilon) {
                return false;
            }
            sourceX = Math.Clamp(sourceX, 0, width - 1);
            sourceY = Math.Clamp(sourceY, 0, height - 1);
            int x0 = (int)Math.Floor(sourceX), y0 = (int)Math.Floor(sourceY);
            int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
            double fx = sourceX - x0, fy = sourceY - y0;
            double top = Interpolate(double.CreateChecked(source[y0 * width + x0]), double.CreateChecked(source[y0 * width + x1]), fx);
            double bottom = Interpolate(double.CreateChecked(source[y1 * width + x0]), double.CreateChecked(source[y1 * width + x1]), fx);
            double sample = Interpolate(top, bottom, fy);
            value = (float)(typeof(T) == typeof(ushort) ? sample / ushort.MaxValue : sample);
            return float.IsFinite(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Interpolate(double first, double second, double fraction) {
            return fraction == 0 ? first : first + (second - first) * fraction;
        }

        private static void ProcessRows(int width, int height, Action<int> processRow) {
            if ((long)width * height < 262144) {
                for (int row = 0; row < height; row++) {
                    processRow(row);
                }
            } else {
                Parallel.For(0, height, processRow);
            }
        }
    }
}
