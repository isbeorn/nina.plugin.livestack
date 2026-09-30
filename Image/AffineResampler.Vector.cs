using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace NINA.Plugin.Livestack.Image {
    internal static partial class AffineResampler {
        // Portable vector APIs provide their own software fallback. Keep double intermediates
        // and round sampled pixels to float before averaging, just like the scalar path.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySampleFour<T>(T[] source, int width, int height, int x,
                double a, double by, double tx, double c, double dy, double ty, bool flipped, out Vector128<float> values)
                where T : unmanaged, INumberBase<T> {
            Vector256<double> columns = Vector256.Create((double)x) + Vector256.Create(0d, 1d, 2d, 3d);
            Vector256<double> sx = Vector256.Create(a) * columns + Vector256.Create(by) + Vector256.Create(tx);
            Vector256<double> sy = Vector256.Create(c) * columns + Vector256.Create(dy) + Vector256.Create(ty);
            if (flipped) {
                sx = Vector256.Create(width - 1d) - sx;
                sy = Vector256.Create(height - 1d) - sy;
            }
            values = default;
            // Borders retain the scalar roundoff/clamping policy. Only gather fully interior blocks.
            if (!Vector256.GreaterThanOrEqualAll(sx, Vector256<double>.Zero)
                || !Vector256.LessThanAll(sx, Vector256.Create(width - 1d))
                || !Vector256.GreaterThanOrEqualAll(sy, Vector256<double>.Zero)
                || !Vector256.LessThanAll(sy, Vector256.Create(height - 1d))) {
                return false;
            }
            Vector256<double> left = Vector256.Floor(sx), top = Vector256.Floor(sy);
            Vector256<double> fx = sx - left, fy = sy - top;
            int i0 = (int)top.GetElement(0) * width + (int)left.GetElement(0);
            int i1 = (int)top.GetElement(1) * width + (int)left.GetElement(1);
            int i2 = (int)top.GetElement(2) * width + (int)left.GetElement(2);
            int i3 = (int)top.GetElement(3) * width + (int)left.GetElement(3);
            Vector256<double> upper = Interpolate(ReadFour(source, i0, i1, i2, i3), ReadFour(source, i0 + 1, i1 + 1, i2 + 1, i3 + 1), fx);
            Vector256<double> lower = Interpolate(ReadFour(source, i0 + width, i1 + width, i2 + width, i3 + width),
                ReadFour(source, i0 + width + 1, i1 + width + 1, i2 + width + 1, i3 + width + 1), fx);
            Vector256<double> sample = Interpolate(upper, lower, fy);
            if (typeof(T) == typeof(ushort)) {
                sample /= Vector256.Create((double)ushort.MaxValue);
            }
            values = Vector256.Narrow(sample, Vector256<double>.Zero).GetLower();
            // A nonfinite lane needs the scalar per-pixel skip policy, including its count handling.
            return Vector128.LessThanOrEqualAll(Vector128.Abs(values), Vector128.Create(float.MaxValue));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<double> ReadFour<T>(T[] source, int i0, int i1, int i2, int i3)
                where T : unmanaged, INumberBase<T> {
            if (typeof(T) == typeof(float)) {
                float[] pixels = (float[])(object)source;
                return Vector256.WidenLower(Vector128.Create(pixels[i0], pixels[i1], pixels[i2], pixels[i3]).ToVector256());
            }
            return Vector256.Create(double.CreateChecked(source[i0]), double.CreateChecked(source[i1]),
                double.CreateChecked(source[i2]), double.CreateChecked(source[i3]));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<double> Interpolate(Vector256<double> first, Vector256<double> second, Vector256<double> fraction) {
            return Vector256.ConditionalSelect(Vector256.Equals(fraction, Vector256<double>.Zero), first, first + (second - first) * fraction);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AccumulateFour<TCount>(float[] stack, TCount[] counts, int uniformCount, int index, Vector128<float> values)
                where TCount : unmanaged, INumberBase<TCount> {
            Vector256<double> previous = Vector256.WidenLower(Vector128.Create<float>(stack.AsSpan(index)).ToVector256());
            Vector256<double> sampled = Vector256.WidenLower(values.ToVector256());
            Vector256<double> count = counts == null ? Vector256.Create((double)uniformCount) : ReadFour(counts, index, index + 1, index + 2, index + 3);
            Vector256<double> next = count + Vector256.Create(1d);
            Vector256<double> average = previous + (sampled - previous) / next;
            average = Vector256.ConditionalSelect(Vector256.Equals(count, Vector256<double>.Zero), sampled, average);
            Vector256.Narrow(average, Vector256<double>.Zero).GetLower().CopyTo(stack.AsSpan(index));
            if (counts != null) {
                counts[index] = TCount.CreateChecked(next.GetElement(0));
                counts[index + 1] = TCount.CreateChecked(next.GetElement(1));
                counts[index + 2] = TCount.CreateChecked(next.GetElement(2));
                counts[index + 3] = TCount.CreateChecked(next.GetElement(3));
            }
        }
    }
}
