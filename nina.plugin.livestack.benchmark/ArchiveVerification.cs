using Accord;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.LivestackDockables;
using NINA.Plugin.Livestack.Replay;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Point = Accord.Point;

namespace nina.plugin.livestack.benchmark {
    public static class ArchiveVerification {
        internal static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

        public static Task<int> Run(string[] args) {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread ui = new(() => {
                int result = 1;
                try {
                    Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    application.Startup += async (_, _) => {
                        try { result = await RunOnDispatcher(args); }
                        catch (Exception error) { Console.Error.WriteLine(error); }
                        finally { application.Shutdown(); }
                    };
                    application.Run();
                }
                catch (Exception error) { Console.Error.WriteLine(error); result = 1; }
                completion.SetResult(result);
            }) { IsBackground = true, Name = "Archive verification dispatcher" };
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            return completion.Task;
        }

        private static async Task<int> RunOnDispatcher(string[] args) {
            string? Option(string name) {
                int index = Array.IndexOf(args, name);
                return index < 0 ? null : index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing value for " + name);
            }
            string output = Path.GetFullPath(Option("--output") ?? "obj/alignment-verification/results");
            Directory.CreateDirectory(output);
            string detectorName = Option("--detector") ?? "builtin";
            if (detectorName != "builtin" && detectorName != "hf") throw new ArgumentException("Detector must be builtin or hf.");
            string sensor = Option("--sensor") ?? "Mono";
            Assembly plugin = typeof(ImageTransformer2).Assembly;
            await File.WriteAllTextAsync(Path.Combine(output, "environment.json"), JsonSerializer.Serialize(new {
                Plugin = plugin.FullName, PluginSha256 = HashFile(plugin.Location), Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription, Environment.ProcessorCount, Detector = detectorName, Sensor = sensor,
                StartedUtc = DateTimeOffset.UtcNow, Arguments = args
            }, JsonOptions));
            if (args.Contains("--catalog-check")) return CheckCatalogs(Option("--catalogs") ?? throw new ArgumentException("--catalogs is required."), output);
            string[] paths = File.ReadAllLines(Option("--input-list") ?? throw new ArgumentException("--input-list is required."))
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#')).Select(Path.GetFullPath).ToArray();
            if (paths.Length == 0) throw new ArgumentException("Input list is empty.");
            bool extract = args.Contains("--extract-catalogs");
            if (!extract && !args.Contains("--replay")) throw new ArgumentException("Choose --extract-catalogs, --catalog-check or --replay.");
            using StreamWriter results = new(Path.Combine(output, "frames.jsonl"));
            using StreamWriter catalogs = new(Path.Combine(output, "catalogs.jsonl"));
            using StreamWriter logs = new(Path.Combine(output, "processing.log"));
            TextWriter synchronizedLogs = TextWriter.Synchronized(logs);
            ConcurrentQueue<string> alignmentLogs = new();
            using StageRecorder stages = new();
            await using ReplayHost host = new(new ImmediateProgress<string>(stages.Report), new ImmediateProgress<string>(line => {
                synchronizedLogs.WriteLine(line); synchronizedLogs.Flush();
                if (line.Contains("alignment", StringComparison.OrdinalIgnoreCase)) alignmentLogs.Enqueue(line);
            }));
            host.Plugin.HotpixelRemoval = !args.Contains("--no-hotpixels");
            if (Option("--masters") is string masterFile) LoadMasters(host, masterFile, output);
            ArchiveVerificationDetector detector = new(host, detectorName, Option("--hf-directory"));
            using MemorySampler memory = new();
            Guid session = Guid.NewGuid();
            int failed = 0;
            foreach (string path in paths) {
                detector.Clear();
                alignmentLogs.Clear();
                Dictionary<LiveStackTab, int> previousCounts = host.Dockable.Tabs.OfType<LiveStackTab>().ToDictionary(tab => tab, tab => tab.StackCount);
                stages.BeginFrame();
                long allocations = GC.GetTotalAllocatedBytes(false);
                int gen2 = GC.CollectionCount(2);
                using Process process = Process.GetCurrentProcess();
                TimeSpan cpu = process.TotalProcessorTime;
                Stopwatch elapsed = Stopwatch.StartNew();
                bool accepted = false;
                string? error = null;
                try { accepted = await Task.Run(() => host.ProcessFileAsync(path, sensor, session, CancellationToken.None)); }
                catch (Exception exception) { error = exception.ToString(); failed++; }
                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                elapsed.Stop();
                stages.EndFrame();
                double cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                long allocatedBytes = GC.GetTotalAllocatedBytes(false) - allocations;
                int gen2Collections = GC.CollectionCount(2) - gen2;
                if (detector.LatestStars != null) {
                    ArchiveStarCatalog catalog = new(path, HashFile(path), detector.Width, detector.Height, sensor, detectorName, detector.Version,
                        detector.LatestStars.Select(ArchiveStar.FromDetection).ToArray()) { Target = detector.Target, Filter = detector.Filter,
                            DetectorSettings = new(detector.Settings) { ["HotpixelRemoval"] = host.Plugin.HotpixelRemoval.ToString(),
                                ["AnalysisChannel"] = sensor == "Mono" ? "Mono" : "Red" } };
                    catalogs.WriteLine(JsonSerializer.Serialize(catalog, JsonOptions));
                    catalogs.Flush();
                }
                object result = new {
                    File = path, Accepted = accepted, Error = error, Milliseconds = elapsed.Elapsed.TotalMilliseconds,
                    CpuMilliseconds = cpuMilliseconds,
                    AllocatedBytes = allocatedBytes, Gen2Collections = gen2Collections,
                    memory.PeakPrivateBytes, memory.PeakWorkingSetBytes, ManagedHeapBytes = GC.GetTotalMemory(false),
                    Stages = stages.Snapshot(), AlignmentLog = alignmentLogs.ToArray(),
                    Stacks = host.Dockable.Tabs.OfType<LiveStackTab>().Select(tab => new { tab.Target, tab.Filter, tab.StackCount,
                        Matrix = tab.StackCount > previousCounts.GetValueOrDefault(tab) ? ReadCommittedMatrix(tab) : null }).ToArray()
                };
                results.WriteLine(JsonSerializer.Serialize(result, JsonOptions)); results.Flush();
                Console.WriteLine($"{(accepted ? "Accepted" : error == null ? "Skipped" : "Error")} {Path.GetFileName(path)} {elapsed.Elapsed.TotalSeconds:F3}s");
                if (extract) {
                    host.Dockable.SelectedTab = null!;
                    foreach (IStackTab tab in host.Dockable.Tabs.ToArray()) await host.Dockable.RemoveTabCommand.ExecuteAsync(tab);
                }
            }
            if (!extract) WriteStacks(host, output);
            return failed == 0 ? 0 : 2;
        }

        private static int CheckCatalogs(string path, string output) {
            using StreamWriter writer = new(Path.Combine(output, "alignment.jsonl"));
            MethodInfo? prepare = typeof(ImageTransformer2).GetMethod("PrepareAlignmentStars");
            MethodInfo? solve = typeof(ImageTransformer2).GetMethods().SingleOrDefault(method => method.Name == "ComputeAlignment"
                && method.GetParameters()[0].ParameterType.Name == "AlignmentStarCatalog");
            Dictionary<string, (object Reference, double[,]? Prediction)> references = new();
            int processed = 0;
            foreach (ArchiveStarCatalog catalog in ReadCatalogs(path)) {
                processed++;
                Stopwatch clock = Stopwatch.StartNew();
                object current = prepare == null ? ImageTransformer2.Instance.GetStars(catalog.ToDetections(), catalog.Width, catalog.Height)
                    : prepare.Invoke(ImageTransformer2.Instance, new object[] { catalog.ToDetections(), catalog.Width, catalog.Height })!;
                double selectionMilliseconds = clock.Elapsed.TotalMilliseconds;
                string key = catalog.Target + ":" + catalog.Filter + ":" + catalog.SensorMode;
                bool initialized = references.TryGetValue(key, out (object Reference, double[,]? Prediction) state);
                object reference = initialized ? state.Reference : current;
                clock.Restart();
                AlignmentResult result = solve == null
                    ? ImageTransformer2.Instance.ComputeAlignment((List<Point>)current, (List<Point>)reference, catalog.Width, catalog.Height)
                    : (AlignmentResult)solve.Invoke(ImageTransformer2.Instance, new object?[] { current, reference, catalog.Width, catalog.Height, state.Prediction, CancellationToken.None })!;
                double solveMilliseconds = clock.Elapsed.TotalMilliseconds;
                if (result.Success) references[key] = (reference, result.Matrix);
                writer.WriteLine(JsonSerializer.Serialize(new {
                    catalog.File, RawStars = catalog.Stars.Length, result.Success, result.InlierCount, result.RmsErrorPixels,
                    result.MatchFraction, result.OverlapFraction, result.RejectionReason, Matrix = Flatten(result.Matrix),
                    SelectionMilliseconds = selectionMilliseconds, SolveMilliseconds = solveMilliseconds
                }, JsonOptions)); writer.Flush();
                Console.WriteLine($"{Path.GetFileName(catalog.File)} {result}");
            }
            if (processed == 0) throw new InvalidDataException("No catalogs were loaded.");
            return 0;
        }

        private static IEnumerable<ArchiveStarCatalog> ReadCatalogs(string path) {
            using FileStream file = File.OpenRead(path);
            using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
            using StreamReader reader = new(stream);
            while (reader.ReadLine() is string line) {
                if (!string.IsNullOrWhiteSpace(line)) yield return JsonSerializer.Deserialize<ArchiveStarCatalog>(line, JsonOptions)!;
            }
        }

        private static void WriteStacks(ReplayHost host, string output) {
            List<object> manifest = new();
            int index = 0;
            foreach (LiveStackTab tab in host.Dockable.Tabs.OfType<LiveStackTab>()) {
                FieldInfo bagField = typeof(LiveStackTab).GetField("bag", BindingFlags.Instance | BindingFlags.NonPublic)!;
                object bag = bagField.GetValue(tab)!;
                Array? counts = (Array?)bag.GetType().GetField("contributionCounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bag);
                string stem = "stack-" + index++;
                using (FileStream pixels = File.Create(Path.Combine(output, stem + ".f32"))) pixels.Write(MemoryMarshal.AsBytes(tab.Stack.AsSpan()));
                int countBytes = counts switch { byte[] => 1, ushort[] => 2, uint[] => 4, _ => 0 };
                using (FileStream countWriter = File.Create(Path.Combine(output, stem + ".counts"))) {
                    switch (counts) {
                        case byte[] small: countWriter.Write(small); break;
                        case ushort[] medium: countWriter.Write(MemoryMarshal.AsBytes(medium.AsSpan())); break;
                        case uint[] large: countWriter.Write(MemoryMarshal.AsBytes(large.AsSpan())); break;
                    }
                }
                manifest.Add(new { tab.Target, tab.Filter, tab.StackCount, tab.Properties.Width, tab.Properties.Height,
                    Pixels = stem + ".f32", Counts = stem + ".counts", CountBytes = countBytes });
            }
            File.WriteAllText(Path.Combine(output, "stacks.json"), JsonSerializer.Serialize(manifest, JsonOptions));
        }

        private static void LoadMasters(ReplayHost host, string path, string output) {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            List<object> masters = new();
            foreach (JsonElement entry in document.RootElement.EnumerateArray()) {
                CalibrationFrameType type = Enum.Parse<CalibrationFrameType>(entry.GetProperty("Type").GetString()!, ignoreCase: true);
                string file = Path.GetFullPath(entry.GetProperty("Path").GetString()!);
                CalibrationFrameMeta master = host.ReadMaster(file, type);
                if (entry.TryGetProperty("Gain", out JsonElement gain)) master.Gain = gain.GetInt32();
                if (entry.TryGetProperty("Offset", out JsonElement offset)) master.Offset = offset.GetInt32();
                if (entry.TryGetProperty("Filter", out JsonElement filter)) master.Filter = filter.GetString()!;
                if (entry.TryGetProperty("ExposureTime", out JsonElement exposure)) master.ExposureTime = exposure.GetDouble();
                switch (type) {
                    case CalibrationFrameType.BIAS: NINA.Plugin.Livestack.LivestackMediator.CalibrationVM.BiasLibrary.Add(master); break;
                    case CalibrationFrameType.DARK: NINA.Plugin.Livestack.LivestackMediator.CalibrationVM.DarkLibrary.Add(master); break;
                    case CalibrationFrameType.FLAT: NINA.Plugin.Livestack.LivestackMediator.CalibrationVM.FlatLibrary.Add(master); break;
                }
                masters.Add(new { Metadata = master, Sha256 = HashFile(file) });
            }
            File.WriteAllText(Path.Combine(output, "calibration.json"), JsonSerializer.Serialize(masters, JsonOptions));
        }

        private static double[]? Flatten(double[,]? matrix) => matrix?.Cast<double>().ToArray();
        private static double[]? ReadCommittedMatrix(LiveStackTab tab) {
            object bag = typeof(LiveStackTab).GetField("bag", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tab)!;
            return Flatten((double[,]?)bag.GetType().GetField("lastCommittedMatrix", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(bag));
        }
        private static string HashFile(string path) { using FileStream stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }

        private sealed class ImmediateProgress<T>(Action<T> callback) : IProgress<T> { public void Report(T value) => callback(value); }

        private sealed class StageRecorder : IDisposable {
            private readonly object sync = new();
            private readonly Dictionary<string, double> durations = new();
            private readonly Stopwatch clock = new();
            private string current = "Load";
            public void BeginFrame() { lock (sync) { durations.Clear(); current = "Load"; clock.Restart(); } }
            public void Report(string value) { lock (sync) { Complete(); current = value; clock.Restart(); } }
            public void EndFrame() { lock (sync) { Complete(); clock.Reset(); } }
            private void Complete() { durations[current] = durations.GetValueOrDefault(current) + clock.Elapsed.TotalMilliseconds; }
            public Dictionary<string, double> Snapshot() { lock (sync) return new(durations); }
            public void Dispose() => clock.Stop();
        }

        private sealed class MemorySampler : IDisposable {
            private readonly Timer timer;
            private long peakPrivate, peakWorking;
            public long PeakPrivateBytes => Interlocked.Read(ref peakPrivate);
            public long PeakWorkingSetBytes => Interlocked.Read(ref peakWorking);
            public MemorySampler() { timer = new Timer(_ => Sample(), null, 0, 20); }
            private void Sample() {
                using Process process = Process.GetCurrentProcess();
                long privateBytes = process.PrivateMemorySize64, workingBytes = process.WorkingSet64;
                SetMaximum(ref peakPrivate, privateBytes);
                SetMaximum(ref peakWorking, workingBytes);
            }
            private static void SetMaximum(ref long target, long value) {
                long previous = Interlocked.Read(ref target);
                while (value > previous) {
                    long observed = Interlocked.CompareExchange(ref target, value, previous);
                    if (observed == previous) return;
                    previous = observed;
                }
            }
            public void Dispose() => timer.Dispose();
        }
    }
}
