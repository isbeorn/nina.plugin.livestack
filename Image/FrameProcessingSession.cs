using NINA.Core.Utility;
using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NINA.Plugin.Livestack.Image {
    /// <summary>Owns frame preparation, a bounded disk-backed queue and cleanup until all work has settled.</summary>
    internal sealed class FrameProcessingSession : IAsyncDisposable {
        private readonly Channel<LiveStackItem> frames = Channel.CreateBounded<LiveStackItem>(8);
        private readonly SemaphoreSlim preparation = new(1, 1);
        private readonly CancellationTokenSource cancellation;
        private readonly CancellationToken token;
        private readonly Func<LiveStackItem, CancellationToken, Task> process;
        private readonly Action queueChanged;
        private readonly object syncRoot = new();
        private readonly TaskCompletionSource producersFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool accepting = true;
        private int producers;
        private int queued;
        private int disposed;

        internal FrameProcessingSession(Func<LiveStackItem, CancellationToken, Task> process, Action queueChanged, CancellationToken token = default) {
            this.process = process;
            this.queueChanged = queueChanged;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            this.token = cancellation.Token;
            Completion = Task.Run(RunAsync);
        }

        internal Task Completion { get; }
        internal int QueueEntries => Volatile.Read(ref queued);

        // The capture host awaits this task, providing backpressure and retaining the raw image until saved.
        internal async Task EnqueueAsync(Func<CancellationToken, Task<LiveStackItem>> prepare) {
            lock (syncRoot) {
                if (!accepting || token.IsCancellationRequested) return;
                producers++;
            }
            LiveStackItem item = null;
            bool counted = false;
            try {
                await preparation.WaitAsync(token).ConfigureAwait(false);
                try {
                    item = await prepare(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref queued);
                    counted = true;
                    queueChanged();
                    await frames.Writer.WriteAsync(item, token).ConfigureAwait(false);
                    item = null; // The consumer now owns the temporary file.
                    counted = false;
                } finally {
                    preparation.Release();
                }
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            } catch (Exception ex) {
                Logger.Error(ex);
            } finally {
                if (counted) {
                    Interlocked.Decrement(ref queued);
                    queueChanged();
                }
                DeleteFrame(item);
                lock (syncRoot) {
                    producers--;
                    if (!accepting && producers == 0) producersFinished.TrySetResult();
                }
            }
        }

        // Graceful completion accepts no new captures but finishes all already admitted frames.
        internal async Task CompleteAsync() {
            StopAccepting();
            await producersFinished.Task.ConfigureAwait(false);
            frames.Writer.TryComplete();
            await Completion.ConfigureAwait(false);
        }

        internal void Cancel() {
            StopAccepting();
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }

        private void StopAccepting() {
            lock (syncRoot) {
                accepting = false;
                if (producers == 0) producersFinished.TrySetResult();
            }
        }

        private async Task RunAsync() {
            try {
                await foreach (LiveStackItem item in frames.Reader.ReadAllAsync(token).ConfigureAwait(false)) {
                    Interlocked.Decrement(ref queued);
                    queueChanged();
                    try {
                        token.ThrowIfCancellationRequested();
                        await process(item, token).ConfigureAwait(false);
                    } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                        throw;
                    } catch (Exception ex) {
                        Logger.Error(ex);
                    } finally {
                        DeleteFrame(item);
                        LiveStackMemoryPressure.CollectIfNeeded("frame completed");
                    }
                }
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            } finally {
                Cancel();
                await producersFinished.Task.ConfigureAwait(false);
                frames.Writer.TryComplete();
                while (frames.Reader.TryRead(out LiveStackItem item)) DeleteFrame(item);
                Interlocked.Exchange(ref queued, 0);
                queueChanged();
            }
        }

        private static void DeleteFrame(LiveStackItem item) {
            if (item == null) return;
            try { File.Delete(item.Path); } catch (Exception ex) { Logger.Error(ex); }
        }

        public async ValueTask DisposeAsync() {
            Cancel();
            await Completion.ConfigureAwait(false);
            if (Interlocked.Exchange(ref disposed, 1) == 0) {
                preparation.Dispose();
                cancellation.Dispose();
            }
        }
    }
}
