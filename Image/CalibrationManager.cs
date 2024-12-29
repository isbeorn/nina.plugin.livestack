using NINA.Core.Enum;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using System.Threading.Tasks;
using System.Threading;
using NINA.Core.Utility;
using System.Collections.Generic;
using NINA.Image.ImageData;
using System.Linq;
using NINA.Profile;
using Accord.Statistics;
using System;

namespace NINA.Plugin.Livestack.Image {

    internal class CalibrationMaster : IDisposable {

        public CalibrationMaster(CalibrationFrameMeta meta) {
            this.Meta = meta;
            this.dataCache = new float[meta.Height][];
            imageReader = new CFitsioFITSReader(meta.Path);
        }

        public CalibrationFrameMeta Meta { get; }

        private CFitsioFITSReader imageReader;
        private float[][] dataCache;

        public float[] ReadPixelRow(int row) {
            if (dataCache[row] == null) {
                dataCache[row] = imageReader.ReadPixelRow<float>(row);
            }
            return dataCache[row];
        }

        public void Dispose() {
            try {
                imageReader.Dispose();
            } catch { }
        }
    }

    public class CalibrationManager : IDisposable {
        public IList<CalibrationFrameMeta> FlatLibrary { get; } = new List<CalibrationFrameMeta>();
        public IList<CalibrationFrameMeta> DarkLibrary { get; } = new List<CalibrationFrameMeta>();
        public IList<CalibrationFrameMeta> BiasLibrary { get; } = new List<CalibrationFrameMeta>();
        private Dictionary<CalibrationFrameMeta, CalibrationMaster> masterCache = new Dictionary<CalibrationFrameMeta, CalibrationMaster>();

        public CalibrationManager() {
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

        private CalibrationMaster GetBiasMaster(int width, int height, int gain, int offset, string inFilter, bool isBayered, CancellationToken token) {
            var filter = string.IsNullOrWhiteSpace(inFilter) ? LiveStackBag.NOFILTER : inFilter;
            CalibrationFrameMeta meta = null;
            if (BiasLibrary?.Count > 0) {
                meta = BiasLibrary.FirstOrDefault(x => x.Gain == gain && x.Offset == offset && x.Width == width && x.Height == height);
            }
            if (meta == null) {
                return null;
            }
            if (masterCache.ContainsKey(meta)) {
                return masterCache[meta];
            }
            var master = new CalibrationMaster(meta);
            masterCache.Add(meta, master);
            return master;
        }

        private CalibrationMaster GetDarkMaster(int width, int height, double exposureTime, int gain, int offset, string inFilter, bool isBayered, CancellationToken token) {
            var filter = string.IsNullOrWhiteSpace(inFilter) ? LiveStackBag.NOFILTER : inFilter;
            CalibrationFrameMeta meta = null;
            if (DarkLibrary?.Count > 0) {
                meta = DarkLibrary.FirstOrDefault(x => x.Gain == gain && x.Offset == offset && x.ExposureTime == exposureTime && x.Width == width && x.Height == height);
            }
            if (meta == null) {
                return null;
            }
            if (masterCache.ContainsKey(meta)) {
                return masterCache[meta];
            }
            if (meta == null) {
                return null;
            }
            var master = new CalibrationMaster(meta);
            masterCache.Add(meta, master);
            return master;
        }

        private CalibrationMaster GetFlatMaster(int width, int height, string inFilter, bool isBayered, CancellationToken token) {
            var filter = string.IsNullOrWhiteSpace(inFilter) ? LiveStackBag.NOFILTER : inFilter;
            CalibrationFrameMeta meta = null;
            if (FlatLibrary?.Count > 0) {
                meta = FlatLibrary.FirstOrDefault(x => x.Filter == filter && x.Width == width && x.Height == height);
            }
            if (meta == null) {
                return null;
            }
            if (masterCache.ContainsKey(meta)) {
                return masterCache[meta];
            }
            if (meta == null) {
                return null;
            }
            var master = new CalibrationMaster(meta);
            masterCache.Add(meta, master);
            return master;
        }

        public ushort[] ApplyLightFrameCalibrationInPlace(CFitsioFITSReader image, int width, int height, double exposureTime, int gain, int offset, string inFilter, bool isBayered, CancellationToken token) {
            CalibrationMaster bias = null;
            if (LivestackMediator.Plugin.UseBiasForLights) {
                bias = GetBiasMaster(width, height, gain, offset, inFilter, isBayered, token);
            }
            var dark = GetDarkMaster(width, height, exposureTime, gain, offset, inFilter, isBayered, token);
            var flat = GetFlatMaster(width, height, inFilter, isBayered, token);

            ushort[] imageArray = new ushort[width * height];
            double flatCorrected = 1d;
            float[] biasRow = Array.Empty<float>();
            float[] darkRow = Array.Empty<float>();
            float[] flatRow = Array.Empty<float>();
            for (int idxRow = 0; idxRow < height; idxRow++) {
                if (bias != null) {
                    biasRow = bias.ReadPixelRow(idxRow);
                }
                if (dark != null) {
                    darkRow = dark.ReadPixelRow(idxRow);
                }
                if (flat != null) {
                    flatRow = flat.ReadPixelRow(idxRow);
                }

                var lightRow = image.ReadPixelRow<ushort>(idxRow);

                for (int idxCol = 0; idxCol < width; idxCol++) {
                    var pixelIndex = idxRow * width + idxCol;

                    float lightCorrected = lightRow[idxCol] / (float)ushort.MaxValue;
                    if (bias != null) {
                        lightCorrected = lightCorrected - biasRow[idxCol];
                    }
                    if (dark != null) {
                        lightCorrected = lightCorrected - darkRow[idxCol];
                    }

                    if (lightCorrected < 0) { lightCorrected = 0; }
                    if (lightCorrected > ushort.MaxValue) { lightCorrected = ushort.MaxValue; }

                    if (flat != null) {
                        flatCorrected = flatRow[idxCol] / flat.Meta.Mean;
                    }

                    imageArray[pixelIndex] = (ushort)((lightCorrected / flatCorrected) * ushort.MaxValue);
                }
            }
            return imageArray;
        }

        public ushort[] ApplyFlatFrameCalibrationInPlace(CFitsioFITSReader image, int width, int height, double exposureTime, int gain, int offset, string inFilter, bool isBayered, CancellationToken token) {
            var bias = GetBiasMaster(width, height, gain, offset, inFilter, isBayered, token);
            CalibrationMaster dark = null;
            if (bias == null) {
                dark = GetDarkMaster(width, height, exposureTime, gain, offset, inFilter, isBayered, token);
            }

            ushort[] imageArray = new ushort[width * height];
            float[] biasRow = Array.Empty<float>();
            float[] darkRow = Array.Empty<float>();
            float[] flatRow = Array.Empty<float>();
            for (int idxRow = 0; idxRow < height; idxRow++) {
                if (bias != null) {
                    biasRow = bias.ReadPixelRow(idxRow);
                }
                if (dark != null) {
                    darkRow = dark.ReadPixelRow(idxRow);
                }
                var lightRow = image.ReadPixelRow<ushort>(idxRow);

                for (int idxCol = 0; idxCol < width; idxCol++) {
                    var pixelIndex = idxRow * width + idxCol;
                    float lightCorrected = lightRow[idxCol] / (float)ushort.MaxValue;
                    if (bias != null) {
                        lightCorrected = lightCorrected - biasRow[idxCol];
                    }
                    if (dark != null) {
                        lightCorrected = lightCorrected - darkRow[idxCol];
                    }

                    if (lightCorrected < 0) { lightCorrected = 0; }
                    if (lightCorrected > ushort.MaxValue) { lightCorrected = ushort.MaxValue; }

                    imageArray[pixelIndex] = (ushort)(lightCorrected * ushort.MaxValue);
                }
            }
            return imageArray;
        }

        public void Dispose() {
            foreach (var item in masterCache) {
                try {
                    item.Value.Dispose();
                } catch { }
                masterCache.Clear();
                BiasLibrary.Clear();
                DarkLibrary.Clear();
                FlatLibrary.Clear();
            }
        }
    }
}