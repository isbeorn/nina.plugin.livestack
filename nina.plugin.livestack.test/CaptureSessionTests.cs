using Moq;
using NINA.Core.Model;
using NINA.Image.FileFormat;
using NINA.Image.Interfaces;
using NINA.Image.ImageData;
using NINA.Image.ImageAnalysis;
using NINA.Plugin.Interfaces;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.LivestackDockables;
using NINA.Plugin.Livestack.Image;

namespace nina.plugin.livestack.test {
    [NonParallelizable]
    [Apartment(ApartmentState.STA)]
    public class CaptureSessionTests {
        [Test]
        public async Task CaptureReturnsBeforeBackgroundStatisticsAndStopWaitsForIt() {
            await using CaptureTestContext host = new();
            Mock<IImageData> image = host.Image();
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<IImageStatistics> statistics = new(TaskCreationOptions.RunContinuationsAsynchronously);
            image.SetupGet(i => i.Statistics).Returns(new Nito.AsyncEx.AsyncLazy<IImageStatistics>(() => {
                started.TrySetResult();
                return statistics.Task;
            }));
            Task run = host.Dockable.StartLiveStackCommand.ExecuteAsync(null);
            await host.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try {
                await host.Capture!(host, CaptureTestContext.Event(image.Object)).WaitAsync(TimeSpan.FromSeconds(5));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Task stop = host.Dockable.StopAsync();
                Assert.That(stop.IsCompleted, Is.False, "Stop must wait for the statistics task it started.");
                statistics.TrySetResult(Mock.Of<IImageStatistics>());
                await stop.WaitAsync(TimeSpan.FromSeconds(5));
                image.VerifyGet(i => i.Data, Times.Never);
                host.Factory.Verify(f => f.CreateBaseImageData(It.IsAny<IImageArray>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<ImageMetaData>()), Times.Never);
            } finally {
                statistics.TrySetResult(Mock.Of<IImageStatistics>());
                await host.Dockable.StopAsync();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        [TestCase("FLAT")]
        [TestCase("DARK")]
        public async Task NonLightCapturesDoNotStartBackgroundPreparation(string type) {
            await using CaptureTestContext host = new();
            Mock<IImageData> image = host.Image(type);
            Task run = host.Dockable.StartLiveStackCommand.ExecuteAsync(null);
            await host.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.Capture!(host, CaptureTestContext.Event(image.Object));
            await host.Dockable.StopAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            image.VerifyGet(i => i.Statistics, Times.Never);
            image.Verify(i => i.SaveToDisk(It.IsAny<FileSaveInfo>(), It.IsAny<CancellationToken>(), true, It.IsAny<IList<ImagePattern>>()), Times.Never);
        }

        [Test]
        public async Task ThirdCaptureWaitsForCapacityAndCancellationDropsQueuedPreparation() {
            await using CaptureTestContext host = new();
            Mock<IImageData> first = host.Image();
            Mock<IImageData> second = host.Image();
            Mock<IImageData> third = host.Image();
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<IImageStatistics> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            first.SetupGet(i => i.Statistics).Returns(new Nito.AsyncEx.AsyncLazy<IImageStatistics>(() => { started.TrySetResult(); return release.Task; }));
            Task run = host.Dockable.StartLiveStackCommand.ExecuteAsync(null);
            await host.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task waiting = Task.CompletedTask;
            try {
                await host.Capture!(host, CaptureTestContext.Event(first.Object)).WaitAsync(TimeSpan.FromSeconds(5));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await host.Capture!(host, CaptureTestContext.Event(second.Object)).WaitAsync(TimeSpan.FromSeconds(5));
                waiting = host.Capture!(host, CaptureTestContext.Event(third.Object));
                Assert.That(waiting.IsCompleted, Is.False);
                Task stop = host.Dockable.StopAsync();
                await waiting.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(stop.IsCompleted, Is.False);
                release.TrySetResult(Mock.Of<IImageStatistics>());
                await stop.WaitAsync(TimeSpan.FromSeconds(5));
                second.VerifyGet(i => i.Statistics, Times.Never);
                third.VerifyGet(i => i.Statistics, Times.Never);
                Assert.That(host.Dockable.QueueEntries, Is.Zero);
            } finally {
                release.TrySetResult(Mock.Of<IImageStatistics>());
                await host.Dockable.StopAsync();
                await Task.WhenAll(waiting, run).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Test]
        public async Task BackgroundPreparationPreservesOrderAndRecoversItsSlotAfterFailure() {
            await using CaptureTestContext host = new();
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            List<int> prepared = new();
            List<int> processed = new();
            await using FrameProcessingSession session = new((item, token) => { processed.Add(int.Parse(Path.GetFileNameWithoutExtension(item.Path))); return Task.CompletedTask; }, () => { });
            Func<int, Func<CancellationToken, Task<LiveStackItem>>> prepare = index => async token => {
                prepared.Add(index);
                if (index == 0) { started.TrySetResult(); await release.Task; throw new IOException("Preparation failed"); }
                string path = Path.Combine(host.DirectoryPath, index + ".fits");
                File.WriteAllText(path, "frame");
                IImageData image = host.Image().Object;
                return new LiveStackItem(path, "target", "L", 60, 100, 10, 16, 16, 16, false, image.StarDetectionAnalysis, image.MetaData);
            };
            await session.EnqueueBackgroundAsync(prepare(0));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.EnqueueBackgroundAsync(prepare(1));
            Task waiting = session.EnqueueBackgroundAsync(prepare(2));
            try {
                Assert.That(waiting.IsCompleted, Is.False);
                Assert.That(prepared, Is.EqualTo(new[] { 0 }));
                release.TrySetResult();
                await waiting.WaitAsync(TimeSpan.FromSeconds(5));
                await session.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(prepared, Is.EqualTo(new[] { 0, 1, 2 }));
                Assert.That(processed, Is.EqualTo(new[] { 1, 2 }));
                Assert.That(Directory.GetFiles(host.DirectoryPath), Is.Empty);
            } finally {
                release.TrySetResult();
                session.Cancel();
                await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Test]
        public async Task FailedSaveLeavesOnlySessionOwnedPartialFilesWhichStopRemoves() {
            await using CaptureTestContext host = new();
            string unrelated = Path.Combine(host.DirectoryPath, "temp", "unrelated.fits");
            Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
            File.WriteAllText(unrelated, "keep");
            Mock<IImageData> bad = host.Image();
            Mock<IImageData> good = host.Image();
            TaskCompletionSource saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bad.Setup(i => i.SaveToDisk(It.IsAny<FileSaveInfo>(), It.IsAny<CancellationToken>(), true, It.IsAny<IList<ImagePattern>>()))
                .Returns((FileSaveInfo info, CancellationToken token, bool force, IList<ImagePattern> patterns) => {
                    Directory.CreateDirectory(info.FilePath);
                    File.WriteAllText(Path.Combine(info.FilePath, "partial.fits"), "partial");
                    return Task.FromException<string>(new IOException("Native write failed"));
                });
            good.Setup(i => i.SaveToDisk(It.IsAny<FileSaveInfo>(), It.IsAny<CancellationToken>(), true, It.IsAny<IList<ImagePattern>>()))
                .Returns((FileSaveInfo info, CancellationToken token, bool force, IList<ImagePattern> patterns) => {
                    Directory.CreateDirectory(info.FilePath);
                    string path = Path.Combine(info.FilePath, "good.fits");
                    File.WriteAllText(path, "frame");
                    saved.TrySetResult();
                    return Task.FromResult(path);
                });
            Task run = host.Dockable.StartLiveStackCommand.ExecuteAsync(null);
            await host.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.Capture!(host, CaptureTestContext.Event(bad.Object));
            await host.Capture!(host, CaptureTestContext.Event(good.Object));
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.Dockable.StopAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(Directory.GetFiles(host.DirectoryPath, "*", SearchOption.AllDirectories), Is.EqualTo(new[] { unrelated }));
            Assert.That(File.ReadAllText(unrelated), Is.EqualTo("keep"));
        }

        [TestCase("LIGHT")]
        [TestCase("SNAPSHOT")]
        public async Task OriginalCaptureWritesForcedTemporaryFitsAndStacksWithoutCloningPixels(string type) {
            await using CaptureTestContext host = new();
            Mock<IImageData> image = host.Image(type);
            image.SetupGet(i => i.Properties).Returns(new ImageProperties(1000, 1000, 16, false, 100, 10));
            image.Object.MetaData.Image.ExposureTime = 60;
            image.Object.MetaData.Camera.Gain = 100;
            image.Object.MetaData.Camera.Offset = 10;
            image.SetupGet(i => i.StarDetectionAnalysis).Returns(new StarDetectionAnalysis {
                DetectedStars = 40, HFR = 3,
                StarList = AlignmentSafetyTests.CreateCatalog(40, 106).Select((point, i) => new DetectedStar {
                    Position = point, HFR = 3, MaxBrightness = 20000 + i * 20, Background = 500,
                    BoundingBox = new System.Drawing.Rectangle((int)point.X - 4, (int)point.Y - 4, 8, 8)
                }).ToList()
            });
            BaseImageData original = new(Enumerable.Repeat((ushort)5000, 1000000).ToArray(), 1000, 1000, 16, false,
                image.Object.MetaData, host.Profile.Object, host.StarDetection.Object, Mock.Of<IStarAnnotator>());
            FileSaveInfo? observed = null;
            IList<ImagePattern>? observedPatterns = null;
            image.Setup(i => i.SaveToDisk(It.IsAny<FileSaveInfo>(), It.IsAny<CancellationToken>(), true, It.IsAny<IList<ImagePattern>>()))
                .Returns((FileSaveInfo info, CancellationToken token, bool force, IList<ImagePattern> patterns) => {
                    observed = info;
                    observedPatterns = patterns;
                    return original.SaveToDisk(info, token, force, patterns);
                });
            TaskCompletionSource stacked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Broker.Setup(b => b.Publish(It.IsAny<IMessage>())).Callback<IMessage>(message => {
                if (message is LivestackBroadcast) stacked.TrySetResult();
            }).Returns(Task.CompletedTask);
            TaskCompletionSource<IImageStatistics> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            image.SetupGet(i => i.Statistics).Returns(new Nito.AsyncEx.AsyncLazy<IImageStatistics>(() => release.Task));
            ImagePattern custom = new("$$CUSTOM$$", "custom", "test") { Value = "original" };
            var captureEvent = CaptureTestContext.Event(image.Object);
            captureEvent.AddImagePattern(custom);
            Task run = host.Dockable.StartLiveStackCommand.ExecuteAsync(null);
            await host.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try {
                await host.Capture!(host, captureEvent).WaitAsync(TimeSpan.FromSeconds(5));
                custom.Value = "changed";
                captureEvent.AddImagePattern(new ImagePattern("$$LATE$$", "late"));
                release.TrySetResult(Mock.Of<IImageStatistics>());
                await stacked.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(observed!.FileType, Is.EqualTo(NINA.Core.Enum.FileTypeEnum.FITS));
                Assert.That(observedPatterns!.Select(p => (p.Key, p.Value)), Is.EqualTo(new[] { ("$$CUSTOM$$", "original") }));
                image.VerifyGet(i => i.Data, Times.Never);
                host.Factory.Verify(f => f.CreateBaseImageData(It.IsAny<IImageArray>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<ImageMetaData>()), Times.Never);
            } finally {
                release.TrySetResult(Mock.Of<IImageStatistics>());
                await host.Dockable.StopAsync();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.That(Directory.GetFiles(host.DirectoryPath, "*", SearchOption.AllDirectories), Is.Empty);
        }

        [Test]
        public async Task OldCaptureCallbackCannotFeedARestartedSession() {
            await using CaptureTestContext host = new();
            Mock<IImageData> oldImage = host.Image();
            Mock<IImageData> marker = host.Image();
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<IImageStatistics> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            marker.SetupGet(i => i.Statistics).Returns(new Nito.AsyncEx.AsyncLazy<IImageStatistics>(() => {
                entered.TrySetResult();
                return release.Task;
            }));
            Task first = host.Dockable.StartLiveStackCommand.ExecuteAsync(null);
            await host.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldCapture = host.Capture!;
            await host.Dockable.StopAsync();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Task second = host.Dockable.StartLiveStackCommand.ExecuteAsync(null);
            await host.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try {
                await oldCapture(host, CaptureTestContext.Event(oldImage.Object));
                await host.Capture!(host, CaptureTestContext.Event(marker.Object));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                oldImage.VerifyGet(i => i.Statistics, Times.Never);
                oldImage.Verify(i => i.SaveToDisk(It.IsAny<FileSaveInfo>(), It.IsAny<CancellationToken>(), true, It.IsAny<IList<ImagePattern>>()), Times.Never);
            } finally {
                release.TrySetResult(Mock.Of<IImageStatistics>());
                await host.Dockable.StopAsync();
                await second.WaitAsync(TimeSpan.FromSeconds(5));
            }
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
                Assert.That(session.QueueEntries, Is.InRange(0, 9), "At most eight saved frames and one pending write are retained.");
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
        public async Task CaptureReturnsBeforeSavingAndStopWaitsForItsTemporaryFile() {
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
                Assert.That(capture.IsCompletedSuccessfully, Is.True, "The callback must return while the background save is pending.");
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
