using NINA.Core.Utility;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Documents;
using System.Windows.Media.Media3D;
using static NINA.Image.FileFormat.FITS.CfitsioNative;

namespace NINA.Plugin.Livestack {

    public class CFitsioFITSReader : IDisposable {
        private nint filePtr;
        private string tempFile;

        public CFitsioFITSReader(string filePath) {
            CFitsioExtensions.fits_open_diskfile(out filePtr, filePath, (int)CfitsioNative.IOMODE.READONLY, out var status);
            CfitsioNative.CheckStatus("fits_open_diskfile", status);

            try {
                CfitsioNative.fits_read_key_long(filePtr, "NAXIS1");
            } catch {
                // When NAXIS1 does not exist, try at the last HDU - e.g. when the image is tile compressed
                CfitsioNative.fits_get_num_hdus(filePtr, out int hdunum, out status);
                CfitsioNative.CheckStatus("fits_get_num_hdus", status);
                if (hdunum > 1) {
                    CfitsioNative.fits_movabs_hdu(filePtr, hdunum, out var hdutypenow, out status);
                    CfitsioNative.CheckStatus("fits_movabs_hdu", status);
                }
            }

            var compressionFlag = CfitsioNative.fits_is_compressed_image(filePtr, out status);
            CfitsioNative.CheckStatus("fits_is_compressed_image", status);
            if (compressionFlag > 0) {
                // When the image is compresse, we decompress it into a temporary file
                tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".fits");
                CFitsioExtensions.fits_create_diskfile(out var ptr, tempFile, out status);
                CfitsioNative.CheckStatus("fits_create_file", status);

                CfitsioNative.fits_img_decompress(filePtr, ptr, out status);
                CfitsioNative.CheckStatus("fits_img_decompress", status);

                // Free resources for current file
                if (filePtr != IntPtr.Zero) {
                    CfitsioNative.fits_close_file(filePtr, out status);
                    CfitsioNative.CheckStatus("fits_close_file", status);
                    CfitsioNative.fits_close_file(ptr, out status);
                    CfitsioNative.CheckStatus("fits_close_file", status);
                }

                CFitsioExtensions.fits_open_diskfile(out filePtr, tempFile, (int)CfitsioNative.IOMODE.READONLY, out status);
                CfitsioNative.CheckStatus("fits_open_diskfile", status);
            }

            var dimensions = CfitsioNative.fits_read_key_long(filePtr, "NAXIS");
            if (dimensions > 2) {
                throw new InvalidOperationException("Reading debayered FITS images not supported.");
            }

            Width = (int)CfitsioNative.fits_read_key_long(filePtr, "NAXIS1");
            Height = (int)CfitsioNative.fits_read_key_long(filePtr, "NAXIS2");
            BitPix = (CfitsioNative.BITPIX)(int)CfitsioNative.fits_read_key_long(filePtr, "BITPIX");
            try {
                DataMin = CfitsioNative.fits_read_key_double(filePtr, "DATAMIN");
            } catch { }
            try {
                DataMax = CfitsioNative.fits_read_key_double(filePtr, "DATAMAX");
            } catch { }
            FilePath = filePath;
        }

        public int Width { get; }
        public int Height { get; }
        public BITPIX BitPix { get; }
        public string FilePath { get; }
        public double? DataMin { get; }
        public double? DataMax { get; }

        private T[] ReadPixelRow<T>(int row) {
            const int nelem = 2;
            var firstpix = new int[nelem] { 1, row + 1 };

            var datatype = GetDataType(typeof(T));

            unsafe {
                var resultBuffer = new T[Width];

                var nulVal = default(T);
                var nulValRef = &nulVal;
                fixed (T* fixedBuffer = resultBuffer) {
                    var result = CfitsioNative.fits_read_pix(filePtr, datatype, firstpix, Width, (IntPtr)nulValRef, (IntPtr)fixedBuffer, out var nullCount, out var status);
                    CheckStatus("fits_read_pix", status);
                }
                return resultBuffer;
            }
        }

        [Obsolete("Use ReadPixelRowAsFloat(int row, Span<float> destination) instead for better performance and less memory allocations.")]
        public float[] ReadPixelRowAsFloat(int row) {
            if (BitPix == BITPIX.BYTE_IMG) {
                var pixels = ReadPixelRow<byte>(row);
                return ToFloatArray(pixels);
            } else if (BitPix == BITPIX.DOUBLE_IMG) {
                var pixels = ReadPixelRow<double>(row);
                return Normalize(ToFloatArray(pixels));
            } else if (BitPix == BITPIX.FLOAT_IMG) {
                var pixels = ReadPixelRow<float>(row);
                return Normalize(pixels);
            } else if (BitPix == BITPIX.LONGLONG_IMG) {
                var pixels = ReadPixelRow<long>(row);
                return ToFloatArray(pixels);
            } else if (BitPix == BITPIX.LONG_IMG) {
                var pixels = ReadPixelRow<int>(row);
                return ToFloatArray(pixels);
            } else if (BitPix == BITPIX.SHORT_IMG) {
                var pixels = ReadPixelRow<ushort>(row);
                return ToFloatArray(pixels);
            } else {
                throw new ArgumentException($"Invalid BITPIX {BitPix}");
            }
        }

        private float[] Normalize(float[] data) {
            if (DataMin != null && DataMax != null && DataMin < DataMax) {
                var range = DataMax.Value - DataMin.Value;
                for (int i = 0; i < data.Length; i++) {
                    data[i] = (float)((data[i] - DataMin.Value) / range);
                }
            }
            return data;
        }

        public float[] ReadAllPixelsAsFloat() {
            var naxes = 2;
            var nelem = Width * Height;
            if (BitPix == BITPIX.BYTE_IMG) {
                var pixels = read_pixels<byte>(filePtr, naxes, nelem);
                return ToFloatArray(pixels);
            } else if (BitPix == BITPIX.DOUBLE_IMG) {
                var pixels = read_pixels<double>(filePtr, naxes, nelem);
                return Normalize(ToFloatArray(pixels));
            } else if (BitPix == BITPIX.FLOAT_IMG) {
                var pixels = read_pixels<float>(filePtr, naxes, nelem);
                return Normalize(pixels);
            } else if (BitPix == BITPIX.LONGLONG_IMG) {
                var pixels = read_pixels<long>(filePtr, naxes, nelem);
                return ToFloatArray(pixels);
            } else if (BitPix == BITPIX.LONG_IMG) {
                var pixels = read_pixels<int>(filePtr, naxes, nelem);
                return ToFloatArray(pixels);
            } else if (BitPix == BITPIX.SHORT_IMG) {
                var pixels = read_pixels<ushort>(filePtr, naxes, nelem);
                return ToFloatArray(pixels);
            } else {
                throw new ArgumentException($"Invalid BITPIX {BitPix}");
            }
        }

        private static float[] ToFloatArray(byte[] src) {
            float[] pixels = new float[src.Length];
            for (int i = 0; i < src.Length; i++) {
                pixels[i] = src[i] / (float)byte.MaxValue;
            }
            return pixels;
        }

        private static float[] ToFloatArray(double[] src) {
            float[] pixels = new float[src.Length];
            for (int i = 0; i < src.Length; i++) {
                pixels[i] = (float)src[i];
            }
            return pixels;
        }

        private static float[] ToFloatArray(ushort[] src) {
            float[] pixels = new float[src.Length];
            for (int i = 0; i < src.Length; i++) {
                pixels[i] = (float)(src[i] / (double)ushort.MaxValue);
            }
            return pixels;
        }

        private static float[] ToFloatArray(long[] src) {
            float[] pixels = new float[src.Length];
            for (int i = 0; i < src.Length; i++) {
                pixels[i] = (float)(((double)src[i] - long.MinValue) / ((double)long.MaxValue - long.MinValue));
            }
            return pixels;
        }

        private static float[] ToFloatArray(int[] src) {
            float[] pixels = new float[src.Length];
            for (int i = 0; i < src.Length; i++) {
                pixels[i] = (float)(((double)src[i] - int.MinValue) / ((double)int.MaxValue - int.MinValue));
            }
            return pixels;
        }

        public double ReadDoubleHeader(string keyname) {
            return CFitsioExtensions.fits_read_key_double(filePtr, keyname);
        }

        public float ReadFloatHeader(string keyname) {
            return CFitsioExtensions.fits_read_key_float(filePtr, keyname);
        }

        public FITSHeader ReadHeader() {
            FITSHeader header = new FITSHeader(Width, Height);
            CfitsioNative.fits_get_hdrspace(filePtr, out var numKeywords, out var numMoreKeywords, out var status);
            CfitsioNative.CheckStatus("fits_get_hdrspace", status);
            for (int headerIdx = 1; headerIdx <= numKeywords; ++headerIdx) {
                CfitsioNative.fits_read_keyn(filePtr, headerIdx, out var keyName, out var keyValue, out var keyComment);

                if (string.IsNullOrEmpty(keyValue) || keyName.Equals("COMMENT") || keyName.Equals("HISTORY")) {
                    continue;
                }

                if (keyValue.Equals("T")) {
                    header.Add(keyName, true, keyComment);
                } else if (keyValue.Equals("F")) {
                    header.Add(keyName, false, keyComment);
                } else if (keyValue.StartsWith("'")) {
                    // Treat as a string
                    keyValue = $"{keyValue.TrimStart('\'').TrimEnd('\'', ' ').Replace(@"''", @"'")}";
                    header.Add(keyName, keyValue, keyComment);
                } else if (keyValue.Contains(".")) {
                    if (double.TryParse(keyValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                        header.Add(keyName, value, keyComment);
                    }
                } else {
                    if (int.TryParse(keyValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) {
                        header.Add(keyName, value, keyComment);
                    } else {
                        // Treat as a string
                        keyValue = $"{keyValue.TrimStart('\'').TrimEnd('\'', ' ').Replace(@"''", @"'")}";
                        header.Add(keyName, keyValue, keyComment);
                    }
                }
            }
            return header;
        }

        public static DATATYPE GetDataType(Type T) {
            if (T == typeof(ushort)) {
                return DATATYPE.TUSHORT;
            }

            if (T == typeof(uint)) {
                return DATATYPE.TUINT;
            }

            if (T == typeof(int)) {
                return DATATYPE.TINT;
            }

            if (T == typeof(short)) {
                return DATATYPE.TSHORT;
            }

            if (T == typeof(float)) {
                return DATATYPE.TFLOAT;
            }

            if (T == typeof(double)) {
                return DATATYPE.TDOUBLE;
            }

            throw new ArgumentException("Invalid cfitsio data type " + T.Name);
        }

        internal ImageMetaData RestoreMetaData() {
            //Translate CFITSio into N.I.N.A. FITSHeader
            FITSHeader header = new FITSHeader(Width, Height);
            CfitsioNative.fits_get_hdrspace(filePtr, out var numKeywords, out var numMoreKeywords, out var status);
            CfitsioNative.CheckStatus("fits_get_hdrspace", status);
            for (int headerIdx = 1; headerIdx <= numKeywords; ++headerIdx) {
                CfitsioNative.fits_read_keyn(filePtr, headerIdx, out var keyName, out var keyValue, out var keyComment);

                if (string.IsNullOrEmpty(keyValue) || keyName.Equals("COMMENT") || keyName.Equals("HISTORY")) {
                    continue;
                }

                if (keyValue.Equals("T")) {
                    header.Add(keyName, true, keyComment);
                } else if (keyValue.Equals("F")) {
                    header.Add(keyName, false, keyComment);
                } else if (keyValue.StartsWith("'")) {
                    // Treat as a string
                    keyValue = $"{keyValue.TrimStart('\'').TrimEnd('\'', ' ').Replace(@"''", @"'")}";
                    header.Add(keyName, keyValue, keyComment);
                } else if (keyValue.Contains(".")) {
                    if (double.TryParse(keyValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                        header.Add(keyName, value, keyComment);
                    }
                } else {
                    if (int.TryParse(keyValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) {
                        header.Add(keyName, value, keyComment);
                    } else {
                        // Treat as a string
                        keyValue = $"{keyValue.TrimStart('\'').TrimEnd('\'', ' ').Replace(@"''", @"'")}";
                        header.Add(keyName, keyValue, keyComment);
                    }
                }
            }

            var metaData = new ImageMetaData();
            try {
                metaData = header.ExtractMetaData();
            } catch (Exception ex) {
                Logger.Error(ex.Message);
            }
            return metaData;
        }

        private bool _disposed = false;

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (!_disposed) {
                if (disposing) {
                }

                if (filePtr != IntPtr.Zero) {
                    CfitsioNative.fits_close_file(filePtr, out var status);
                    filePtr = IntPtr.Zero;
                }

                if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile)) {
                    try {
                        File.Delete(tempFile);
                        tempFile = null; // Clear reference after deletion
                    } catch (Exception ex) {
                        // Log the exception or handle it as needed
                        Logger.Error($"Failed to delete temp file", ex);
                    }
                }

                _disposed = true;
            }
        }

        public void ReadPixelRowAsFloat(int row, Span<float> destination) {
            if ((uint)row >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(row));
            if (destination.Length < Width) throw new ArgumentException("Destination span too small.", nameof(destination));

            destination = destination.Slice(0, Width);

            switch (BitPix) {
                case BITPIX.FLOAT_IMG: {
                        ReadPixelRow(row, destination);
                        NormalizeSpanInPlace(destination);
                        return;
                    }

                case BITPIX.DOUBLE_IMG: {
                        double[] tmp = ArrayPool<double>.Shared.Rent(Width);
                        try {
                            var tmpRow = tmp.AsSpan(0, Width);
                            ReadPixelRow(row, tmpRow);
                            ConvertDoubleToFloat_Exact(tmpRow, destination);
                            NormalizeSpanInPlace(destination);
                        } finally { ArrayPool<double>.Shared.Return(tmp); }
                        return;
                    }

                case BITPIX.BYTE_IMG: {
                        byte[] tmp = ArrayPool<byte>.Shared.Rent(Width);
                        try {
                            var tmpRow = tmp.AsSpan(0, Width);
                            ReadPixelRow(row, tmpRow);
                            ConvertByteToFloat_Exact(tmpRow, destination);
                        } finally { ArrayPool<byte>.Shared.Return(tmp); }
                        return;
                    }

                case BITPIX.SHORT_IMG: {
                        ushort[] tmp = ArrayPool<ushort>.Shared.Rent(Width);
                        try {
                            var tmpRow = tmp.AsSpan(0, Width);
                            ReadPixelRow(row, tmpRow);
                            ConvertUShortToFloat_Exact(tmpRow, destination);
                        } finally { ArrayPool<ushort>.Shared.Return(tmp); }
                        return;
                    }

                case BITPIX.LONG_IMG: {
                        int[] tmp = ArrayPool<int>.Shared.Rent(Width);
                        try {
                            var tmpRow = tmp.AsSpan(0, Width);
                            ReadPixelRow(row, tmpRow);
                            ConvertIntToFloat_Exact(tmpRow, destination);
                        } finally { ArrayPool<int>.Shared.Return(tmp); }
                        return;
                    }

                case BITPIX.LONGLONG_IMG: {
                        long[] tmp = ArrayPool<long>.Shared.Rent(Width);
                        try {
                            var tmpRow = tmp.AsSpan(0, Width);
                            ReadPixelRow(row, tmpRow);
                            ConvertLongToFloat_Exact(tmpRow, destination);
                        } finally { ArrayPool<long>.Shared.Return(tmp); }
                        return;
                    }

                default:
                    throw new ArgumentException($"Invalid BITPIX {BitPix}", nameof(BitPix));
            }
        }

        private unsafe void ReadPixelRow<T>(int row, Span<T> destination) where T : unmanaged {
            if ((uint)row >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(row));
            if (destination.Length < Width) throw new ArgumentException("Destination span too small.", nameof(destination));

            destination = destination.Slice(0, Width);

            int* firstpix = stackalloc int[2];
            firstpix[0] = 1;
            firstpix[1] = row + 1;

            DATATYPE datatype = GetDataType(typeof(T));

            T nulVal = default;
            nint nulPtr = (nint)(&nulVal);

            fixed (T* dstPtr = destination) {
                int anynul;
                int status;

                int result = CFitsioExtensions.fits_read_pix(
                    filePtr,
                    datatype,
                    (IntPtr)firstpix,
                    Width,
                    nulPtr,
                    (nint)dstPtr,
                    out anynul,
                    out status);

                CheckStatus("fits_read_pix", status);
            }
        }

        private static void ConvertByteToFloat_Exact(ReadOnlySpan<byte> src, Span<float> dst) {
            if (dst.Length < src.Length) throw new ArgumentException("dst too small", nameof(dst));
            float denom = (float)byte.MaxValue; // 255f
            for (int i = 0; i < src.Length; i++)
                dst[i] = src[i] / denom;
        }

        // ushort: pixels[i] = (float)(src[i] / (double)ushort.MaxValue);
        private static void ConvertUShortToFloat_Exact(ReadOnlySpan<ushort> src, Span<float> dst) {
            if (dst.Length < src.Length) throw new ArgumentException("dst too small", nameof(dst));
            double denom = (double)ushort.MaxValue; // 65535d
            for (int i = 0; i < src.Length; i++)
                dst[i] = (float)(src[i] / denom);
        }

        // int: pixels[i] = (float)(((double)src[i] - int.MinValue) / ((double)int.MaxValue - int.MinValue));
        private static void ConvertIntToFloat_Exact(ReadOnlySpan<int> src, Span<float> dst) {
            if (dst.Length < src.Length) throw new ArgumentException("dst too small", nameof(dst));

            const double range = (double)int.MaxValue - (double)int.MinValue; // 2^32 - 1
            const double invRange = 1.0 / range;
            const double offset = -(double)int.MinValue * invRange;

            int i = 0;
            int last = src.Length - (src.Length % 4);

            for (; i < last; i += 4) {
                dst[i + 0] = (float)((double)src[i + 0] * invRange + offset);
                dst[i + 1] = (float)((double)src[i + 1] * invRange + offset);
                dst[i + 2] = (float)((double)src[i + 2] * invRange + offset);
                dst[i + 3] = (float)((double)src[i + 3] * invRange + offset);
            }

            for (; i < src.Length; i++)
                dst[i] = (float)((double)src[i] * invRange + offset);
        }

        // long: pixels[i] = (float)(((double)src[i] - long.MinValue) / ((double)long.MaxValue - long.MinValue));
        private static void ConvertLongToFloat_Exact(ReadOnlySpan<long> src, Span<float> dst) {
            if (dst.Length < src.Length) throw new ArgumentException("dst too small", nameof(dst));

            const double range = (double)long.MaxValue - (double)long.MinValue; // 2^64 - 1
            const double invRange = 1.0 / range;
            const double offset = -(double)long.MinValue * invRange;

            int i = 0;
            int last = src.Length - (src.Length % 4);

            for (; i < last; i += 4) {
                dst[i + 0] = (float)((double)src[i + 0] * invRange + offset);
                dst[i + 1] = (float)((double)src[i + 1] * invRange + offset);
                dst[i + 2] = (float)((double)src[i + 2] * invRange + offset);
                dst[i + 3] = (float)((double)src[i + 3] * invRange + offset);
            }

            for (; i < src.Length; i++)
                dst[i] = (float)((double)src[i] * invRange + offset);
        }

        // double: pixels[i] = (float)src[i];
        private static void ConvertDoubleToFloat_Exact(ReadOnlySpan<double> src, Span<float> dst) {
            if (dst.Length < src.Length) throw new ArgumentException("dst too small", nameof(dst));
            for (int i = 0; i < src.Length; i++)
                dst[i] = (float)src[i];
        }

        private void NormalizeSpanInPlace(Span<float> data) {
            if (DataMin != null && DataMax != null && DataMin < DataMax) {
                double min = DataMin.Value;
                double range = DataMax.Value - DataMin.Value;

                for (int i = 0; i < data.Length; i++) {
                    data[i] = (float)((data[i] - min) / range);
                }
            }
        }
    }
}