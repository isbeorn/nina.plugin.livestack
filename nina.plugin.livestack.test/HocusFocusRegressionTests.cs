using NINA.Plugin.Livestack.Image;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using nina.plugin.livestack.benchmark;

namespace nina.plugin.livestack.test {
    public class HocusFocusRegressionTests {
        /// <summary>Checks against independent windowed phase correlation on five raw red-CFA patches.</summary>
        [TestCase(false)]
        [TestCase(true)]
        public void DenseHocusFocusFramesAlignAfterProductionStarSelection(bool reverse) {
            ArchiveStarCatalog[] frames = ReadFixture();
            ArchiveStarCatalog reference = frames[reverse ? 1 : 0];
            ArchiveStarCatalog current = frames[reverse ? 0 : 1];
            Assert.That(reference.Stars.Length, Is.GreaterThan(9000), "Keep the dense raw detector output in this regression fixture.");
            Assert.That(current.Stars.Length, Is.GreaterThan(9000));
            object selectedReference = Prepare(reference);
            object selectedCurrent = Prepare(current);
            AlignmentResult result = Solve(selectedCurrent, selectedReference, current.Width, current.Height);

            Assert.That(result.Success, Is.True, result.RejectionReason);
            Assert.That(result.RmsErrorPixels, Is.LessThanOrEqualTo(1));
            TestContext.WriteLine(result);
            double[,] matrix = result.Matrix;
            double[,] expected = { { 0.9999911077912044, 2.7760734662924474e-05, 0.0543199040756067 },
                { -6.698640308910644e-05, 0.9999861730394557, -0.11165788826444759 }, { 0, 0, 1 } };
            if (reverse) expected = MathNet.Numerics.LinearAlgebra.Double.DenseMatrix.OfArray(expected).Inverse().ToArray();
            foreach ((int x, int y) in new[] { (0, 0), (current.Width - 1, 0), (0, current.Height - 1),
                    (current.Width - 1, current.Height - 1), (current.Width / 2, current.Height / 2) }) {
                double dx = matrix[0, 0] * x + matrix[0, 1] * y + matrix[0, 2]
                    - (expected[0, 0] * x + expected[0, 1] * y + expected[0, 2]);
                double dy = matrix[1, 0] * x + matrix[1, 1] * y + matrix[1, 2]
                    - (expected[1, 0] * x + expected[1, 1] * y + expected[1, 2]);
                Assert.That(Math.Sqrt(dx * dx + dy * dy), Is.LessThan(0.75), $"Registration disagrees with independent image patches at ({x}, {y}).");
            }
        }

        private static object Prepare(ArchiveStarCatalog frame) {
            MethodInfo? prepare = typeof(ImageTransformer2).GetMethod("PrepareAlignmentStars");
            return prepare == null ? ImageTransformer2.Instance.GetStars(frame.ToDetections(), frame.Width, frame.Height)
                : prepare.Invoke(ImageTransformer2.Instance, new object[] { frame.ToDetections(), frame.Width, frame.Height })!;
        }

        private static AlignmentResult Solve(object current, object reference, int width, int height) {
            MethodInfo? supported = typeof(ImageTransformer2).GetMethods().SingleOrDefault(method => method.Name == "ComputeAlignment"
                && method.GetParameters()[0].ParameterType.Name == "AlignmentStarCatalog");
            return supported == null
                ? ImageTransformer2.Instance.ComputeAlignment((List<Accord.Point>)current, (List<Accord.Point>)reference, width, height)
                : (AlignmentResult)supported.Invoke(ImageTransformer2.Instance, new object?[] { current, reference, width, height, null, CancellationToken.None })!;
        }

        private static ArchiveStarCatalog[] ReadFixture() {
            using Stream stream = typeof(HocusFocusRegressionTests).Assembly.GetManifestResourceStream("nina.plugin.livestack.test.TestData.HocusFocusSh2126.jsonl.gz")!;
            using GZipStream compressed = new(stream, CompressionMode.Decompress);
            using StreamReader reader = new(compressed);
            List<ArchiveStarCatalog> frames = new();
            JsonSerializerOptions options = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
            while (reader.ReadLine() is string line) frames.Add(JsonSerializer.Deserialize<ArchiveStarCatalog>(line, options)!);
            Assert.That(frames, Has.Count.EqualTo(2));
            return frames.ToArray();
        }
    }
}
