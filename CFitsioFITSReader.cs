using System;
using System.Globalization;
using System.IO;
using NINA.Image.FileFormat.FITS;
using static NINA.Image.FileFormat.FITS.CfitsioNative;
using NINA.Image.ImageData;
using NINA.Core.Utility;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Documents;
using System.Windows.Media.Media3D;
using Grpc.Core;

namespace NINA.Plugin.Livestack {

    public class CFitsioFITSReader : IDisposable {
        private nint filePtr;
        private string tempFile;

        public CFitsioFITSReader(string filePath) {
            CfitsioNative.fits_open_file(out filePtr, filePath, CfitsioNative.IOMODE.READONLY, out var status);
            CfitsioNative.CheckStatus("fits_open_file", status);

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
                CfitsioNative.fits_create_file(out var ptr, tempFile, out status);
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

                CfitsioNative.fits_open_file(out filePtr, tempFile, CfitsioNative.IOMODE.READONLY, out status);
                CfitsioNative.CheckStatus("fits_open_file", status);
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
                    var result = fits_read_pix(filePtr, datatype, firstpix, Width, (IntPtr)nulValRef, (IntPtr)fixedBuffer, out var nullCount, out var status);
                    CheckStatus("fits_read_pix", status);
                }
                return resultBuffer;
            }
        }

        private T[] ReadAllPixels<T>() {
            const int nelem = 2;
            var firstpix = new int[nelem] { 1, 1 };

            var datatype = GetDataType(typeof(T));

            unsafe {
                var resultBuffer = new T[Width * Height];

                var nulVal = default(T);
                var nulValRef = &nulVal;
                fixed (T* fixedBuffer = resultBuffer) {
                    var result = fits_read_pix(filePtr, datatype, firstpix, Width * Height, (IntPtr)nulValRef, (IntPtr)fixedBuffer, out var nullCount, out var status);
                    CheckStatus("fits_read_pix", status);
                }
                return resultBuffer;
            }
        }

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

        public ushort[] ReadPixelRowAsUShort(int row) {
            if (BitPix == BITPIX.BYTE_IMG) {
                var pixels = ReadPixelRow<byte>(row);
                return ToUshortArray(pixels);
            } else if (BitPix == BITPIX.DOUBLE_IMG) {
                var pixels = ReadPixelRow<double>(row);
                return ToUshortArray(pixels);
            } else if (BitPix == BITPIX.FLOAT_IMG) {
                var pixels = ReadPixelRow<float>(row);
                return ToUshortArray(pixels);
            } else if (BitPix == BITPIX.LONGLONG_IMG) {
                var pixels = ReadPixelRow<long>(row);
                return ToUshortArray(pixels);
            } else if (BitPix == BITPIX.LONG_IMG) {
                var pixels = ReadPixelRow<int>(row);
                return ToUshortArray(pixels);
            } else if (BitPix == BITPIX.SHORT_IMG) {
                var pixels = ReadPixelRow<ushort>(row);
                return pixels;
            } else {
                throw new ArgumentException($"Invalid BITPIX {BitPix}");
            }
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

        private static ushort[] ToUshortArray(byte[] src) {
            ushort[] pixels = new ushort[src.Length];
            for (int i = 0; i < src.Length; i++) {
                pixels[i] = (ushort)((src[i] / (double)byte.MaxValue) * ushort.MaxValue);
            }
            return pixels;
        }

        private static ushort[] ToUshortArray(double[] src) {
            ushort[] pixels = new ushort[src.Length];
            for (int i = 0; i < src.Length; i++) {
                pixels[i] = (ushort)(src[i] * ushort.MaxValue);
            }
            return pixels;
        }

        private static ushort[] ToUshortArray(float[] src) {
            ushort[] pixels = new ushort[src.Length];
            for (int i = 0; i < src.Length; i++) {
                pixels[i] = (ushort)(src[i] * ushort.MaxValue);
            }
            return pixels;
        }

        private static ushort[] ToUshortArray(long[] src) {
            ushort[] pixels = new ushort[src.Length];
            for (int i = 0; i < src.Length; i++) {
                pixels[i] = (ushort)((((double)src[i] - long.MinValue) / ((double)long.MaxValue - long.MinValue)) * ushort.MaxValue);
            }
            return pixels;
        }

        private static ushort[] ToUshortArray(int[] src) {
            ushort[] pixels = new ushort[src.Length];
            for (int i = 0; i < src.Length; i++) {
                pixels[i] = (ushort)((((double)src[i] - int.MinValue) / ((double)int.MaxValue - int.MinValue)) * ushort.MaxValue);
            }
            return pixels;
        }

        public double ReadDoubleHeader(string keyname) {
            return fits_read_key_double(filePtr, keyname);
        }

        public float ReadFloatHeader(string keyname) {
            return fits_read_key_float(filePtr, keyname);
        }

        private const int FLEN_KEYWORD = 75;
        private const int FLEN_COMMENT = 73;

        // fits_read_key_double      ffgkyd
        [DllImport("cfitsionative.dll", CharSet = CharSet.Ansi, EntryPoint = "ffgkyd", CallingConvention = CallingConvention.Cdecl)]
        private static extern int _fits_read_key_double(
            IntPtr fptr,
            [MarshalAs(UnmanagedType.LPStr, SizeConst = FLEN_KEYWORD)] string keyname,
            out double value,
            [MarshalAs(UnmanagedType.LPStr, SizeConst = FLEN_COMMENT)] StringBuilder comm,
            out int status);

        public static double fits_read_key_double(IntPtr fptr, string keyname) {
            _fits_read_key_double(fptr, keyname, out var value, null, out var status);
            CheckStatus("fits_read_key_long", status);
            return value;
        }

        // fits_read_key_double      ffgkye
        [DllImport("cfitsionative.dll", CharSet = CharSet.Ansi, EntryPoint = "ffgkye", CallingConvention = CallingConvention.Cdecl)]
        private static extern int _fits_read_key_float(
            IntPtr fptr,
            [MarshalAs(UnmanagedType.LPStr, SizeConst = FLEN_KEYWORD)] string keyname,
            out float value,
            [MarshalAs(UnmanagedType.LPStr, SizeConst = FLEN_COMMENT)] StringBuilder comm,
            out int status);

        public static float fits_read_key_float(IntPtr fptr, string keyname) {
            _fits_read_key_float(fptr, keyname, out var value, null, out var status);
            CheckStatus("fits_read_key_long", status);
            return value;
        }

        // fits_read_key_double      ffgkyj
        [DllImport("cfitsionative.dll", CharSet = CharSet.Ansi, EntryPoint = "ffgkyj", CallingConvention = CallingConvention.Cdecl)]
        private static extern int _fits_read_key_lng(
            IntPtr fptr,
            [MarshalAs(UnmanagedType.LPStr, SizeConst = FLEN_KEYWORD)] string keyname,
            out long value,
            [MarshalAs(UnmanagedType.LPStr, SizeConst = FLEN_COMMENT)] StringBuilder comm,
            out int status);

        public static long fits_read_key_lng(IntPtr fptr, string keyname) {
            _fits_read_key_lng(fptr, keyname, out var value, null, out var status);
            CheckStatus("fits_read_key_long", status);
            return value;
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
    }
}