using Moq;
using NINA.Core.Model;
using NINA.Image.FileFormat;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack;
using NINA.Plugin.Livestack.Instructions;
using NINA.WPF.Base.Interfaces.Mediator;

namespace nina.plugin.livestack.test {
    [NonParallelizable]
    [Apartment(ApartmentState.STA)]
    public class FlatSessionTests {
        [Test]
        public async Task CanceledExecutionDoesNotPublishAMaster() {
            await using CaptureTestContext host = new();
            StackFlats instruction = new(host.Profile.Object, host.ImageSave.Object, Mock.Of<IApplicationStatusMediator>()) { WaitForStack = true };
            instruction.SequenceBlockInitialize();
            try {
                for (int i = 0; i < 3; i++) await SendFlat(host, 0.4f);
                Assert.ThrowsAsync<OperationCanceledException>(() => instruction.Execute(null!, new CancellationToken(true)));
                Assert.That(LivestackMediator.CalibrationVM.SessionFlatLibrary, Is.Empty);
            } finally {
                instruction.SequenceBlockTeardown();
            }
        }

        [Test]
        public async Task BackgroundCompletionSurvivesTeardownAndKeepsIterationsSeparate([Values] bool saveCalibrated, [Values] bool wait) {
            await using CaptureTestContext host = new();
            host.Plugin.SaveCalibratedFlats = saveCalibrated;
            StackFlats instruction = new(host.Profile.Object, host.ImageSave.Object, Mock.Of<IApplicationStatusMediator>()) { WaitForStack = wait };
            instruction.SequenceBlockInitialize();
            try {
                for (int i = 0; i < 3; i++) await SendFlat(host, 0.3f);
                await instruction.Execute(null!, CancellationToken.None);
                instruction.SequenceBlockTeardown();
                instruction.SequenceBlockInitialize();
                for (int i = 0; i < 3; i++) await SendFlat(host, 0.7f);
                instruction.WaitForStack = true;
                await instruction.Execute(null!, CancellationToken.None);
                Assert.That(LivestackMediator.CalibrationVM.SessionFlatLibrary.Select(m => m.Mean).OrderBy(m => m), Is.EqualTo(new[] { 0.3f, 0.7f }).Within(1e-5));
                Assert.That(instruction.QueueEntries, Is.Zero);
                Assert.That(Directory.GetFiles(host.DirectoryPath, "*.fits", SearchOption.AllDirectories), Has.Length.EqualTo(saveCalibrated ? 8 : 2));
            } finally {
                instruction.SequenceBlockTeardown();
            }
        }

        [Test]
        public async Task AbortingAnUnexecutedBlockDoesNotAffectTheNextBlock() {
            await using CaptureTestContext host = new();
            StackFlats instruction = new(host.Profile.Object, host.ImageSave.Object, Mock.Of<IApplicationStatusMediator>()) { WaitForStack = true };
            instruction.SequenceBlockInitialize();
            await SendFlat(host, 0.3f);
            var oldCapture = host.Capture!;
            instruction.SequenceBlockTeardown();
            instruction.SequenceBlockInitialize();
            try {
                Mock<IImageData> ignored = host.Image("FLAT");
                await oldCapture(host, CaptureTestContext.Event(ignored.Object));
                ignored.Verify(i => i.SaveToDisk(It.IsAny<FileSaveInfo>(), It.IsAny<CancellationToken>(), true, It.IsAny<IList<ImagePattern>>()), Times.Never);
                for (int i = 0; i < 3; i++) await SendFlat(host, 0.7f);
                await instruction.Execute(null!, CancellationToken.None);
                Assert.That(LivestackMediator.CalibrationVM.SessionFlatLibrary.Single().Mean, Is.EqualTo(0.7f).Within(1e-5));
                Assert.That(Directory.GetFiles(host.DirectoryPath, "*.fits", SearchOption.AllDirectories), Has.Length.EqualTo(1));
            } finally {
                instruction.SequenceBlockTeardown();
            }
        }

        private static async Task SendFlat(CaptureTestContext host, float value) {
            Mock<IImageData> image = host.Image("FLAT");
            string path = Path.Combine(host.DirectoryPath, Guid.NewGuid().ToString("N") + ".fits");
            image.Setup(i => i.SaveToDisk(It.IsAny<FileSaveInfo>(), It.IsAny<CancellationToken>(), true, It.IsAny<IList<ImagePattern>>()))
                .Returns(() => {
                    CFitsioFITSExtendedWriter writer = new(path, Enumerable.Repeat(value, 256).ToArray(), 16, 16);
                    writer.PopulateHeaderCards(image.Object.MetaData);
                    writer.Close();
                    return Task.FromResult(path);
                });
            await host.Capture!(host, CaptureTestContext.Event(image.Object));
        }
    }
}
