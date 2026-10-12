using NINA.Plugin.Livestack.Image;
using nina.plugin.livestack.benchmark;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace nina.plugin.livestack.test {
    public class M15SparseRegressionTests {
        [TestCase(2, false, true)]
        [TestCase(2, true, true)]
        [TestCase(4, false, true)]
        [TestCase(4, true, true)]
        [TestCase(3, false, false)]
        [TestCase(3, true, false)]
        public void SparseBuiltinFramesUseIndependentImageOracle(int currentIndex, bool reverse, bool requireAcceptance) {
            FixtureFrame[] frames = ReadFixture();
            FixtureFrame sparse = frames[currentIndex];
            FixtureFrame dense = frames.Single(frame => frame.Catalog.File == sparse.ReferenceFile);
            ArchiveStarCatalog current = reverse ? dense.Catalog : sparse.Catalog;
            ArchiveStarCatalog reference = reverse ? sparse.Catalog : dense.Catalog;
            AlignmentStarCatalog selectedCurrent = ImageTransformer2.Instance.PrepareAlignmentStars(current.ToDetections(), current.Width, current.Height);
            AlignmentStarCatalog selectedReference = ImageTransformer2.Instance.PrepareAlignmentStars(reference.ToDetections(), reference.Width, reference.Height);
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(selectedCurrent, selectedReference, current.Width, current.Height);
            TestContext.WriteLine($"Current={selectedCurrent.MatchStars.Count}; Reference={selectedReference.MatchStars.Count}; {result}");
            if (requireAcceptance) {
                Assert.That(result.Success, Is.True, result.RejectionReason);
            }
            if (!result.Success) {
                return;
            }

            double[,] expected = reverse
                ? MathNet.Numerics.LinearAlgebra.Double.DenseMatrix.OfArray(sparse.OracleMatrix).Inverse().ToArray()
                : sparse.OracleMatrix;
            foreach ((int x, int y) in new[] { (0, 0), (current.Width - 1, 0), (0, current.Height - 1),
                    (current.Width - 1, current.Height - 1), (current.Width / 2, current.Height / 2) }) {
                double dx = result.Matrix[0, 0] * x + result.Matrix[0, 1] * y + result.Matrix[0, 2]
                    - (expected[0, 0] * x + expected[0, 1] * y + expected[0, 2]);
                double dy = result.Matrix[1, 0] * x + result.Matrix[1, 1] * y + result.Matrix[1, 2]
                    - (expected[1, 0] * x + expected[1, 1] * y + expected[1, 2]);
                Assert.That(Math.Sqrt(dx * dx + dy * dy), Is.LessThan(0.75),
                    $"Accepted registration disagrees with independent image patches at ({x}, {y}).");
            }
        }

        private static FixtureFrame[] ReadFixture() {
            using Stream source = typeof(M15SparseRegressionTests).Assembly.GetManifestResourceStream("nina.plugin.livestack.test.TestData.M15SparseBuiltin.jsonl.gz")!;
            using GZipStream compressed = new(source, CompressionMode.Decompress);
            using StreamReader reader = new(compressed);
            List<FixtureFrame> frames = new();
            JsonSerializerOptions options = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
            while (reader.ReadLine() is string line) {
                ArchiveStarCatalog catalog = JsonSerializer.Deserialize<ArchiveStarCatalog>(line, options)!;
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement oracle = document.RootElement.GetProperty("OracleMatrix");
                double[,] matrix = new double[3, 3];
                for (int row = 0; row < 3; row++) {
                    for (int column = 0; column < 3; column++) {
                        matrix[row, column] = oracle[row][column].GetDouble();
                    }
                }
                frames.Add(new(catalog, matrix, document.RootElement.GetProperty("OracleReferenceFile").GetString()!));
            }
            Assert.That(frames, Has.Count.EqualTo(5));
            return frames.ToArray();
        }

        private sealed record FixtureFrame(ArchiveStarCatalog Catalog, double[,] OracleMatrix, string ReferenceFile);
    }
}
