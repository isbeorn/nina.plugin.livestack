using Accord;
using NINA.Image.ImageAnalysis;

namespace nina.plugin.livestack.benchmark {
    public sealed record ArchiveStarCatalog(string File, string Sha256, int Width, int Height, string SensorMode,
        string Detector, string DetectorVersion, ArchiveStar[] Stars) {
        public string Target { get; init; } = "";
        public string Filter { get; init; } = "";
        public Dictionary<string, string> DetectorSettings { get; init; } = new();
        public List<DetectedStar> ToDetections() => Stars.Select(star => new DetectedStar {
            Position = new Point(star.X, star.Y), HFR = star.Hfr, MaxBrightness = star.MaxBrightness,
            AverageBrightness = star.AverageBrightness, Background = star.Background,
            BoundingBox = new System.Drawing.Rectangle(star.BoxX, star.BoxY, star.BoxWidth, star.BoxHeight)
        }).ToList();
    }

    public sealed record ArchiveStar(float X, float Y, double Hfr, double MaxBrightness, double AverageBrightness,
        double Background, int BoxX, int BoxY, int BoxWidth, int BoxHeight) {
        public static ArchiveStar FromDetection(DetectedStar star) => new(star.Position.X, star.Position.Y, star.HFR,
            star.MaxBrightness, star.AverageBrightness, star.Background, star.BoundingBox.X, star.BoundingBox.Y,
            star.BoundingBox.Width, star.BoundingBox.Height);
    }
}
