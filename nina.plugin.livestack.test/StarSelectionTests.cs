using Accord;
using NINA.Image.ImageAnalysis;
using NINA.Plugin.Livestack.Image;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace nina.plugin.livestack.test {
    public class StarSelectionTests {
        // Recorded from the scalar implementation. Preserve the entire ordered catalog,
        // including source order and the dense-catalog reduction boundary.
        [TestCase(1025, "5691BE9063C97FC9487B202F148D9B2DDA3C054D508A17033BDCDBEA55294913")]
        [TestCase(2001, "ECC9F4D01B246688CB98FF09E855A24F00A50117E8F98856A666BA3EFCA76F8E")]
        [TestCase(9000, "F5DCDFC130F1D757BFB460C447FC933C0D579ED6744CCB77F7426ECB0B1C8C5D")]
        public void DenseSelectionPreservesScalarCatalogExactly(int count, string expectedHash) {
            Random random = new(307);
            List<DetectedStar> detections = Enumerable.Range(0, count).Select(i => {
                float x = 100 + (float)random.NextDouble() * 9376;
                float y = 100 + (float)random.NextDouble() * 6188;
                double brightness = 12000 + (i * 7919) % 32000;
                return new DetectedStar {
                    Position = new Point(x, y), MaxBrightness = brightness, AverageBrightness = brightness / 3,
                    Background = 500, HFR = 3, BoundingBox = new System.Drawing.Rectangle((int)x - 4, (int)y - 4, 8, 8)
                };
            }).ToList();
            List<Point> selected = ImageTransformer2.Instance.GetStars(detections, 9576, 6388);
            float[] coordinates = selected.SelectMany(point => new[] { point.X, point.Y }).ToArray();
            Assert.That(selected.Count, Is.EqualTo(1000));
            Assert.That(Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(coordinates.AsSpan()))), Is.EqualTo(expectedHash));
        }

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
