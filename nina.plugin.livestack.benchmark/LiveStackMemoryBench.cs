using BenchmarkDotNet.Attributes;
using NINA.Image.ImageData;
using NINA.Plugin.Livestack.Image;

namespace nina.plugin.livestack.benchmark {
    [MemoryDiagnoser]
    public class LiveStackMemoryBench {
        private float[] source = [];
        private float[] originalStack = [];
        private uint[] originalCounts = [];
        private LiveStackBag compactStack = null!;
        private int height;
        private readonly double[,] matrix = { { 1d, 0d, 0.5d }, { 0d, 1d, -0.5d }, { 0d, 0d, 1d } };

        [Params(1936, 6248, 11656)]
        public int Width { get; set; } = 1936;

        [GlobalSetup]
        public void Setup() {
            height = Width switch { 1936 => 1096, 6248 => 4176, 11656 => 8750, _ => throw new ArgumentOutOfRangeException() };
            source = Enumerable.Repeat(0.6f, Width * height).ToArray();
            originalStack = Enumerable.Repeat(0.2f, source.Length).ToArray();
            originalCounts = Enumerable.Repeat(1u, source.Length).ToArray();
            compactStack = new LiveStackBag("benchmark", "L", new ImageProperties(Width, height, 16, false, 100, 10), new ImageMetaData(), new());
            compactStack.Add((float[])originalStack.Clone());
        }

        [Benchmark(Baseline = true)]
        public float Full32BitCounts() {
            ImageTransformer2.Instance.ApplyAffineTransformationAndStack(source, originalStack, originalCounts, Width, height, matrix);
            return originalStack[source.Length / 2];
        }

        [Benchmark]
        public float CompactCounts() {
            compactStack.AddTransformed(source, matrix, false);
            return compactStack.Stack[source.Length / 2];
        }
    }
}
