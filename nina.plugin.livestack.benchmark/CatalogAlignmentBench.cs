using Accord;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using NINA.Image.ImageAnalysis;
using NINA.Plugin.Livestack.Image;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace nina.plugin.livestack.benchmark {
    [MemoryDiagnoser]
    [ShortRunJob]
    public class CatalogAlignmentBench {
        internal static readonly string[] Scenarios = {
            "Sparse20", "Typical300", "Dense3000", "Dense12000", "AboveCap20000",
            "BadPrediction3000", "MeridianFlip3000", "Unrelated12000", "DenseNegative20000", "MultipleReferences3000"
        };

        [ParamsSource(nameof(AvailableScenarios))]
        public string Scenario { get; set; } = "Typical300";

        [Params("Normal", "NoPrediction", "Support4000")]
        public string Mode { get; set; } = "Normal";

        public IEnumerable<string> AvailableScenarios => Environment.GetEnvironmentVariable("LIVESTACK_ALIGNMENT_CATALOGS") == null
            ? Scenarios : Scenarios.Concat(new[] { "HocusFocus", "HocusFocusReverse" });
        private AlignmentBenchmarkWorkload workload = null!;

        [GlobalSetup]
        public void Setup() {
            workload = new AlignmentBenchmarkWorkload(Scenario, Mode);
            workload.Verify();
        }

        [Benchmark]
        public object PrepareCurrentCatalog() => workload.PrepareCurrent();

        [Benchmark]
        public AlignmentResult AlignPreparedCatalog() => workload.AlignPrepared();

        [Benchmark]
        public AlignmentResult PrepareAndAlignCurrent() => workload.PrepareAndAlign();
    }

    internal sealed class AlignmentBenchmarkRuntime {
        private readonly Func<List<DetectedStar>, int, int, object> prepare;
        private readonly Func<object, object, int, int, double[,], CancellationToken, AlignmentResult> align;
        private readonly Func<object, IReadOnlyList<Point>>? matching;
        private readonly Func<object, IReadOnlyList<Point>>? support;
        private readonly Func<IEnumerable<Point>, IEnumerable<Point>, object>? createCatalog;

        public AlignmentBenchmarkRuntime() {
            object transformer = ImageTransformer2.Instance;
            Type transformerType = transformer.GetType();
            MethodInfo? prepareMethod = transformerType.GetMethod("PrepareAlignmentStars");
            UsesCatalogApi = prepareMethod != null;
            prepareMethod ??= transformerType.GetMethod("GetStars")!;
            ParameterExpression detections = Expression.Parameter(typeof(List<DetectedStar>));
            ParameterExpression width = Expression.Parameter(typeof(int));
            ParameterExpression height = Expression.Parameter(typeof(int));
            prepare = Expression.Lambda<Func<List<DetectedStar>, int, int, object>>(
                Expression.Convert(Expression.Call(Expression.Constant(transformer), prepareMethod, detections, width, height), typeof(object)),
                detections, width, height).Compile();

            MethodInfo alignMethod = transformerType.GetMethods().Single(method => method.Name == "ComputeAlignment"
                && (method.GetParameters()[0].ParameterType == typeof(List<Point>)) != UsesCatalogApi);
            ParameterExpression current = Expression.Parameter(typeof(object));
            ParameterExpression reference = Expression.Parameter(typeof(object));
            ParameterExpression hint = Expression.Parameter(typeof(double[,]));
            ParameterExpression token = Expression.Parameter(typeof(CancellationToken));
            Type catalogType = alignMethod.GetParameters()[0].ParameterType;
            List<Expression> arguments = new() {
                Expression.Convert(current, catalogType), Expression.Convert(reference, catalogType), width, height
            };
            if (UsesCatalogApi) {
                arguments.Add(hint);
            }
            arguments.Add(token);
            align = Expression.Lambda<Func<object, object, int, int, double[,], CancellationToken, AlignmentResult>>(
                Expression.Call(Expression.Constant(transformer), alignMethod, arguments), current, reference, width, height, hint, token).Compile();

            if (UsesCatalogApi) {
                ParameterExpression catalog = Expression.Parameter(typeof(object));
                matching = Expression.Lambda<Func<object, IReadOnlyList<Point>>>(
                    Expression.Property(Expression.Convert(catalog, catalogType), "MatchStars"), catalog).Compile();
                support = Expression.Lambda<Func<object, IReadOnlyList<Point>>>(
                    Expression.Property(Expression.Convert(catalog, catalogType), "SupportStars"), catalog).Compile();
                ParameterExpression matches = Expression.Parameter(typeof(IEnumerable<Point>));
                ParameterExpression supporting = Expression.Parameter(typeof(IEnumerable<Point>));
                ConstructorInfo constructor = catalogType.GetConstructor(new[] { typeof(IEnumerable<Point>), typeof(IEnumerable<Point>) })!;
                createCatalog = Expression.Lambda<Func<IEnumerable<Point>, IEnumerable<Point>, object>>(
                    Expression.Convert(Expression.New(constructor, matches, supporting), typeof(object)), matches, supporting).Compile();
            }
        }

        public bool UsesCatalogApi { get; }

        public object Prepare(List<DetectedStar> detections, int width, int height, string mode) {
            object catalog = prepare(detections, width, height);
            return mode == "Support4000" && UsesCatalogApi
                ? createCatalog!(matching!(catalog), support!(catalog).Take(4000)) : catalog;
        }

        public AlignmentResult Align(object current, object reference, int width, int height, double[,]? hint) {
            return align(current, reference, width, height, hint!, default);
        }

        public (int Matching, int Support) Counts(object catalog) {
            return UsesCatalogApi ? (matching!(catalog).Count, support!(catalog).Count)
                : (((List<Point>)catalog).Count, ((List<Point>)catalog).Count);
        }
    }

    internal sealed class AlignmentBenchmarkWorkload {
        private static readonly double[,] disabledPrediction = new double[3, 3];
        private readonly AlignmentBenchmarkRuntime runtime = new();
        private readonly AlignmentBenchmarkCase[] cases;
        private readonly object[] references;
        private readonly object[] currents;
        private readonly string mode;
        private int next;

        public AlignmentBenchmarkWorkload(string scenario, string mode, string? catalogPath = null, string? expectedPath = null) {
            this.mode = mode;
            cases = scenario.StartsWith("HocusFocus", StringComparison.Ordinal)
                ? new[] { AlignmentBenchmarkCase.ReadArchive(catalogPath ?? Environment.GetEnvironmentVariable("LIVESTACK_ALIGNMENT_CATALOGS")
                    ?? throw new ArgumentException("HocusFocus requires --catalogs or LIVESTACK_ALIGNMENT_CATALOGS."),
                    expectedPath ?? Environment.GetEnvironmentVariable("LIVESTACK_ALIGNMENT_EXPECTED_MATRIX")
                    ?? throw new ArgumentException("HocusFocus requires an independent --expected-matrix or LIVESTACK_ALIGNMENT_EXPECTED_MATRIX."),
                    scenario == "HocusFocusReverse") }
                : scenario == "MultipleReferences3000"
                ? new[] { AlignmentBenchmarkCase.Create("Dense3000", 307), AlignmentBenchmarkCase.Create("Dense3000", 503) }
                : new[] { AlignmentBenchmarkCase.Create(scenario, 307) };
            references = cases.Select(item => runtime.Prepare(item.Reference, item.Width, item.Height, mode)).ToArray();
            currents = cases.Select(item => runtime.Prepare(item.Current, item.Width, item.Height, mode)).ToArray();
        }

        public bool UsesCatalogApi => runtime.UsesCatalogApi;
        public int ReferenceDetections => cases[0].Reference.Count;
        public int CurrentDetections => cases[0].Current.Count;
        public (int Matching, int Support) CurrentCounts => runtime.Counts(currents[0]);
        public (int Matching, int Support) ReferenceCounts => runtime.Counts(references[0]);

        private double[,] Prediction(AlignmentBenchmarkCase item) => mode == "NoPrediction" ? disabledPrediction : item.Prediction;

        public void Verify() {
            for (int i = 0; i < cases.Length; i++) {
                AlignmentBenchmarkCase item = cases[i];
                AlignmentResult result = runtime.Align(currents[i], references[i], item.Width, item.Height, Prediction(item));
                AlignmentBenchmarkCase.RequireExpected(result, item.Expected, item.Width, item.Height, item.ShouldAccept, item.MaximumTransformError);
                object prepared = runtime.Prepare(item.Current, item.Width, item.Height, mode);
                result = runtime.Align(prepared, references[i], item.Width, item.Height, Prediction(item));
                AlignmentBenchmarkCase.RequireExpected(result, item.Expected, item.Width, item.Height, item.ShouldAccept, item.MaximumTransformError);
            }
            next = 0;
        }

        public object PrepareCurrent() {
            AlignmentBenchmarkCase item = cases[next++ % cases.Length];
            return runtime.Prepare(item.Current, item.Width, item.Height, mode);
        }

        public AlignmentResult AlignPrepared() {
            int index = next++ % cases.Length;
            AlignmentBenchmarkCase item = cases[index];
            return runtime.Align(currents[index], references[index], item.Width, item.Height, Prediction(item));
        }

        public AlignmentResult PrepareAndAlign() {
            int index = next++ % cases.Length;
            AlignmentBenchmarkCase item = cases[index];
            object current = runtime.Prepare(item.Current, item.Width, item.Height, mode);
            return runtime.Align(current, references[index], item.Width, item.Height, Prediction(item));
        }

        public void CheckLast(object value, string operation) {
            if (value is AlignmentResult result) {
                AlignmentBenchmarkCase item = cases[(next - 1) % cases.Length];
                AlignmentBenchmarkCase.RequireExpected(result, item.Expected, item.Width, item.Height, item.ShouldAccept, item.MaximumTransformError);
            } else if (runtime.Counts(value).Matching == 0) {
                throw new InvalidOperationException($"{operation} returned an empty catalog.");
            }
        }
    }

    internal sealed record AlignmentBenchmarkCase(List<DetectedStar> Reference, List<DetectedStar> Current,
            int Width, int Height, double[,] Expected, double[,] Prediction, bool ShouldAccept, double MaximumTransformError = 0.75) {
        public static AlignmentBenchmarkCase Create(string scenario, int seed) {
            int count = scenario switch {
                "Sparse20" => 20,
                "Typical300" => 300,
                "Dense3000" or "BadPrediction3000" or "MeridianFlip3000" => 3000,
                "Dense12000" or "Unrelated12000" => 12000,
                "AboveCap20000" or "DenseNegative20000" => 20000,
                _ => throw new ArgumentException($"Unknown alignment benchmark scenario: {scenario}.")
            };
            (int width, int height) = count <= 300 ? (6248, 4176) : count <= 3000 ? (9576, 6388) : (11656, 8750);
            if (scenario == "DenseNegative20000") {
                (width, height) = (1000, 1000);
            }
            double angle = 0.012;
            double a = Math.Cos(angle), c = Math.Sin(angle);
            double[,] expected = scenario == "MeridianFlip3000"
                ? new double[,] { { -1, 0, width }, { 0, -1, height }, { 0, 0, 1 } }
                : new double[,] { { a, -c, -42 }, { c, a, 27 }, { 0, 0, 1 } };
            List<DetectedStar> reference = CreateDetections(count, width, height, seed);
            Random jitter = new(seed + 11);
            bool shouldAccept = scenario is not ("Unrelated12000" or "DenseNegative20000");
            List<DetectedStar> current = shouldAccept
                ? reference.Select(star => At(star,
                    (float)(expected[0, 0] * star.Position.X + expected[0, 1] * star.Position.Y + expected[0, 2] + (jitter.NextDouble() - 0.5) * 0.44),
                    (float)(expected[1, 0] * star.Position.X + expected[1, 1] * star.Position.Y + expected[1, 2] + (jitter.NextDouble() - 0.5) * 0.44))).ToList()
                : CreateDetections(count, width, height, seed + 401);
            current.AddRange(CreateDetections(Math.Max(3, count / 10), width, height, seed + 97));
            Shuffle(reference, seed + 23);
            Shuffle(current, seed + 41);
            double[,] prediction = (double[,])expected.Clone();
            prediction[0, 2] += scenario == "BadPrediction3000" ? 600 : 0.2;
            prediction[1, 2] += scenario == "BadPrediction3000" ? -400 : -0.2;
            if (scenario == "MeridianFlip3000") {
                prediction = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            }
            return new(reference, current, width, height, expected, prediction, shouldAccept);
        }

        public static AlignmentBenchmarkCase ReadArchive(string path, string expectedPath, bool reverse) {
            using FileStream input = File.OpenRead(path);
            using Stream content = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(input, CompressionMode.Decompress) : input;
            using StreamReader reader = new(content);
            List<ArchiveStarCatalog> catalogs = new();
            JsonSerializerOptions json = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
            while (reader.ReadLine() is string line) {
                if (!string.IsNullOrWhiteSpace(line)) {
                    catalogs.Add(JsonSerializer.Deserialize<ArchiveStarCatalog>(line, json)!);
                }
            }
            if (catalogs.Count != 2 || catalogs[0].Width != catalogs[1].Width || catalogs[0].Height != catalogs[1].Height) {
                throw new InvalidOperationException("A real-pair benchmark requires exactly two same-sized detector catalogs.");
            }
            string oracle = File.ReadAllText(expectedPath);
            JsonDocument parsed;
            try {
                parsed = JsonDocument.Parse(oracle);
            } catch (JsonException) {
                parsed = JsonDocument.Parse(File.ReadLines(expectedPath).Last(line => !string.IsNullOrWhiteSpace(line)));
            }
            using JsonDocument document = parsed;
            JsonElement matrix = document.RootElement;
            if (matrix.ValueKind == JsonValueKind.Object) {
                matrix = matrix.GetProperty("Matrix");
            }
            double[] values = matrix.EnumerateArray().SelectMany(value => value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Select(number => number.GetDouble()) : new[] { value.GetDouble() }).ToArray();
            if (values.Length != 9 || values.Any(value => !double.IsFinite(value))) {
                throw new InvalidOperationException("Expected matrix must contain nine finite numbers.");
            }
            double[,] expected = new double[3, 3];
            for (int i = 0; i < values.Length; i++) {
                expected[i / 3, i % 3] = values[i];
            }
            if (reverse) {
                double determinant = expected[0, 0] * expected[1, 1] - expected[0, 1] * expected[1, 0];
                if (Math.Abs(determinant) < 1e-12) {
                    throw new InvalidOperationException("Independent oracle matrix is singular.");
                }
                double a = expected[1, 1] / determinant, b = -expected[0, 1] / determinant;
                double c = -expected[1, 0] / determinant, d = expected[0, 0] / determinant;
                expected = new double[,] { { a, b, -a * expected[0, 2] - b * expected[1, 2] },
                    { c, d, -c * expected[0, 2] - d * expected[1, 2] }, { 0, 0, 1 } };
                catalogs.Reverse();
            }
            return new(catalogs[0].ToDetections(), catalogs[1].ToDetections(), catalogs[0].Width, catalogs[0].Height,
                expected, new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }, true, 0.75);
        }

        private static List<DetectedStar> CreateDetections(int count, int width, int height, int seed) {
            Random random = new(seed);
            return Enumerable.Range(0, count).Select(index => {
                float x = 128 + (float)random.NextDouble() * (width - 256);
                float y = 128 + (float)random.NextDouble() * (height - 256);
                double brightness = 12000 + (index * 7919) % 32000;
                DetectedStar star = new() { MaxBrightness = brightness, AverageBrightness = brightness / 3,
                    Background = 500, HFR = 3 + (index % 7) * 0.03 };
                return At(star, x, y);
            }).ToList();
        }

        private static DetectedStar At(DetectedStar original, float x, float y) {
            return new DetectedStar {
                Position = new Point(x, y), MaxBrightness = original.MaxBrightness, AverageBrightness = original.AverageBrightness,
                Background = original.Background, HFR = original.HFR,
                BoundingBox = new System.Drawing.Rectangle((int)x - 4, (int)y - 4, 8, 8)
            };
        }

        private static void Shuffle<T>(IList<T> items, int seed) {
            Random random = new(seed);
            for (int i = items.Count - 1; i > 0; i--) {
                int other = random.Next(i + 1);
                (items[i], items[other]) = (items[other], items[i]);
            }
        }

        internal static void RequireExpected(AlignmentResult result, double[,] expected, int width, int height, bool shouldAccept, double maximumError = 0.75) {
            if (result.Success != shouldAccept) {
                throw new InvalidOperationException($"Expected {(shouldAccept ? "acceptance" : "rejection")}: {result}");
            }
            if (!shouldAccept) {
                return;
            }
            double[,] actual = result.Matrix;
            if (result.RmsErrorPixels > 1 || result.InlierCount < 8 || !double.IsFinite(result.RmsErrorPixels)) {
                throw new InvalidOperationException($"Invalid alignment diagnostics: {result}");
            }
            foreach (double x in new[] { 0, width * 0.5, width - 1d }) {
                foreach (double y in new[] { 0, height * 0.5, height - 1d }) {
                    double dx = (actual[0, 0] - expected[0, 0]) * x + (actual[0, 1] - expected[0, 1]) * y + actual[0, 2] - expected[0, 2];
                    double dy = (actual[1, 0] - expected[1, 0]) * x + (actual[1, 1] - expected[1, 1]) * y + actual[1, 2] - expected[1, 2];
                    if (!double.IsFinite(dx) || !double.IsFinite(dy) || dx * dx + dy * dy > maximumError * maximumError) {
                        throw new InvalidOperationException($"Solved matrix differs from the independent transform by more than {maximumError} pixels.");
                    }
                }
            }
        }
    }

    internal static class CatalogAlignmentComparison {
        public static int Run(string[] args) {
            string output = Option(args, "--output") ?? "alignment-comparison.json";
            string scenario = Option(args, "--scenario") ?? "all";
            string requestedMode = Option(args, "--mode") ?? "all";
            string? catalogs = Option(args, "--catalogs");
            string? expectedMatrix = Option(args, "--expected-matrix");
            int iterations = int.Parse(Option(args, "--iterations") ?? "3", CultureInfo.InvariantCulture);
            int samples = int.Parse(Option(args, "--samples") ?? "5", CultureInfo.InvariantCulture);
            if (iterations < 1 || samples < 1) {
                throw new ArgumentOutOfRangeException(nameof(args), "Iterations and samples must be positive.");
            }
            string[] scenarios = scenario == "all" ? catalogs == null ? CatalogAlignmentBench.Scenarios
                : CatalogAlignmentBench.Scenarios.Concat(new[] { "HocusFocus", "HocusFocusReverse" }).ToArray() : new[] { scenario };
            string[] modes = requestedMode == "all" ? new[] { "Normal", "NoPrediction", "Support4000" } : new[] { requestedMode };
            if (modes.Any(mode => mode is not ("Normal" or "NoPrediction" or "Support4000"))) {
                throw new ArgumentException("Mode must be Normal, NoPrediction, Support4000 or all.");
            }
            List<object> rows = new();
            bool failed = false;
            foreach (string name in scenarios) {
                foreach (string mode in modes) {
                    Console.WriteLine($"Checking {name}, {mode}...");
                    AlignmentBenchmarkWorkload workload = new(name, mode, catalogs, expectedMatrix);
                    try {
                        workload.Verify();
                    } catch (InvalidOperationException exception) {
                        rows.Add(new { Scenario = name, Mode = mode, Status = "correctness_failed", Reason = exception.Message });
                        Console.WriteLine(exception.Message);
                        failed = true;
                        continue;
                    }
                    foreach ((string operation, Func<object> run) in new[] {
                        ("PrepareCurrentCatalog", new Func<object>(workload.PrepareCurrent)),
                        ("AlignPreparedCatalog", new Func<object>(() => workload.AlignPrepared())),
                        ("PrepareAndAlignCurrent", new Func<object>(() => workload.PrepareAndAlign()))
                    }) {
                        workload.CheckLast(run(), operation);
                        List<object> measurements = new();
                        AlignmentResult? diagnostic = null;
                        for (int sample = 0; sample < samples; sample++) {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            GC.Collect();
                            using Process process = Process.GetCurrentProcess();
                            TimeSpan cpuBefore = process.TotalProcessorTime;
                            long allocationBefore = GC.GetTotalAllocatedBytes(true);
                            long started = Stopwatch.GetTimestamp();
                            object last = null!;
                            for (int i = 0; i < iterations; i++) {
                                last = run();
                            }
                            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds / iterations;
                            long allocated = (GC.GetTotalAllocatedBytes(true) - allocationBefore) / iterations;
                            process.Refresh();
                            double cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds / iterations;
                            workload.CheckLast(last, operation);
                            diagnostic = last as AlignmentResult;
                            measurements.Add(new { WallMilliseconds = elapsed, CpuMilliseconds = cpu, AllocatedBytes = allocated });
                        }
                        (int matching, int supporting) = workload.CurrentCounts;
                        (int referenceMatching, int referenceSupporting) = workload.ReferenceCounts;
                        rows.Add(new {
                            Scenario = name, Mode = mode, Operation = operation, Status = "verified",
                            CatalogApi = workload.UsesCatalogApi, workload.ReferenceDetections, workload.CurrentDetections,
                            ReferenceMatchingStars = referenceMatching, ReferenceSupportStars = referenceSupporting,
                            MatchingStars = matching, SupportStars = supporting,
                            Accepted = diagnostic?.Success, Matches = diagnostic?.InlierCount,
                            RmsPixels = diagnostic?.Success == true ? diagnostic.RmsErrorPixels : (double?)null,
                            MatchFraction = diagnostic?.Success == true ? diagnostic.MatchFraction : (double?)null,
                            Samples = measurements
                        });
                    }
                }
            }
            Assembly plugin = typeof(ImageTransformer2).Assembly;
            object report = new {
                CreatedUtc = DateTimeOffset.UtcNow, Runtime = Environment.Version.ToString(), ProcessorCount = Environment.ProcessorCount,
                SourceRevision = Option(args, "--source-revision"),
                Plugin = plugin.Location, PluginVersion = plugin.GetName().Version?.ToString(),
                PluginSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(plugin.Location))),
                RunnerSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location))),
                CatalogSha256 = catalogs == null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(catalogs))),
                OracleSha256 = expectedMatrix == null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(expectedMatrix))),
                Iterations = iterations, Samples = samples, Results = rows
            };
            string fullOutput = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            File.WriteAllText(fullOutput, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Wrote {fullOutput}");
            return failed ? 1 : 0;
        }

        private static string? Option(string[] args, string name) {
            int index = Array.IndexOf(args, name);
            return index < 0 ? null : index + 1 < args.Length ? args[index + 1]
                : throw new ArgumentException($"Missing value for {name}.");
        }
    }
}
