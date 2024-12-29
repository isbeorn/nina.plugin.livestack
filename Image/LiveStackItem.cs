using NINA.Core.Enum;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace NINA.Plugin.Livestack.Image {

    public class LiveStackItem {

        public LiveStackItem(string path,
                             string target,
                             string filter,
                             double exposureTime,
                             int gain,
                             int offset,
                             int width,
                             int height,
                             int bitDepth,
                             bool isBayered,
                             IStarDetectionAnalysis analysis) {
            Path = path;
            Filter = filter;
            ExposureTime = exposureTime;
            Gain = gain;
            Offset = offset;
            Target = target;
            Width = width;
            Height = height;
            IsBayered = isBayered;
            HFR = analysis.HFR;
            StarList = analysis.StarList.Select(star => new DetectedStar {
                HFR = star.HFR,
                Position = new Accord.Point(star.Position.X, star.Position.Y),
                AverageBrightness = star.AverageBrightness,
                MaxBrightness = star.MaxBrightness,
                Background = star.Background,
                BoundingBox = new Rectangle(star.BoundingBox.X, star.BoundingBox.Y, star.BoundingBox.Width, star.BoundingBox.Height)
            }).ToList();
        }

        public string Path { get; }
        public string Target { get; }

        public string Filter { get; }
        public double ExposureTime { get; }
        public int Gain { get; }
        public int Offset { get; }
        public int Width { get; }
        public int Height { get; }
        public bool IsBayered { get; }
        public double HFR { get; }

        public List<DetectedStar> StarList { get; }
    }
}