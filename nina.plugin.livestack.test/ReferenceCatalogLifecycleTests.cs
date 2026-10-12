using Accord;
using NINA.Image.ImageData;
using NINA.Plugin.Livestack.Image;

namespace nina.plugin.livestack.test {
    public class ReferenceCatalogLifecycleTests {
        [TestCase(1001, false)]
        [TestCase(40, true)]
        public void RepeatedReadsPreserveCatalogAndPredictionForNormalizedLegacyLists(int count, bool duplicate) {
            List<Point> stars = AlignmentSafetyTests.CreateCatalog(count, 1441);
            if (duplicate) {
                stars.Add(stars[0]);
            }
            LiveStackBag bag = Bag(stars);
            AlignmentStarCatalog catalog = bag.ReferenceCatalog;
            bag.CommitAlignment(AlignmentResult.Accepted(AlignmentSafetyTests.Matrix(tx: 2), 40, 1, 0, 1));
            object prediction = Prediction(bag)!;

            Assert.Multiple(() => {
                Assert.That(bag.ReferenceCatalog, Is.SameAs(catalog));
                Assert.That(bag.ReferenceImageStars, Is.SameAs(stars));
                Assert.That(Prediction(bag), Is.SameAs(prediction));
            });
        }

        [TestCase(0)]
        [TestCase(1000)]
        public void ChangingEffectiveLegacySupportReplacesCatalogAndClearsPrediction(int index) {
            List<Point> stars = AlignmentSafetyTests.CreateCatalog(1001, 1443);
            LiveStackBag bag = Bag(stars);
            AlignmentStarCatalog before = bag.ReferenceCatalog;
            bag.CommitAlignment(AlignmentResult.Accepted(AlignmentSafetyTests.Matrix(tx: 2), 40, 1, 0, 1));
            stars[index] = new Point(12, 14);

            AlignmentStarCatalog after = bag.ReferenceCatalog;
            Assert.Multiple(() => {
                Assert.That(after, Is.Not.SameAs(before));
                Assert.That(after.SupportStars, Does.Contain(stars[index]));
                Assert.That(bag.ReferenceCatalog, Is.SameAs(after));
                Assert.That(Prediction(bag), Is.Null);
            });
        }

        private static LiveStackBag Bag(List<Point> stars) {
            return new LiveStackBag("target", "L", new ImageProperties(1000, 1000, 16, false, 100, 10), new ImageMetaData(), stars);
        }

        [Test]
        public void ChangesOutsideTheBoundedLegacySupportDoNotInvalidateTheCatalog() {
            List<Point> stars = AlignmentSafetyTests.CreateCatalog(AlignmentStarCatalog.MaximumSupportStars + 1, 1445);
            LiveStackBag bag = Bag(stars);
            AlignmentStarCatalog catalog = bag.ReferenceCatalog;
            stars[^1] = new Point(12, 14);
            Assert.That(bag.ReferenceCatalog, Is.SameAs(catalog));
        }

        private static object? Prediction(LiveStackBag bag) {
            return typeof(LiveStackBag).GetField("lastCommittedMatrix", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(bag);
        }
    }
}
