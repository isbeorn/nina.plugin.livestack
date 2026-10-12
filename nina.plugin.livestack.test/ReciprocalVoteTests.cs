using Accord;
using NINA.Plugin.Livestack.Image;
using System.Collections;
using System.Reflection;

namespace nina.plugin.livestack.test {
    public class ReciprocalVoteTests {
        [TestCase(false)]
        [TestCase(true)]
        public void EnoughDistinctiveMatchesSuppressGreedyAmbiguousTies(bool quads) {
            int anchors = quads ? 8 : 3;
            int size = anchors + 2;
            int[] votes = new int[size * size];
            for (int i = 0; i < anchors; i++) {
                votes[i * size + i] = 40 - i;
            }
            votes[anchors * size + anchors] = 10;
            votes[anchors * size + anchors + 1] = 10;
            votes[(anchors + 1) * size + anchors] = 10;
            votes[(anchors + 1) * size + anchors + 1] = 10;

            List<(int Ref, int Src, int Votes)> matches = Extract(votes, size, size, quads);

            Assert.That(matches, Is.EqualTo(Enumerable.Range(0, anchors).Select(i => (i, i, 40 - i))));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GreedyFallbackPreservesEqualVoteOrderAndUniqueAssignments(bool quads) {
            List<(int Ref, int Src, int Votes)> matches = Extract(new[] { 8, 8, 8, 8 }, 2, 2, quads);

            Assert.That(matches, Is.EqualTo(new[] { (0, 0, 8), (1, 1, 8) }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void GreedyFallbackKeepsTheAlreadyDistinctivePair(bool quads) {
            int[] votes = { 12, 0, 0, 0, 8, 8, 0, 8, 8 };

            Assert.That(Extract(votes, 3, 3, quads), Is.EqualTo(new[] { (0, 0, 12), (1, 1, 8), (2, 2, 8) }));
        }

        [Test]
        public void TriangleFallbackRetainsItsReducedThresholdAndFourVoteFloor() {
            int[] votes = new int[32 * 32];
            votes[0] = 10;
            votes[33] = 5;
            votes[66] = 4;
            votes[99] = 3;

            Assert.That(Extract(votes, 32, 32, false), Is.EqualTo(new[] { (0, 0, 10), (1, 1, 5), (2, 2, 4) }));
        }

        [Test]
        public void QuadFallbackRetainsItsGlobalVoteThreshold() {
            int[] votes = new int[9 * 9];
            for (int i = 0; i < 7; i++) {
                votes[i * 9 + i] = 80 - i;
            }
            votes[70] = 9;
            votes[80] = 8;

            Assert.That(Extract(votes, 9, 9, true), Is.EqualTo(Enumerable.Range(0, 7).Select(i => (i, i, 80 - i))));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReciprocalSelectionRejectsWeakerCompetitionInRectangularVotes(bool quads) {
            int anchors = quads ? 8 : 3;
            int rows = anchors + 1;
            int columns = anchors + 2;
            int[] votes = new int[rows * columns];
            for (int i = 0; i < anchors; i++) {
                votes[i * columns + i] = 40 - i;
            }
            votes[anchors * columns] = 8;
            votes[columns - 1] = 6;

            Assert.That(Extract(votes, rows, columns, quads), Is.EqualTo(Enumerable.Range(0, anchors).Select(i => (i, i, 40 - i))));
        }

        [TestCase(false, 162)]
        [TestCase(true, 160)]
        public void FinalPairCapAndDescendingOrderRemainSpecificToEachPath(bool quads, int expectedCount) {
            int[] votes = new int[162 * 162];
            for (int i = 0; i < 162; i++) {
                votes[i * 162 + i] = 1000 + i;
            }

            Assert.That(Extract(votes, 162, 162, quads), Is.EqualTo(Enumerable.Range(0, expectedCount).Select(i => (161 - i, 161 - i, 1161 - i))));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EmptyVotesDoNotInventPairs(bool quads) {
            Assert.That(Extract(new int[12], 3, 4, quads), Is.Empty);
            Assert.That(Extract(Array.Empty<int>(), 0, 0, quads), Is.Empty);
        }

        private static List<(int Ref, int Src, int Votes)> Extract(int[] votes, int rows, int columns, bool quads) {
            if (quads) {
                List<Point> reference = Enumerable.Range(0, rows).Select(i => new Point(i, 0)).ToList();
                List<Point> target = Enumerable.Range(0, columns).Select(i => new Point(i, 0)).ToList();
                MethodInfo method = typeof(ImageTransformer2).GetMethod("ComputeQuadMatchList", BindingFlags.NonPublic | BindingFlags.Instance)!;
                List<(Point Ref, Point Src, int Votes)> matches = (List<(Point Ref, Point Src, int Votes)>)method.Invoke(ImageTransformer2.Instance, new object[] { votes, reference, target })!;
                return matches.Select(pair => ((int)pair.Ref.X, (int)pair.Src.X, pair.Votes)).ToList();
            }
            MethodInfo triangleMethod = typeof(ImageTransformer2).GetMethod("ComputeMatchList", BindingFlags.NonPublic | BindingFlags.Instance)!;
            IEnumerable triangleMatches = (IEnumerable)triangleMethod.Invoke(ImageTransformer2.Instance, new object[] { votes, rows, columns, 150.0 })!;
            List<(int Ref, int Src, int Votes)> result = new();
            foreach (object match in triangleMatches) {
                Type type = match.GetType();
                result.Add(((int)type.GetProperty("Index1")!.GetValue(match)!, (int)type.GetProperty("Index2")!.GetValue(match)!, (int)type.GetProperty("Votes")!.GetValue(match)!));
            }
            return result;
        }
    }
}