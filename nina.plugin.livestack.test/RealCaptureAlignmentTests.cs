using Accord;
using NINA.Image.ImageData;
using NINA.Plugin.Livestack.Image;
using System.Text.Json;

namespace nina.plugin.livestack.test {
    public class RealCaptureAlignmentTests {
        private static readonly Lazy<CaptureSequence[]> Sequences = new(LoadSequences);

        public static IEnumerable<TestCaseData> RecordedFrames() {
            foreach (CaptureSequence sequence in Sequences.Value) {
                foreach (CaptureFrame frame in sequence.Frames.Skip(1)) {
                    yield return new TestCaseData(sequence, frame).SetName($"Archive alignment: {sequence.Name}/{frame.File}");
                }
            }
        }

        [TestCaseSource(nameof(RecordedFrames))]
        public void RecordedFramePreservesVerifiedAlignment(CaptureSequence sequence, CaptureFrame frame) {
            AlignmentResult result = ImageTransformer2.Instance.ComputeAlignment(ToPoints(frame), ToPoints(sequence.Frames[0]), sequence.Width, sequence.Height);
            if (frame.ExpectedMatrix == null) {
                Assert.That(result.Success, Is.False, "The unsupported archive match must remain rejected.");
                return;
            }

            Assert.That(result.Success, Is.True, result.RejectionReason);
            // The recorded transforms were checked against actual star-image patches.
            // Allow small fitting changes while guarding orientation and full-frame registration.
            double[,] matrix = result.Matrix;
            foreach ((int x, int y) in new[] { (0, 0), (sequence.Width - 1, 0), (0, sequence.Height - 1),
                    (sequence.Width - 1, sequence.Height - 1), (sequence.Width / 2, sequence.Height / 2) }) {
                double dx = matrix[0, 0] * x + matrix[0, 1] * y + matrix[0, 2]
                    - (frame.ExpectedMatrix[0] * x + frame.ExpectedMatrix[1] * y + frame.ExpectedMatrix[2]);
                double dy = matrix[1, 0] * x + matrix[1, 1] * y + matrix[1, 2]
                    - (frame.ExpectedMatrix[3] * x + frame.ExpectedMatrix[4] * y + frame.ExpectedMatrix[5]);
                Assert.That(Math.Sqrt(dx * dx + dy * dy), Is.LessThan(0.75), $"Registration moved at ({x}, {y}).");
            }
        }

        [TestCase("2023-05-26_M101/B", "2024-01-11 Rosette/Ha")]
        [TestCase("2025-01-10_Leo_Triplet/R", "2022-08-06 M15/R")]
        [TestCase("2024-12-26 Lowers Nebula/Ha", "2023-05-26_M101/R")]
        public void DifferentRealFieldsAreRejectedInBothDirections(string firstName, string secondName) {
            CaptureSequence first = Sequences.Value.Single(sequence => sequence.Name == firstName);
            CaptureSequence second = Sequences.Value.Single(sequence => sequence.Name == secondName);
            Assert.That(ImageTransformer2.Instance.ComputeAlignment(ToPoints(first.Frames[0]), ToPoints(second.Frames[0]), second.Width, second.Height).Success, Is.False);
            Assert.That(ImageTransformer2.Instance.ComputeAlignment(ToPoints(second.Frames[0]), ToPoints(first.Frames[0]), first.Width, first.Height).Success, Is.False);
        }

        [Test]
        public void RejectedArchiveFramePreservesTheExistingStack() {
            CaptureSequence sequence = Sequences.Value.Single(sequence => sequence.Name == "2025-01-10_Leo_Triplet/R");
            CaptureFrame rejected = sequence.Frames.Single(frame => frame.ExpectedMatrix == null);
            List<Point> reference = ToPoints(sequence.Frames[0]);
            LiveStackBag bag = new("Leo Triplet", "R", new ImageProperties(sequence.Width, sequence.Height, 16, false, 100, 250), new ImageMetaData(), reference);
            float[] original = Enumerable.Repeat(0.4f, sequence.Width * sequence.Height).ToArray();
            bag.Add(original);
            AlignmentResult result = bag.AlignAndAdd(new float[original.Length], bag.Properties, ToPoints(rejected));

            Assert.That(result.Success, Is.False);
            Assert.That(bag.ImageCount, Is.EqualTo(1));
            Assert.That(bag.ReferenceImageStars, Is.SameAs(reference));
            Assert.That(bag.Stack, Is.SameAs(original));
            Assert.That(bag.Stack.All(value => value == 0.4f), Is.True, "Every stack pixel must remain unchanged.");
        }

        private static List<Point> ToPoints(CaptureFrame frame) {
            return frame.Stars.Select(point => new Point(point[0], point[1])).ToList();
        }

        private static CaptureSequence[] LoadSequences() {
            using Stream stream = typeof(RealCaptureAlignmentTests).Assembly.GetManifestResourceStream("nina.plugin.livestack.test.TestData.RealCaptureCatalogs.jsonl")!;
            using StreamReader reader = new(stream);
            List<CaptureSequence> sequences = new();
            while (reader.ReadLine() is string line) {
                sequences.Add(JsonSerializer.Deserialize<CaptureSequence>(line)!);
            }
            return sequences.ToArray();
        }

        public sealed record CaptureSequence(string Name, int Width, int Height, CaptureFrame[] Frames);
        public sealed record CaptureFrame(string File, string PierSide, float[][] Stars, double[]? ExpectedMatrix);
    }
}
