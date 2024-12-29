using Newtonsoft.Json;
using System;
using System.Runtime.Serialization;

namespace NINA.Plugin.Livestack.Image {

    [JsonObject(MemberSerialization.OptIn)]
    public class CalibrationFrameMeta {

        public CalibrationFrameMeta() {
            Mean = double.NaN;
        }

        public CalibrationFrameMeta(CalibrationFrameType type, string path, int gain, int offset, double exposureTime, string filter, int width, int height, double mean) {
            Type = type;
            Path = path;
            Gain = gain;
            Offset = offset;
            ExposureTime = exposureTime;
            Filter = filter;
            Width = width;
            Height = height;
            Mean = mean;
        }

        [JsonProperty]
        public CalibrationFrameType Type { get; set; }

        [JsonProperty]
        public string Path { get; set; }

        [JsonProperty]
        public int Gain { get; set; }

        [JsonProperty]
        public int Offset { get; set; }

        [JsonProperty]
        public double ExposureTime { get; set; }

        [JsonProperty]
        public string Filter { get; set; }

        [JsonProperty]
        public int Width { get; set; }

        [JsonProperty]
        public int Height { get; set; }

        [JsonProperty]
        public double Mean { get; set; }

        public override bool Equals(object obj) {
            if (obj is CalibrationFrameMeta other) {
                return Type == other.Type &&
                       Path == other.Path &&
                       Gain == other.Gain &&
                       Offset == other.Offset &&
                       ExposureTime.Equals(other.ExposureTime) &&
                       Filter == other.Filter &&
                       Width == other.Width &&
                       Height == other.Height;
            }
            return false;
        }

        public override int GetHashCode() {
            unchecked // Allow overflow
            {
                int hash = 17;
                hash = hash * 23 + Type.GetHashCode();
                hash = hash * 23 + (Path?.GetHashCode() ?? 0);
                hash = hash * 23 + Gain.GetHashCode();
                hash = hash * 23 + Offset.GetHashCode();
                hash = hash * 23 + ExposureTime.GetHashCode();
                hash = hash * 23 + (Filter?.GetHashCode() ?? 0);
                hash = hash * 23 + Width.GetHashCode();
                hash = hash * 23 + Height.GetHashCode();
                return hash;
            }
        }
    }

    public enum CalibrationFrameType {
        DARK,
        BIAS,
        FLAT
    }
}