using NINA.Core.Utility;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace NINA.Plugin.Livestack.Image {

    public class CalibrationManagerSimd : ICalibrationManager {

        internal sealed class CalibrationMaster : IDisposable {

            public CalibrationMaster(CalibrationFrameMeta meta, bool cacheRows) {
                Meta = meta ?? throw new ArgumentNullException(nameof(meta));

                _width = meta.Width;
                _height = meta.Height;

                _imageReader = new CFitsioFITSReader(meta.Path);
                try {
                    if (_imageReader.Width != _width || _imageReader.Height != _height) {
                        throw new ArgumentException("Calibration master dimensions differ from its metadata.", nameof(meta));
                    }
                    _rowLoaded = cacheRows ? new bool[_height] : null;
                    _data = cacheRows ? GC.AllocateUninitializedArray<float>(checked(_width * _height)) : ArrayPool<float>.Shared.Rent(_width);
                } catch {
                    _imageReader.Dispose();
                    throw;
                }
            }

            public CalibrationFrameMeta Meta { get; }

            private readonly CFitsioFITSReader _imageReader;
            private readonly int _width;
            private readonly int _height;

            // A reusable row by default, or a contiguous image when caching is explicitly requested.
            private float[] _data;
            private int _currentRow = -1;

            // Null for streaming mode; otherwise tracks rows loaded into the full image.
            private readonly bool[] _rowLoaded;

            /// <summary>
            /// Returns a read-only view of the requested row.
            /// In streaming mode the view is valid until another row is read or the master is disposed.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ReadOnlySpan<float> ReadPixelRow(int row) {
                ObjectDisposedException.ThrowIf(_data == null, this);
                if ((uint)row >= (uint)_height)
                    throw new ArgumentOutOfRangeException(nameof(row));

                if (_rowLoaded == null) {
                    if (_currentRow != row) {
                        _imageReader.ReadPixelRowAsFloat(row, _data.AsSpan(0, _width));
                        _currentRow = row;
                    }
                    return _data.AsSpan(0, _width);
                }

                int offset = row * _width;

                if (!_rowLoaded[row]) {
                    Span<float> rowSpan = _data.AsSpan(offset, _width);
                    _imageReader.ReadPixelRowAsFloat(row, rowSpan); // no allocation, fills backing store directly
                    _rowLoaded[row] = true;
                }

                return _data.AsSpan(offset, _width);
            }

            public void Dispose() {
                float[] data = _data;
                if (data == null) {
                    return;
                }
                _data = null;
                try {
                    _imageReader.Dispose();
                } finally {
                    if (_rowLoaded == null) {
                        ArrayPool<float>.Shared.Return(data);
                    }
                }
            }
        }

        public IList<CalibrationFrameMeta> FlatLibrary { get; } = new List<CalibrationFrameMeta>();
        public IList<CalibrationFrameMeta> DarkLibrary { get; } = new List<CalibrationFrameMeta>();
        public IList<CalibrationFrameMeta> BiasLibrary { get; } = new List<CalibrationFrameMeta>();
        private readonly Dictionary<CalibrationFrameMeta, CalibrationMaster> masterCache = new Dictionary<CalibrationFrameMeta, CalibrationMaster>();

        private readonly bool cacheMasterRows;

        public CalibrationManagerSimd() : this(false) {
        }

        public CalibrationManagerSimd(bool cacheMasterRows) {
            this.cacheMasterRows = cacheMasterRows;
        }

        public void RegisterBiasMaster(CalibrationFrameMeta calibrationFrameMeta) {
            if (!BiasLibrary.Any(x => x.Equals(calibrationFrameMeta))) {
                BiasLibrary.Add(calibrationFrameMeta);
            }
        }

        public void RegisterDarkMaster(CalibrationFrameMeta calibrationFrameMeta) {
            if (!DarkLibrary.Any(x => x.Equals(calibrationFrameMeta))) {
                DarkLibrary.Add(calibrationFrameMeta);
            }
        }

        public void RegisterFlatMaster(CalibrationFrameMeta calibrationFrameMeta) {
            if (!FlatLibrary.Any(x => x.Equals(calibrationFrameMeta))) {
                FlatLibrary.Add(calibrationFrameMeta);
            }
        }

        private CalibrationMaster GetBiasMaster(int width, int height, int gain, int offset) {
            CalibrationFrameMeta meta =
                BiasLibrary.FirstOrDefault(x => x.Gain == gain && x.Offset == offset && x.Width == width && x.Height == height)
                ?? BiasLibrary.FirstOrDefault(x => x.Gain == gain && x.Offset == -1 && x.Width == width && x.Height == height)
                ?? BiasLibrary.FirstOrDefault(x => x.Gain == -1 && x.Offset == offset && x.Width == width && x.Height == height)
                ?? BiasLibrary.FirstOrDefault(x => x.Gain == -1 && x.Offset == -1 && x.Width == width && x.Height == height);
            return GetOrCreateMaster(meta);
        }

        private CalibrationMaster GetDarkMaster(int width, int height, double exposureTime, int gain, int offset) {
            CalibrationFrameMeta meta =
                DarkLibrary.FirstOrDefault(x => x.Gain == gain && x.Offset == offset && x.ExposureTime == exposureTime && x.Width == width && x.Height == height)
                ?? DarkLibrary.FirstOrDefault(x => x.Gain == gain && x.Offset == -1 && x.Width == width && x.Height == height)
                ?? DarkLibrary.FirstOrDefault(x => x.Gain == -1 && x.Offset == offset && x.Width == width && x.Height == height)
                ?? DarkLibrary.FirstOrDefault(x => x.Gain == -1 && x.Offset == -1 && x.Width == width && x.Height == height);
            return GetOrCreateMaster(meta);
        }

        private CalibrationMaster GetFlatMaster(int width, int height, string inFilter) {
            string filter = string.IsNullOrWhiteSpace(inFilter) ? LiveStackBag.NOFILTER : inFilter;
            CalibrationFrameMeta meta = FlatLibrary.FirstOrDefault(x => x.Filter == filter && x.Width == width && x.Height == height);
            return GetOrCreateMaster(meta);
        }

        private CalibrationMaster GetOrCreateMaster(CalibrationFrameMeta meta) {
            if (meta == null) {
                return null;
            }
            if (!masterCache.TryGetValue(meta, out CalibrationMaster master)) {
                master = new CalibrationMaster(meta, cacheMasterRows);
                masterCache.Add(meta, master);
            }
            return master;
        }

        public float[] ApplyLightFrameCalibrationInPlace(
            CFitsioFITSReader image,
            int width,
            int height,
            double exposureTime,
            int gain,
            int offset,
            string inFilter,
            bool isBayered) {
            float[] output = GC.AllocateUninitializedArray<float>(AffineResampler.GetLength(width, height));
            ApplyLightFrameCalibrationInto(image, output, width, height, exposureTime, gain, offset, inFilter, isBayered);
            return output;
        }

        public void ApplyLightFrameCalibrationInto(CFitsioFITSReader image, float[] imageArray, int width, int height, double exposureTime, int gain, int offset, string inFilter, bool isBayered, CancellationToken token = default) {
            ValidateDestination(image, imageArray, width, height, token);
            CalibrationMaster bias = LivestackMediator.Plugin.UseBiasForLights ? GetBiasMaster(width, height, gain, offset) : null;
            CalibrationMaster dark = GetDarkMaster(width, height, exposureTime, gain, offset);
            CalibrationMaster flat = GetFlatMaster(width, height, inFilter);
            CalibrateFrame(image, imageArray, width, height, bias, dark, flat, token);
        }

        public float[] ApplyFlatFrameCalibrationInPlace(
            CFitsioFITSReader image,
            int width,
            int height,
            double exposureTime,
            int gain,
            int offset,
            string inFilter,
            bool isBayered) {
            float[] output = GC.AllocateUninitializedArray<float>(AffineResampler.GetLength(width, height));
            ApplyFlatFrameCalibrationInto(image, output, width, height, exposureTime, gain, offset, inFilter, isBayered);
            return output;
        }

        public void ApplyFlatFrameCalibrationInto(CFitsioFITSReader image, float[] imageArray, int width, int height, double exposureTime, int gain, int offset, string inFilter, bool isBayered, CancellationToken token = default) {
            ValidateDestination(image, imageArray, width, height, token);
            CalibrationMaster bias = GetBiasMaster(width, height, gain, offset);
            CalibrationMaster dark = bias == null ? GetDarkMaster(width, height, exposureTime, gain, offset) : null;
            CalibrateFrame(image, imageArray, width, height, bias, dark, null, token);
        }

        private static void ValidateDestination(CFitsioFITSReader image, float[] imageArray, int width, int height, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(imageArray);
            if (imageArray.Length != AffineResampler.GetLength(width, height) || image.Width != width || image.Height != height) {
                throw new ArgumentException("Calibration buffers and FITS dimensions must agree.");
            }
        }

        private static void CalibrateFrame(CFitsioFITSReader image, float[] imageArray, int width, int height, CalibrationMaster bias, CalibrationMaster dark, CalibrationMaster flat, CancellationToken token) {
            StringBuilder sb = new StringBuilder();
            sb.Append($"Calibrating \"{image.FilePath}\";");
            if (bias != null) sb.Append($" using bias \"{bias.Meta.Path}\";");
            if (dark != null) sb.Append($" using dark \"{dark.Meta.Path}\";");
            if (flat != null) sb.Append($" using flat \"{flat.Meta.Path}\";");
            Logger.Info(sb.ToString());

            float flatMean = flat != null ? (float)flat.Meta.Mean : 1f;
            for (int row = 0; row < height; row++) {
                token.ThrowIfCancellationRequested();
                Span<float> pixels = imageArray.AsSpan(row * width, width);
                image.ReadPixelRowAsFloat(row, pixels);
                CalibrateRow(pixels,
                    bias != null ? bias.ReadPixelRow(row) : default,
                    dark != null ? dark.ReadPixelRow(row) : default,
                    flat != null ? flat.ReadPixelRow(row) : default,
                    flatMean);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CalibrateRow(
            Span<float> pixels,
            ReadOnlySpan<float> bias,
            ReadOnlySpan<float> dark,
            ReadOnlySpan<float> flat,
            float flatMean) {
            int n = pixels.Length;
            bool hasBias = !bias.IsEmpty;
            bool hasDark = !dark.IsEmpty;
            bool hasFlat = !flat.IsEmpty;
            int simd = Vector<float>.Count;
            int iVec = 0;
            int last = Vector.IsHardwareAccelerated ? n - (n % simd) : 0;

            var vZero = Vector<float>.Zero;
            var vOne = new Vector<float>(1f);
            var vMean = hasFlat ? new Vector<float>(flatMean) : default;

            for (; iVec < last; iVec += simd) {
                var v = new Vector<float>(pixels.Slice(iVec, simd));

                if (hasBias) v -= new Vector<float>(bias.Slice(iVec, simd));
                if (hasDark) v -= new Vector<float>(dark.Slice(iVec, simd));

                // clamp [0,1]
                v = Vector.Min(vOne, Vector.Max(vZero, v));

                if (hasFlat) {
                    var f = new Vector<float>(flat.Slice(iVec, simd));
                    v *= (vMean / f);
                }

                v.CopyTo(pixels.Slice(iVec, simd));
            }

            // Also handles the whole row when SIMD is unavailable or the row is too short.
            for (int i = iVec; i < n; i++) {
                float v = pixels[i];
                if (hasBias) v -= bias[i];
                if (hasDark) v -= dark[i];

                if (v < 0f) v = 0f;
                else if (v > 1f) v = 1f;

                if (hasFlat) v *= flatMean / flat[i];

                pixels[i] = v;
            }
        }

        public void Dispose() {
            foreach (var item in masterCache) {
                try {
                    item.Value.Dispose();
                } catch { }
            }
            masterCache.Clear();
            BiasLibrary.Clear();
            DarkLibrary.Clear();
            FlatLibrary.Clear();
        }
    }
}