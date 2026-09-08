using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using NINA.Plugin.Livestack.Image;

namespace nina.plugin.livestack.benchmark {

    [MemoryDiagnoser]
    [Config(typeof(AffineStackBenchConfig))]
    public class AffineStackBench {
        private readonly ImageTransformer2 transformer = ImageTransformer2.Instance;
        private float[] source = [];
        private ushort[] ushortSource = [];
        private float[] baselineStack = [];
        private float[] directStack = [];
        private float[] destination = [];
        private uint[] contributionCounts = [];
        private double[,] affineMatrix = new double[3, 3];
        private int width;
        private int height;

        [Params(
            "Small_1936x1096",
            "Typical_6248x4176",
            "MediumFormat_11656x8750")]
        public string Scenario { get; set; } = "Small_1936x1096";

        [GlobalSetup]
        public void Setup() {
            (width, height) = Scenario switch {
                "Small_1936x1096" => (1936, 1096),
                "Typical_6248x4176" => (6248, 4176),
                "MediumFormat_11656x8750" => (11656, 8750),
                _ => throw new ArgumentOutOfRangeException(nameof(Scenario), Scenario, null)
            };

            int length = checked(width * height);
            source = new float[length];
            ushortSource = new ushort[length];
            baselineStack = new float[length];
            directStack = new float[length];
            destination = new float[length];
            contributionCounts = new uint[length];
            Array.Fill(contributionCounts, 7u);

            for (int i = 0; i < length; i++) {
                float value = ((i * 73) % 4096) / 4095f;
                source[i] = value;
                ushortSource[i] = (ushort)Math.Clamp(value * ushort.MaxValue, 0, ushort.MaxValue);

                float stackValue = ((i * 37) % 4096) / 4095f;
                baselineStack[i] = stackValue;
                directStack[i] = stackValue;
            }

            affineMatrix = new double[3, 3] {
                { 1.0003d, 0.0017d, -13.25d },
                { -0.0012d, 0.9998d, 8.75d },
                { 0d, 0d, 1d }
            };
        }

        [Benchmark(Baseline = true)]
        public float Baseline_TransformThenStack_Float() {
            float[] transformed = transformer.ApplyAffineTransformation(source, width, height, affineMatrix, flippedImage: false);
            SequentialStack(transformed, baselineStack, stackImageCount: 7);
            return Sample(baselineStack) + Sample(transformed);
        }

        [Benchmark]
        public float Into_ThenStack_Float() {
            transformer.ApplyAffineTransformationInto(source, destination, width, height, affineMatrix, flippedImage: false);
            SequentialStack(destination, directStack, stackImageCount: 7);
            return Sample(directStack) + Sample(destination);
        }

        [Benchmark]
        public float Direct_TransformAndStack_Float() {
            transformer.ApplyAffineTransformationAndStack(source, directStack, stackImageCount: 7, width, height, affineMatrix, flippedImage: false);
            return Sample(directStack);
        }

        [Benchmark]
        public float Baseline_TransformThenStack_UShort() {
            float[] transformed = transformer.ApplyAffineTransformation(ushortSource, width, height, affineMatrix, flippedImage: false);
            SequentialStack(transformed, baselineStack, stackImageCount: 7);
            return Sample(baselineStack) + Sample(transformed);
        }

        [Benchmark]
        public float Direct_TransformAndStack_UShort() {
            transformer.ApplyAffineTransformationAndStack(ushortSource, directStack, stackImageCount: 7, width, height, affineMatrix, flippedImage: false);
            return Sample(directStack);
        }

        [Benchmark]
        public float Direct_WeightedTransformAndStack_Float() {
            transformer.ApplyAffineTransformationAndStack(source, directStack, contributionCounts, width, height, affineMatrix);
            return Sample(directStack);
        }

        [Benchmark]
        public float Direct_WeightedTransformAndStack_UShort() {
            transformer.ApplyAffineTransformationAndStack(ushortSource, directStack, contributionCounts, width, height, affineMatrix);
            return Sample(directStack);
        }

        // Historical baseline for measuring fused resampling against a separate averaging pass.
        private static void SequentialStack(float[] image, float[] stack, int stackImageCount) {
            int length = stack.Length;
            float nextCount = stackImageCount + 1f;

            if (!System.Numerics.Vector.IsHardwareAccelerated || length < System.Numerics.Vector<float>.Count) {
                for (int i = 0; i < length; i++) {
                    stack[i] = (stackImageCount * stack[i] + image[i]) / nextCount;
                }
                return;
            }

            int simd = System.Numerics.Vector<float>.Count;
            int last = length - (length % simd);
            int index = 0;

            var currentCount = new System.Numerics.Vector<float>(stackImageCount);
            var nextCountVector = new System.Numerics.Vector<float>(nextCount);

            for (; index < last; index += simd) {
                var currentStack = new System.Numerics.Vector<float>(stack, index);
                var currentImage = new System.Numerics.Vector<float>(image, index);
                (((currentStack * currentCount) + currentImage) / nextCountVector).CopyTo(stack, index);
            }

            for (; index < length; index++) {
                stack[index] = (stackImageCount * stack[index] + image[index]) / nextCount;
            }
        }

        private float Sample(float[] values) {
            int middle = values.Length / 2;
            return values[0] + values[middle] + values[^1];
        }
    }

    public class AffineStackBenchConfig : ManualConfig {

        public AffineStackBenchConfig() {
            AddJob(Job.ShortRun.WithArguments(new Argument[] {
                new MsBuildArgument("/p:NoWarn=NU1701"),
                new MsBuildArgument("-m:1")
            }));
        }
    }
}
