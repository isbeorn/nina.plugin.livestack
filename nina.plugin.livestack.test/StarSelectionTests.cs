using Accord;
using NINA.Image.ImageAnalysis;
using NINA.Plugin.Livestack.Image;

namespace nina.plugin.livestack.test {
    public class StarSelectionTests {
        [Test]
        public void BrightStarSeedsKeepGridOrderAndStableBrightnessTies([Values] bool tied, [Values] bool reversed) {
            double[] brightness = tied ? new[] { 20000d, 20000d, 20000d, 20000d, 20000d } : new[] { 20000d, 30000d, 10000d, 40000d, 50000d };
            List<DetectedStar> detections = new();
            for (int x = 0; x < 5; x++) {
                for (int y = 0; y < 5; y++) {
                    for (int i = 0; i < 5; i++) {
                        detections.Add(Star(x * 200 + 40 + i * 20, y * 200 + 60 + i * 15, brightness[i]));
                    }
                }
            }
            if (reversed) {
                detections.Reverse();
            }
            int[] seedOrder = tied ? (reversed ? new[] { 4, 3, 2, 1 } : new[] { 0, 1, 2, 3 }) : new[] { 4, 3, 1, 0 };
            List<Point> expectedSeeds = new();
            for (int x = 0; x < 5; x++) {
                for (int y = 0; y < 5; y++) {
                    foreach (int i in seedOrder) {
                        expectedSeeds.Add(new Point(x * 200 + 40 + i * 20, y * 200 + 60 + i * 15));
                    }
                }
            }

            List<Point> selected = ImageTransformer2.Instance.GetStars(detections, 1000, 1000);

            Assert.That(selected.Take(100), Is.EqualTo(expectedSeeds));
            Assert.That(selected, Is.EquivalentTo(detections.Select(star => star.Position)));
            Assert.That(selected.Distinct().Count(), Is.EqualTo(125));
        }

        [Test]
        public void SparseGridFillsRemainingSeedsByDistanceToCenter([Values] bool reversed) {
            List<DetectedStar> detections = Enumerable.Range(0, 12).Select(i => Star(100 + i * 5, 100 + i * 3, 20000)).ToList();
            detections[0].MaxBrightness = 48000;
            detections[3].MaxBrightness = 46000;
            detections[5].MaxBrightness = 44000;
            detections[9].MaxBrightness = 42000;
            Point[] expected = new[] { 0, 3, 5, 9, 11, 10, 8, 7, 6, 4, 2, 1 }
                .Select(i => detections[i].Position).ToArray();
            if (reversed) {
                detections.Reverse();
            }

            Assert.That(ImageTransformer2.Instance.GetStars(detections, 1000, 1000), Is.EqualTo(expected));
        }

        private static DetectedStar Star(int x, int y, double brightness) {
            return new DetectedStar {
                Position = new Point(x, y),
                BoundingBox = new System.Drawing.Rectangle(x - 4, y - 4, 8, 8),
                MaxBrightness = brightness,
                Background = 500,
                HFR = 3
            };
        }
    }
}
