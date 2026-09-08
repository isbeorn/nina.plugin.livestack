using Accord;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using NINA.Image.ImageAnalysis;
using NINA.Plugin.Livestack.Image;

namespace nina.plugin.livestack.benchmark {

    [MemoryDiagnoser]
    [ShortRunJob]
    public class StarMatcherBench {
        private List<DetectedStar> referenceDetections = new List<DetectedStar>();
        private List<DetectedStar> targetDetections = new List<DetectedStar>();
        private int width;
        private int height;

        [Params(
            "Sparse_20_1936x1096",
            "Typical_300_6248x4176",
            "DenseFullFrame_3000_9576x6388",
            "MeridianFlip_3000_9576x6388",
            "MediumFormat_12000_11656x8750")]
        public string Scenario { get; set; } = "Sparse_20_1936x1096";

        [GlobalSetup]
        public void Setup() {
            var scenario = StarFieldScenario.Parse(Scenario);
            width = scenario.Width;
            height = scenario.Height;

            var referenceStars = CreateRandomStarField(scenario.StarCount, width, height, scenario.Seed);
            double[,] transform = scenario.MeridianFlip
                ? new double[3, 3] {
                    { -1.0, 0.0, width },
                    { 0.0, -1.0, height },
                    { 0.0, 0.0, 1.0 }
                }
                : new double[3, 3] {
                    { 1.0012, 0.0125, -42.0 },
                    { -0.0105, 0.9991, 27.5 },
                    { 0.0, 0.0, 1.0 }
                };

            var targetStars = ApplyAffine(referenceStars, transform, scenario.Seed + 11, jitter: 0.22f);
            targetStars.AddRange(CreateRandomStarField(Math.Max(3, scenario.StarCount / 10), width, height, scenario.Seed + 97));
            Shuffle(referenceStars, scenario.Seed + 23);
            Shuffle(targetStars, scenario.Seed + 41);

            referenceDetections = ToDetectedStars(referenceStars, width, height);
            targetDetections = ToDetectedStars(targetStars, width, height);
        }

        [Benchmark]
        public double MatchStars() {
            IImageTransformer transformer = ImageTransformer2.Instance;
            var referenceStars = transformer.GetStars(referenceDetections, width, height);
            var targetStars = transformer.GetStars(targetDetections, width, height);
            var matrix = transformer.ComputeAffineTransformation(targetStars, referenceStars);
            return matrix[0, 0] + matrix[0, 2] + matrix[1, 1] + matrix[1, 2];
        }

        private static List<SyntheticStar> CreateRandomStarField(int count, int width, int height, int seed) {
            Random random = new Random(seed);
            var stars = new List<SyntheticStar>(count);

            for (int i = 0; i < count; i++) {
                float x = 32f + ((float)random.NextDouble() * (width - 64f));
                float y = 32f + ((float)random.NextDouble() * (height - 64f));
                stars.Add(new SyntheticStar(
                    new Point(x, y),
                    12000 + ((i * 7919) % 32000),
                    3.0 + ((i % 7) * 0.03)));
            }

            return stars;
        }

        private static List<SyntheticStar> ApplyAffine(IEnumerable<SyntheticStar> stars, double[,] matrix, int seed, float jitter) {
            Random random = new Random(seed);
            var transformed = new List<SyntheticStar>();

            foreach (SyntheticStar star in stars) {
                float x = (float)((matrix[0, 0] * star.Position.X) + (matrix[0, 1] * star.Position.Y) + matrix[0, 2]);
                float y = (float)((matrix[1, 0] * star.Position.X) + (matrix[1, 1] * star.Position.Y) + matrix[1, 2]);
                x += (((float)random.NextDouble() * 2f) - 1f) * jitter;
                y += (((float)random.NextDouble() * 2f) - 1f) * jitter;
                transformed.Add(new SyntheticStar(new Point(x, y), star.MaxBrightness, star.Hfr));
            }

            return transformed;
        }

        private static List<DetectedStar> ToDetectedStars(List<SyntheticStar> stars, int width, int height) {
            var detections = new List<DetectedStar>(stars.Count);

            for (int i = 0; i < stars.Count; i++) {
                int x = (int)Math.Round(stars[i].Position.X);
                int y = (int)Math.Round(stars[i].Position.Y);
                detections.Add(new DetectedStar {
                    Position = stars[i].Position,
                    BoundingBox = new System.Drawing.Rectangle(Math.Clamp(x - 4, 1, width - 9), Math.Clamp(y - 4, 1, height - 9), 8, 8),
                    MaxBrightness = stars[i].MaxBrightness,
                    Background = 500,
                    HFR = stars[i].Hfr
                });
            }

            return detections;
        }

        private static void Shuffle<T>(IList<T> list, int seed) {
            Random random = new Random(seed);
            for (int i = list.Count - 1; i > 0; i--) {
                int swapIndex = random.Next(i + 1);
                (list[i], list[swapIndex]) = (list[swapIndex], list[i]);
            }
        }

        private readonly struct StarFieldScenario {
            private StarFieldScenario(int starCount, int width, int height, int seed, bool meridianFlip = false) {
                StarCount = starCount;
                Width = width;
                Height = height;
                Seed = seed;
                MeridianFlip = meridianFlip;
            }

            public int StarCount { get; }
            public int Width { get; }
            public int Height { get; }
            public int Seed { get; }
            public bool MeridianFlip { get; }

            public static StarFieldScenario Parse(string value) {
                return value switch {
                    "Sparse_20_1936x1096" => new StarFieldScenario(20, 1936, 1096, 101),
                    "Typical_300_6248x4176" => new StarFieldScenario(300, 6248, 4176, 211),
                    "DenseFullFrame_3000_9576x6388" => new StarFieldScenario(3000, 9576, 6388, 307),
                    "MeridianFlip_3000_9576x6388" => new StarFieldScenario(3000, 9576, 6388, 503, meridianFlip: true),
                    "MediumFormat_12000_11656x8750" => new StarFieldScenario(12000, 11656, 8750, 401),
                    _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
                };
            }
        }

        private readonly struct SyntheticStar {
            public SyntheticStar(Point position, double maxBrightness, double hfr) {
                Position = position;
                MaxBrightness = maxBrightness;
                Hfr = hfr;
            }

            public Point Position { get; }
            public double MaxBrightness { get; }
            public double Hfr { get; }
        }
    }
}
