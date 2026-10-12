using Accord;
using NINA.Plugin.Livestack.Image;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace nina.plugin.livestack.benchmark {
    internal static class ProposalComparison {
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type TransformerType = typeof(ImageTransformer2);
        private static readonly string[] Routes = { "Production", "OrderedTriangles", "SpatialTriangles", "Quads", "LocalTriangles", "ProductionIdentity", "ProductionWrongPrediction" };

        public static int Run(string[] args) {
            string output = Option(args, "--output") ?? "proposal-comparison.json";
            int repetitions = int.Parse(Option(args, "--repetitions") ?? "3", CultureInfo.InvariantCulture);
            int budgetMilliseconds = int.Parse(Option(args, "--budget-ms") ?? "1000", CultureInfo.InvariantCulture);
            long allocationBudget = long.Parse(Option(args, "--allocation-budget") ?? "67108864", CultureInfo.InvariantCulture);
            if (repetitions < 1 || budgetMilliseconds < 1 || allocationBudget < 1) throw new ArgumentOutOfRangeException(nameof(args));
            string[] routes = Option(args, "--route") is string route ? new[] { route } : Routes;
            if (routes.Any(value => !Routes.Contains(value))) throw new ArgumentException("Unknown proposal route.");
            List<ComparisonCase> cases = ReadCases(args);
            if (args.Contains("--both-directions")) {
                cases.AddRange(cases.ToArray().Select(item => new ComparisonCase(item.Name + "-Reverse", item.Current, item.Reference,
                    item.ExpectedAccept, item.Expected == null ? null : MathNet.Numerics.LinearAlgebra.Double.DenseMatrix.OfArray(item.Expected).Inverse().ToArray())));
            }
            List<object> results = new();
            foreach (ComparisonCase item in cases) {
                AlignmentStarCatalog reference = ImageTransformer2.Instance.PrepareAlignmentStars(item.Reference.ToDetections(), item.Reference.Width, item.Reference.Height);
                AlignmentStarCatalog current = ImageTransformer2.Instance.PrepareAlignmentStars(item.Current.ToDetections(), item.Current.Width, item.Current.Height);
                foreach (string path in routes) {
                    using CancellationTokenSource initialization = new(budgetMilliseconds);
                    try { Solve(path, current, reference, item.Reference.Width, item.Reference.Height, initialization.Token); }
                    catch (OperationCanceledException) { }
                    catch (Exception error) when (error is InvalidOperationException or ArgumentException or ArithmeticException) { }
                }
                foreach (string cache in new[] { "Cold", "Warm" }) {
                    for (int repetition = 0; repetition < repetitions; repetition++) {
                        IEnumerable<string> order = repetition % 2 == 0 ? routes : routes.Reverse();
                        foreach (string path in order) {
                            TransformerType.GetField("quadReferenceCache", PrivateInstance)!.SetValue(ImageTransformer2.Instance, null);
                            if (cache == "Warm") {
                                using CancellationTokenSource warmup = new(budgetMilliseconds);
                                try { Solve(path, current, reference, item.Reference.Width, item.Reference.Height, warmup.Token); }
                                catch (OperationCanceledException) { }
                                catch (Exception error) when (error is InvalidOperationException or ArgumentException or ArithmeticException) { }
                            }
                            using CancellationTokenSource deadline = new(budgetMilliseconds);
                            long before = GC.GetTotalAllocatedBytes(true);
                            Stopwatch clock = Stopwatch.StartNew();
                            AlignmentResult? result = null;
                            string? errorText = null;
                            bool canceled = false;
                            try { result = Solve(path, current, reference, item.Reference.Width, item.Reference.Height, deadline.Token); }
                            catch (OperationCanceledException) { canceled = true; }
                            catch (Exception error) when (error is InvalidOperationException or ArgumentException or ArithmeticException) { errorText = error.Message; }
                            clock.Stop();
                            long allocated = GC.GetTotalAllocatedBytes(true) - before;
                            double? errorPixels = result?.Success == true && item.Expected != null
                                ? MaximumError(result.Matrix, item.Expected, item.Reference.Width, item.Reference.Height) : null;
                            results.Add(new {
                                item.Name, Reference = item.Reference.File, Current = item.Current.File,
                                Route = path, Cache = cache, Repetition = repetition, Milliseconds = clock.Elapsed.TotalMilliseconds,
                                AllocatedBytes = allocated, WithinTimeBudget = clock.Elapsed.TotalMilliseconds <= budgetMilliseconds,
                                WithinAllocationBudget = allocated <= allocationBudget, Canceled = canceled,
                                Accepted = result?.Success ?? false, Rejection = errorText ?? result?.RejectionReason,
                                ExpectedAcceptance = item.ExpectedAccept, MaximumOracleErrorPixels = errorPixels,
                                Accuracy = result?.Success != true ? "Rejected" : item.ExpectedAccept == false ? "FalseAccept"
                                    : errorPixels is double measured ? measured <= 0.75 ? "CorrectAccept" : "IncorrectTransform" : "Unadjudicated",
                                Matches = result?.InlierCount, Matrix = result?.Matrix?.Cast<double>().ToArray(),
                                ReferenceMatchStars = reference.MatchStars.Count, CurrentMatchStars = current.MatchStars.Count,
                                CurrentSupportStars = current.SupportStars.Count
                            });
                        }
                    }
                }
            }
            Assembly plugin = TransformerType.Assembly;
            string destination = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, JsonSerializer.Serialize(new {
                CreatedUtc = DateTimeOffset.UtcNow, Runtime = Environment.Version.ToString(), Environment.ProcessorCount,
                PluginSha256 = Hash(plugin.Location), RunnerSha256 = Hash(Assembly.GetExecutingAssembly().Location),
                BudgetMilliseconds = budgetMilliseconds, AllocationBudgetBytes = allocationBudget,
                BudgetSemantics = "Cancellation is cooperative. Over-budget completed work is recorded, not counted as an in-budget success.",
                TimingScope = "Initialized runtime and prepared catalogs; includes association index, proposal and common final verifier. Cold means descriptor-cache cold, not process startup. No detector, pixels or resampling.",
                Results = results
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Wrote {destination}");
            return 0;
        }

        private static AlignmentResult? Solve(string route, AlignmentStarCatalog current, AlignmentStarCatalog reference, int width, int height, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            if (route.StartsWith("Production", StringComparison.Ordinal)) {
                double[,] prediction = route == "ProductionIdentity" ? new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }
                    : route == "ProductionWrongPrediction" ? new double[,] { { -1, 0, width + 300 }, { 0, -1, height - 200 }, { 0, 0, 1 } }
                    : new double[3, 3];
                return ImageTransformer2.Instance.ComputeAlignment(current, reference, width, height, prediction, token);
            }
            List<Point> referencePoints = reference.MatchStars.ToList();
            List<Point> currentPoints = current.MatchStars.ToList();
            Type searchType = TransformerType.GetNestedType("AlignmentSearch", BindingFlags.NonPublic)!;
            object search = Activator.CreateInstance(searchType, new object?[] { referencePoints, current.SupportStars.ToList(), true, currentPoints.Count })!;
            double[,]? candidate;
            if (route == "Quads") {
                object?[] arguments = { referencePoints, currentPoints, null, token, search };
                bool found = (bool)Invoke("TryComputeQuadAffineTransformation", arguments)!;
                candidate = found ? (double[,]?)arguments[2] : null;
            } else {
                bool preserveOrder = route == "OrderedTriangles";
                List<Point> limitedCurrent = (List<Point>)Invoke("LimitPointSetForTriangleFallback", new object?[] { currentPoints, preserveOrder })!;
                List<Point> limitedReference = (List<Point>)Invoke("LimitPointSetForTriangleFallback", new object?[] { referencePoints, preserveOrder })!;
                candidate = route == "LocalTriangles"
                    ? (double[,]?)Invoke("EstimateAffineTransformation", new object?[] { LocalTriangleProposal.Match(limitedReference, limitedCurrent, token), token })
                    : (double[,]?)Invoke("ComputeTriangleAffineTransformation", new object?[] { limitedCurrent, limitedReference, token });
            }
            token.ThrowIfCancellationRequested();
            return (AlignmentResult)Invoke("RefineAndValidate", new object?[] { candidate, search, width, height })!;
        }

        private static object? Invoke(string name, object?[] arguments) {
            try { return TransformerType.GetMethod(name, PrivateInstance)!.Invoke(ImageTransformer2.Instance, arguments); }
            catch (TargetInvocationException error) when (error.InnerException != null) {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static List<ComparisonCase> ReadCases(string[] args) {
            string? catalogs = Option(args, "--catalogs");
            if (catalogs == null) {
                string scenario = Option(args, "--scenario") ?? "Typical300";
                string[] scenarios = scenario == "all" ? CatalogAlignmentBench.Scenarios.Where(value => value != "MultipleReferences3000").ToArray() : new[] { scenario };
                return scenarios.Select(name => {
                    AlignmentBenchmarkCase item = AlignmentBenchmarkCase.Create(name, 307);
                    ArchiveStarCatalog Catalog(string label, List<NINA.Image.ImageAnalysis.DetectedStar> stars) => new(label, "synthetic-seed-307", item.Width, item.Height, "Mono", "Synthetic", "1", stars.Select(ArchiveStar.FromDetection).ToArray());
                    return new ComparisonCase(name, Catalog("reference", item.Reference), Catalog("current", item.Current), item.ShouldAccept, item.Expected);
                }).ToList();
            }
            Dictionary<string, ArchiveStarCatalog> records = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in catalogs.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
                using FileStream input = File.OpenRead(path);
                using Stream content = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(input, CompressionMode.Decompress) : input;
                using StreamReader reader = new(content);
                while (reader.ReadLine() is string line) {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    ArchiveStarCatalog item = JsonSerializer.Deserialize<ArchiveStarCatalog>(line, ArchiveVerification.JsonOptions)!;
                    if (!records.TryAdd(item.File, item)) throw new InvalidDataException("Duplicate catalog path: " + item.File);
                }
            }
            using JsonDocument pairs = JsonDocument.Parse(File.ReadAllText(Option(args, "--pairs") ?? throw new ArgumentException("--pairs is required.")));
            List<ComparisonCase> output = new();
            foreach (JsonElement pair in pairs.RootElement.EnumerateArray()) {
                string referencePath = pair.GetProperty("Reference").GetString()!;
                string currentPath = pair.GetProperty("Current").GetString()!;
                ArchiveStarCatalog reference = records[referencePath], current = records[currentPath];
                if (reference.Width != current.Width || reference.Height != current.Height) throw new InvalidDataException("Pair dimensions differ.");
                bool? expected = pair.TryGetProperty("ExpectedAcceptance", out JsonElement accepted) && accepted.ValueKind != JsonValueKind.Null ? accepted.GetBoolean() : null;
                double[,]? matrix = null;
                if (pair.TryGetProperty("ExpectedMatrix", out JsonElement oracle) && oracle.ValueKind == JsonValueKind.Array) {
                    double[] flat = oracle.EnumerateArray().Select(value => value.GetDouble()).ToArray();
                    if (flat.Length != 9) throw new InvalidDataException("ExpectedMatrix must contain nine values.");
                    matrix = new double[3, 3];
                    for (int i = 0; i < 9; i++) matrix[i / 3, i % 3] = flat[i];
                }
                output.Add(new(pair.GetProperty("Name").GetString()!, reference, current, expected, matrix));
            }
            return output;
        }

        private static double MaximumError(double[,] actual, double[,] expected, int width, int height) {
            double maximum = 0;
            foreach (double x in new[] { 0d, (width - 1) / 2d, width - 1d }) {
                foreach (double y in new[] { 0d, (height - 1) / 2d, height - 1d }) {
                    double dx = (actual[0, 0] - expected[0, 0]) * x + (actual[0, 1] - expected[0, 1]) * y + actual[0, 2] - expected[0, 2];
                    double dy = (actual[1, 0] - expected[1, 0]) * x + (actual[1, 1] - expected[1, 1]) * y + actual[1, 2] - expected[1, 2];
                    maximum = Math.Max(maximum, Math.Sqrt(dx * dx + dy * dy));
                }
            }
            return maximum;
        }

        private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        private static string? Option(string[] args, string name) {
            int index = Array.IndexOf(args, name);
            return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing value for " + name);
        }
        private sealed record ComparisonCase(string Name, ArchiveStarCatalog Reference, ArchiveStarCatalog Current, bool? ExpectedAccept, double[,]? Expected);
    }
}
