using Moq;
using NINA.Core.Model;
using NINA.Image.FileFormat;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack.Image;

namespace nina.plugin.livestack.test {
    [NonParallelizable]
    [Apartment(ApartmentState.STA)]
    public class CaptureSessionTests {
        [Test]
        public async Task OldCaptureCallbackCannotFeedARestartedSession() {
            await using CaptureTestContext host = new();
            Mock<IImageData> image = host.Image();
            Task first = host.Dockable.StartLiveStackCommand.ExecuteAsync(null);
            await host.Subscribed.Task;
            var oldCapture = host.Capture!;
            await host.Dockable.StopAsync();
            await first;
            Task second = host.Dockable.StartLiveStackCommand.ExecuteAsync(null);
            await host.Subscribed.Task;
            await oldCapture(host, CaptureTestContext.Event(image.Object));
            image.Verify(i => i.SaveToDisk(It.IsAny<FileSaveInfo>(), It.IsAny<CancellationToken>(), true, It.IsAny<IList<ImagePattern>>()), Times.Never);
            await host.Dockable.StopAsync();
            await second;
            Assert.That(host.Dockable.QueueEntries, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CompletingOrCancelingSessionCleansQueuedFilesAndRecoversFromFailure(bool cancel) {
            await using CaptureTestContext host = new();
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int processed = 0;
            await using FrameProcessingSession session = new(async (item, token) => {
                int index = Interlocked.Increment(ref processed);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                if (index == 2) throw new IOException("Failed frame");
            }, () => { });
            Task[] producers = Enumerable.Range(0, 20).Select(index => session.EnqueueAsync(token => {
                string path = Path.Combine(host.DirectoryPath, index + ".fits");
                File.WriteAllText(path, "frame");
                IImageData image = host.Image().Object;
                return Task.FromResult(new LiveStackItem(path, "target", "L", 60, 100, 10, 16, 16, 16, false, image.StarDetectionAnalysis, image.MetaData));
            })).ToArray();
            try {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Task completion = session.CompleteAsync();
                Assert.That(completion.IsCompleted, Is.False);
                Assert.That(session.QueueEntries, Is.InRange(1, 9), "At most eight saved frames and one pending write are retained.");
                if (cancel) session.Cancel();
                release.TrySetResult();
                await Task.WhenAll(producers.Append(completion)).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(processed, Is.EqualTo(cancel ? 1 : 20));
                Assert.That(session.QueueEntries, Is.Zero);
                Assert.That(Directory.GetFiles(host.DirectoryPath), Is.Empty);
            } finally {
                session.Cancel();
                release.TrySetResult();
                await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Test]
        public async Task CaptureAndStopWaitForInFlightPreparationAndRemoveItsFile() {
            await using CaptureTestContext host = new();
            Mock<IImageData> image = host.Image();
            TaskCompletionSource saving = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            string path = Path.Combine(host.DirectoryPath, "late.fits");
            image.Setup(i => i.SaveToDisk(It.IsAny<FileSaveInfo>(), It.IsAny<CancellationToken>(), true, It.IsAny<IList<ImagePattern>>()))
                .Returns(async () => { saving.TrySetResult(); await release.Task; File.WriteAllText(path, "frame"); return path; });
            Task run = host.Dockable.StartLiveStackCommand.ExecuteAsync(null);
            await host.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task capture = host.Capture!(host, CaptureTestContext.Event(image.Object));
            try {
                await saving.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(capture.IsCompleted, Is.False, "The host must retain ownership until preparation finishes.");
                host.Dockable.StartLiveStackCommand.Cancel();
                await Task.Delay(50);
                Assert.That(run.IsCompleted, Is.False, "Stopping must await even a save that ignores cancellation.");
                release.SetResult();
                await Task.WhenAll(capture, run).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(File.Exists(path), Is.False);
                Assert.That(host.Dockable.QueueEntries, Is.Zero);
                Assert.That(host.Capture, Is.Null);
            } finally {
                release.TrySetResult();
                host.Dockable.StartLiveStackCommand.Cancel();
                await Task.WhenAll(capture, run).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
