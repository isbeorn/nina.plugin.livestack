using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace NINA.Plugin.Livestack.Image {
    /// <summary>Bounded, exact-length scratch storage. Rented pixels are uninitialized.</summary>
    internal sealed class ImageBufferPool {
        internal static ImageBufferPool Shared { get; } = new(256L * 1024 * 1024, 2);
        private readonly long maximumRetainedBytes;
        private readonly int maximumRetainedBuffers;
        private readonly List<float[]> available = new();
        private readonly object syncRoot = new();
        private long retainedBytes;
        private long generation;

        internal ImageBufferPool(long maximumRetainedBytes, int maximumRetainedBuffers) {
            if (maximumRetainedBytes < 0 || maximumRetainedBuffers < 0) {
                throw new ArgumentOutOfRangeException(nameof(maximumRetainedBytes));
            }
            this.maximumRetainedBytes = maximumRetainedBytes;
            this.maximumRetainedBuffers = maximumRetainedBuffers;
        }

        internal ImageBufferLease Rent(int length) {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            long rentalGeneration;
            lock (syncRoot) {
                rentalGeneration = generation;
                for (int i = 0; i < available.Count; i++) {
                    if (available[i].Length == length) {
                        float[] buffer = available[i];
                        available.RemoveAt(i);
                        retainedBytes -= (long)length * sizeof(float);
                        return new ImageBufferLease(this, buffer, rentalGeneration);
                    }
                }
                // A camera-size change should not retain unrelated large scratch frames.
                available.Clear();
                retainedBytes = 0;
            }
            return new ImageBufferLease(this, GC.AllocateUninitializedArray<float>(length), rentalGeneration);
        }

        internal void Trim() {
            lock (syncRoot) {
                available.Clear();
                retainedBytes = 0;
                generation++;
            }
        }

        internal void Return(float[] buffer, long rentalGeneration) {
            long bytes = (long)buffer.Length * sizeof(float);
            lock (syncRoot) {
                if (rentalGeneration == generation && available.Count < maximumRetainedBuffers
                    && bytes <= maximumRetainedBytes - retainedBytes) {
                    available.Add(buffer);
                    retainedBytes += bytes;
                }
            }
        }
    }

    /// <summary>One owner returns scratch storage or detaches it for permanent stack ownership.</summary>
    internal sealed class ImageBufferLease : IMemoryOwner<float> {
        private readonly ImageBufferPool pool;
        private readonly long generation;
        private float[] buffer;

        internal ImageBufferLease(ImageBufferPool pool, float[] buffer, long generation) {
            this.pool = pool;
            this.buffer = buffer;
            this.generation = generation;
        }

        internal float[] Buffer => buffer ?? throw new ObjectDisposedException(nameof(ImageBufferLease));

        public Memory<float> Memory => Buffer.AsMemory();

        internal float[] Detach() {
            return Interlocked.Exchange(ref buffer, null) ?? throw new ObjectDisposedException(nameof(ImageBufferLease));
        }

        public void Dispose() {
            float[] released = Interlocked.Exchange(ref buffer, null);
            if (released != null) {
                pool.Return(released, generation);
            }
        }
    }
}
