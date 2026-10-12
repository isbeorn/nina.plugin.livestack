using Moq;
using NINA.Core.Model;
using NINA.Image.FileFormat;
using NINA.Image.Interfaces;
using Nito.AsyncEx;
using System.Windows.Threading;

namespace nina.plugin.livestack.test {
    // ReplayApplicationTests shuts down the process-wide WPF resource system.
    [Order(-1)]
    [NonParallelizable]
    [Apartment(ApartmentState.STA)]
    public class ShutdownTests {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void StopAndTeardownFinishWhileTheWpfDispatcherIsBlocked(bool teardown, bool saving) {
            CaptureTestContext host = new();
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext? previous = SynchronizationContext.Current;
            TaskCompletionSource saveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource releaseSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task run = Task.CompletedTask;
            Task stop = Task.CompletedTask;
            string? temporaryPath = null;
            try {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                run = host.Dockable.StartLiveStackCommand.ExecuteAsync(null);
                Assert.That(host.Capture, Is.Not.Null);
                if (saving) {
                    Mock<IImageData> image = host.Image();
                    image.Setup(i => i.SaveToDisk(It.IsAny<FileSaveInfo>(), It.IsAny<CancellationToken>(), true, It.IsAny<IList<ImagePattern>>()))
                        .Returns(async (FileSaveInfo info, CancellationToken token, bool force, IList<ImagePattern> patterns) => {
                            Directory.CreateDirectory(info.FilePath);
                            temporaryPath = Path.Combine(info.FilePath, "pending.fits");
                            File.WriteAllText(temporaryPath, "partial frame");
                            saveStarted.TrySetResult();
                            await releaseSave.Task;
                            return temporaryPath;
                        });
                    host.Capture!(host, CaptureTestContext.Event(image.Object)).GetAwaiter().GetResult();
                    Assert.That(saveStarted.Task.Wait(TimeSpan.FromSeconds(5)), Is.True);
                }

                // N.I.N.A. calls AsyncContext.Run(plugin.Teardown) inside its WPF closing handler.
                AsyncContext.Run(async () => {
                    stop = teardown ? host.Plugin.Teardown() : host.Dockable.StopAsync();
                    if (saving) Assert.That(stop.IsCompleted, Is.False, "Cleanup must wait for an in-flight save.");
                    releaseSave.TrySetResult();
                    await stop.WaitAsync(TimeSpan.FromSeconds(3));
                });

                Assert.That(run.IsCompletedSuccessfully, Is.True);
                Assert.That(host.Capture, Is.Null);
                Assert.That(host.Dockable.QueueEntries, Is.Zero);
                Assert.That(host.Dockable.IsExpanded, Is.True);
                if (saving) {
                    Assert.That(File.Exists(temporaryPath), Is.False);
                    Assert.That(Directory.GetDirectories(Path.Combine(host.DirectoryPath, "temp")), Is.Empty);
                }
            } finally {
                releaseSave.TrySetResult();
                host.Dockable.StartLiveStackCommand.Cancel();
                // Let a failing implementation finish its queued continuation so it cannot leak into other tests.
                DispatcherFrame frame = new();
                DispatcherTimer timeout = new(TimeSpan.FromSeconds(5), DispatcherPriority.Send,
                    (_, _) => frame.Continue = false, dispatcher);
                Task.WhenAll(run, stop).ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
                timeout.Stop();
                SynchronizationContext.SetSynchronizationContext(previous);
                AsyncContext.Run(() => host.DisposeAsync().AsTask());
            }
        }
    }
}