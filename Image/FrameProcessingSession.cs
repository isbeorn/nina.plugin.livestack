using NINA.Core.Utility;
using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NINA.Plugin.Livestack.Image {
    /// <summary>Owns bounded FIFO preparation, a disk-backed queue and cleanup until all work has settled.</summary>
    internal sealed class FrameProcessingSession : IAsyncDisposable {
        private readonly Channel<Preparation> preparations = Channel.CreateUnbounded<Preparation>(new UnboundedChannelOptions { SingleReader = true });
        private readonly SemaphoreSlim preparationCapacity = new(2, 2);
        private readonly Channel<LiveStackItem> frames = Channel.CreateBounded<LiveStackItem>(8);
        private readonly CancellationTokenSource cancellation;
        private readonly CancellationToken token;
        private readonly Func<LiveStackItem, CancellationToken, Task> process;
        private readonly Action queueChanged;
        private readonly object syncRoot = new();
        private readonly TaskCompletionSource producersFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task preparationCompletion;
        private bool accepting = true;
        private int producers;
        private int queued;
        private int disposed;

        internal FrameProcessingSession(Func<LiveStackItem, CancellationToken, Task> process, Action queueChanged, CancellationToken token = default) {
            this.process = process;
            this.queueChanged = queueChanged;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            this.token = cancellation.Token;
            preparationCompletion = Task.Run(PrepareAsync);
            Completion = Task.Run(RunAsync);
        }

        internal Task Completion { get; }
        internal int QueueEntries => Volatile.Read(ref queued);

        /// <summary>Returns after bounded admission so capture preparation runs outside the host callback.</summary>
        internal async Task EnqueueBackgroundAsync(Func<CancellationToken, Task<LiveStackItem>> prepare) {
            await AdmitAsync(prepare).ConfigureAwait(false);
        }

        /// <summary>Retains the flat capture contract of waiting until preparation and disk admission finish.</summary>
        internal async Task EnqueueAsync(Func<CancellationToken, Task<LiveStackItem>> prepare) {
            Task prepared = await AdmitAsync(prepare).ConfigureAwait(false);
            await prepared.ConfigureAwait(false);
        }

        private async Task<Task> AdmitAsync(Func<CancellationToken, Task<LiveStackItem>> prepare) {
            lock (syncRoot) {
                if (!accepting || token.IsCancellationRequested) return Task.CompletedTask;
                producers++;
            }
            bool reserved = false;
            try {
                await preparationCapacity.WaitAsync(token).ConfigureAwait(false);
                reserved = true;
                token.ThrowIfCancellationRequested();
                Preparation work = new(prepare);
                preparations.Writer.TryWrite(work);
                reserved = false;
                return work.Finished.Task;
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                return Task.CompletedTask;
            } finally {
                if (reserved) preparationCapacity.Release();
                lock (syncRoot) {
                    producers--;
                    if (!accepting && producers == 0) producersFinished.TrySetResult();
                }
            }
        }

        private async Task PrepareAsync() {
            try {
                await foreach (Preparation work in preparations.Reader.ReadAllAsync(token).ConfigureAwait(false)) {
                    LiveStackItem item = null;
                    bool counted = false;
                    Func<CancellationToken, Task<LiveStackItem>> prepare = work.Prepare;
                    work.Prepare = null;
                    try {
                        token.ThrowIfCancellationRequested();
                        item = await prepare(token).ConfigureAwait(false);
                        prepare = null;
                        token.ThrowIfCancellationRequested();
                        Interlocked.Increment(ref queued);
                        counted = true;
                        queueChanged();
                        await frames.Writer.WriteAsync(item, token).ConfigureAwait(false);
                        item = null;
                        counted = false;
                    } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                    } catch (Exception ex) {
                        Logger.Error(ex);
                    } finally {
                        prepare = null;
                        if (counted) {
                            Interlocked.Decrement(ref queued);
                            queueChanged();
                        }
                        DeleteFrame(item);
                        preparationCapacity.Release();
                        work.Finished.TrySetResult();
                    }
                }
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            } finally {
                StopAccepting();
                await producersFinished.Task.ConfigureAwait(false);
                while (preparations.Reader.TryRead(out Preparation work)) {
                    work.Prepare = null;
                    preparationCapacity.Release();
                    work.Finished.TrySetResult();
                }
                frames.Writer.TryComplete();
            }
        }

        /// <summary>Finishes all accepted preparation and processing without accepting new captures.</summary>
        internal async Task CompleteAsync() {
            StopAccepting();
            await producersFinished.Task.ConfigureAwait(false);
            preparations.Writer.TryComplete();
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
                preparations.Writer.TryComplete();
                await preparationCompletion.ConfigureAwait(false);
                while (frames.Reader.TryRead(out LiveStackItem item)) DeleteFrame(item);
                Interlocked.Exchange(ref queued, 0);
                queueChanged();
            }
        }

        private static void DeleteFrame(LiveStackItem item) {
            if (item == null) return;
            try { File.Delete(item.Path); } catch (Exception ex) { Logger.Error(ex); }
        }

        private sealed class Preparation {
            internal Preparation(Func<CancellationToken, Task<LiveStackItem>> prepare) => Prepare = prepare;
            internal Func<CancellationToken, Task<LiveStackItem>> Prepare;
            internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async ValueTask DisposeAsync() {
            Cancel();
            await Completion.ConfigureAwait(false);
            if (Interlocked.Exchange(ref disposed, 1) == 0) {
                preparationCapacity.Dispose();
                cancellation.Dispose();
            }
        }
    }
}
