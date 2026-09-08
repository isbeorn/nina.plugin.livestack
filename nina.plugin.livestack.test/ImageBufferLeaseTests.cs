using NINA.Image.ImageData;
using NINA.Plugin.Livestack.Image;
using System.Buffers;
using System.Runtime.InteropServices;

namespace nina.plugin.livestack.test {
    public class ImageBufferLeaseTests {
        [Test]
        public void StandardOwnerExposesExactWritableMemoryOverTheRentedArray() {
            ImageBufferPool pool = new(4096, 2);
            using ImageBufferLease lease = pool.Rent(100);
            Assert.That(lease, Is.InstanceOf<IMemoryOwner<float>>());
            IMemoryOwner<float> owner = lease;

            Assert.That(MemoryMarshal.TryGetArray((ReadOnlyMemory<float>)owner.Memory, out ArraySegment<float> segment), Is.True);
            Assert.That(segment.Array, Is.SameAs(lease.Buffer));
            Assert.That(segment.Offset, Is.Zero);
            Assert.That(segment.Count, Is.EqualTo(100));
            owner.Memory.Span[0] = 0.25f;
            owner.Memory.Span[^1] = 0.75f;
            Assert.That(lease.Buffer[0], Is.EqualTo(0.25f));
            Assert.That(lease.Buffer[^1], Is.EqualTo(0.75f));
            lease.Buffer[50] = 0.5f;
            Assert.That(owner.Memory.Span[50], Is.EqualTo(0.5f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StandardOwnerIsInvalidatedByDisposalOrTransfer(bool detached) {
            ImageBufferPool pool = new(4096, 2);
            using ImageBufferLease lease = pool.Rent(100);
            Assert.That(lease, Is.InstanceOf<IMemoryOwner<float>>());
            IMemoryOwner<float> owner = lease;
            float[] pixels = lease.Buffer;
            if (detached) {
                Assert.That(lease.Detach(), Is.SameAs(pixels));
                Assert.Throws<ObjectDisposedException>(() => _ = owner.Memory);
            }
            owner.Dispose();
            owner.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = owner.Memory);
            Assert.Throws<ObjectDisposedException>(() => _ = lease.Buffer);
            using ImageBufferLease next = pool.Rent(100);
            Assert.That(ReferenceEquals(next.Buffer, pixels), Is.EqualTo(!detached));
            using ImageBufferLease another = pool.Rent(100);
            Assert.That(another.Buffer, Is.Not.SameAs(pixels), "Disposal must never return detached storage or return a buffer twice.");
        }

        [Test]
        public void ReturnedBuffersAreExactLengthAndReusedOnlyAfterDisposal() {
            ImageBufferPool pool = new(4096, 2);
            ImageBufferLease first = pool.Rent(100);
            float[] pixels = first.Buffer;
            using ImageBufferLease concurrent = pool.Rent(100);
            Assert.That(concurrent.Buffer, Is.Not.SameAs(pixels));
            first.Dispose();
            first.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = first.Buffer);
            using ImageBufferLease reused = pool.Rent(100);
            Assert.That(reused.Buffer, Is.SameAs(pixels));
            using ImageBufferLease another = pool.Rent(100);
            Assert.That(another.Buffer, Is.Not.SameAs(pixels), "Double disposal must not return the same array twice.");
            Assert.That(reused.Buffer, Has.Length.EqualTo(100));
        }

        [Test]
        public void DetachedStorageNeverReturnsToThePool() {
            ImageBufferPool pool = new(4096, 2);
            ImageBufferLease lease = pool.Rent(100);
            float[] owned = lease.Detach();
            lease.Dispose();
            Assert.Throws<ObjectDisposedException>(() => _ = lease.Buffer);
            Assert.Throws<ObjectDisposedException>(() => lease.Detach());
            using ImageBufferLease next = pool.Rent(100);
            Assert.That(next.Buffer, Is.Not.SameAs(owned));
        }

        [TestCase(399, 2, false)]
        [TestCase(400, 0, false)]
        [TestCase(400, 1, true)]
        public void RetentionHonorsByteAndBufferLimits(long bytes, int buffers, bool retained) {
            ImageBufferPool pool = new(bytes, buffers);
            ImageBufferLease lease = pool.Rent(100);
            float[] pixels = lease.Buffer;
            lease.Dispose();
            using ImageBufferLease next = pool.Rent(100);
            Assert.That(ReferenceEquals(next.Buffer, pixels), Is.EqualTo(retained));
        }

        [Test]
        public void TrimDiscardsIdleAndOutstandingRentalsAndAllowsNewReuse() {
            ImageBufferPool pool = new(4096, 2);
            ImageBufferLease active = pool.Rent(100);
            ImageBufferLease idle = pool.Rent(100);
            float[] activePixels = active.Buffer, idlePixels = idle.Buffer;
            idle.Dispose();
            pool.Trim();
            active.Dispose();
            ImageBufferLease next = pool.Rent(100);
            Assert.That(next.Buffer, Is.Not.SameAs(activePixels));
            Assert.That(next.Buffer, Is.Not.SameAs(idlePixels));
            float[] newPixels = next.Buffer;
            next.Dispose();
            using ImageBufferLease reused = pool.Rent(100);
            Assert.That(reused.Buffer, Is.SameAs(newPixels));
        }

        [Test]
        public void ChangingDimensionsDoesNotRetainThePreviousCameraBuffer() {
            ImageBufferPool pool = new(4096, 2);
            ImageBufferLease first = pool.Rent(100);
            float[] oldPixels = first.Buffer;
            first.Dispose();
            using ImageBufferLease different = pool.Rent(101);
            Assert.That(different.Buffer, Has.Length.EqualTo(101));
            using ImageBufferLease oldSize = pool.Rent(100);
            Assert.That(oldSize.Buffer, Is.Not.SameAs(oldPixels));
        }

        [Test]
        public void ReferenceTakesOwnershipButRejectedFrameIsReturned() {
            ImageBufferPool pool = new(8000000, 2);
            ImageProperties properties = new(1000, 1000, 16, false, 100, 10);
            List<Accord.Point> stars = AlignmentSafetyTests.CreateCatalog(40, 106);
            LiveStackBag bag = new("target", "L", properties, new ImageMetaData(), null);
            using ImageBufferLease reference = pool.Rent(1000000);
            float[] owned = reference.Buffer;
            Array.Fill(owned, 0.2f);
            Assert.That(bag.AlignAndAdd(reference, properties, stars).Success, Is.True);
            Assert.Throws<ObjectDisposedException>(() => _ = reference.Buffer);
            Assert.That(bag.Stack, Is.SameAs(owned));

            ImageBufferLease rejected = pool.Rent(1000000);
            float[] rejectedPixels = rejected.Buffer;
            Array.Fill(rejectedPixels, 0.8f);
            Assert.That(bag.AlignAndAdd(rejected, properties, AlignmentSafetyTests.CreateCatalog(40, 107)).Success, Is.False);
            rejected.Dispose();
            using ImageBufferLease reused = pool.Rent(1000000);
            Assert.That(reused.Buffer, Is.SameAs(rejectedPixels));
            Array.Fill(reused.Buffer, 0.6f);
            Assert.That(bag.AlignAndAdd(reused, properties, stars).Success, Is.True);
            Assert.That(reused.Buffer, Is.SameAs(rejectedPixels), "Later accepted frames stay temporary.");
            Assert.That(bag.Stack, Is.All.EqualTo(0.4f).Within(1e-6));
        }

        [Test]
        public void ExceptionAndCancellationReleaseTemporaryFrames() {
            ImageBufferPool pool = new(4096, 1);
            float[]? pixels = null;
            Assert.Throws<OperationCanceledException>(() => {
                using ImageBufferLease lease = pool.Rent(100);
                pixels = lease.Buffer;
                throw new OperationCanceledException();
            });
            using ImageBufferLease reused = pool.Rent(100);
            Assert.That(reused.Buffer, Is.SameAs(pixels));
        }

        [Test]
        public void ConcurrentRentalsNeverShareWritableStorage() {
            ImageBufferPool pool = new(4096, 2);
            System.Collections.Concurrent.ConcurrentDictionary<float[], byte> active = new();
            Parallel.For(0, 1000, iteration => {
                using ImageBufferLease lease = pool.Rent(100);
                Assert.That(active.TryAdd(lease.Buffer, 0), Is.True);
                Thread.SpinWait(100);
                Assert.That(active.TryRemove(lease.Buffer, out _), Is.True);
            });
        }
    }
}
