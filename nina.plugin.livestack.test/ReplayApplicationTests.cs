using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.LivestackDockables;
using NINA.Plugin.Livestack.Replay;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace nina.plugin.livestack.test {
    [NonParallelizable]
    [Apartment(ApartmentState.STA)]
    public class ReplayApplicationTests {
        private Application application = null!;
        private Thread uiThread = null!;

        [OneTimeSetUp]
        public void StartApplication() {
            TaskCompletionSource<Application> started = new();
            uiThread = new Thread(() => {
                Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Startup += (_, _) => started.SetResult(app);
                app.Run();
            }) { IsBackground = true };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            application = started.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }

        [OneTimeTearDown]
        public void StopApplication() {
            application.Dispatcher.Invoke(application.Shutdown);
            Assert.That(uiThread.Join(TimeSpan.FromSeconds(10)), Is.True);
        }
        private static string Sample(string name) {
            return Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "nina.plugin.livestack.benchmark", "BenchmarkData", name));
        }

        [Test]
        public void FileDiscoveryOrdersAcrossFiltersDeduplicatesAndIgnoresNonImages() {
            string directory = Path.Combine(Path.GetTempPath(), "ReplayInputs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try {
                string first = Path.Combine(directory, "R_2025-01-11_01-00-00_LIGHT.fits");
                string last = Path.Combine(directory, "B_2025-01-11_02-00-00_LIGHT.xisf");
                File.WriteAllText(last, "");
                File.WriteAllText(first, "");
                File.WriteAllText(Path.Combine(directory, "notes.txt"), "");
                Assert.That(CaptureFiles.Find(new[] { directory, first }), Is.EqualTo(new[] { first, last }));
                Assert.Throws<FileNotFoundException>(() => CaptureFiles.Find(new[] { Path.Combine(directory, "missing.fits") }));
            } finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void WindowCommandsReplayRealFitsStopResumeResetAndReleaseFiles() {
            RunWithDispatcher(async () => {
                ReplayViewModel model = new();
                MainWindow window = new(model) { ShowActivated = false, ShowInTaskbar = false, Left = -10000 };
                string working = model.Host.WorkingDirectory;
                string source = Sample("light_b_1.fits");
                byte[] originalHash = SHA256.HashData(File.ReadAllBytes(source));
                window.Show();
                try {
                    Button step = (Button)window.FindName("StepButton");
                    Button play = (Button)window.FindName("PlayButton");
                    Assert.That(step.Command, Is.SameAs(model.StepCommand));
                    Assert.That(step.Command.CanExecute(null), Is.False);
                    await model.AddPathsAsync(new[] { source, Sample("light_b_2.fits") });
                    Assert.That(step.Command.CanExecute(null), Is.True);
                    Task run = model.PlayCommand.ExecuteAsync(null);
                    Assert.That(model.IsBusy, Is.True);
                    Assert.That(play.Command.CanExecute(null), Is.False);
                    model.StopCommand.Execute(null);
                    await run;

                    Assert.That(model.Files.Count(file => file.Result == "Accepted"), Is.EqualTo(1), string.Join("\n", model.Log));
                    Assert.That(model.Files.Count(file => file.Result == "Queued"), Is.EqualTo(1));
                    await model.StepCommand.ExecuteAsync(null);
                    LiveStackTab tab = model.Host.Dockable.Tabs.OfType<LiveStackTab>().Single();
                    Assert.That(tab.StackCount, Is.EqualTo(2), string.Join("\n", model.Log));
                    Assert.That(tab.StackImage, Is.Not.Null);
                    Assert.That(tab.StackImage.IsFrozen, Is.True);
                    Assert.That(model.Log.Any(line => line.Contains("Matches=")), Is.True);
                    Assert.That(Directory.GetFiles(working, "*", SearchOption.AllDirectories), Is.Empty);
                    Assert.That(SHA256.HashData(File.ReadAllBytes(source)), Is.EqualTo(originalHash));

                    window.UpdateLayout();
                    RenderTargetBitmap screenshot = new((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    screenshot.Render(window);
                    PngBitmapEncoder encoder = new();
                    encoder.Frames.Add(BitmapFrame.Create(screenshot));
                    string output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "replay-window.png");
                    using (FileStream stream = File.Create(output)) encoder.Save(stream);
                    TestContext.AddTestAttachment(output);

                    await model.ResetCommand.ExecuteAsync(null);
                    Assert.That(model.Host.Dockable.Tabs, Is.Empty);
                    Assert.That(model.Files.All(file => file.Result == "Queued"), Is.True);
                    await model.StepCommand.ExecuteAsync(null);
                    Assert.That(model.Host.Dockable.Tabs.OfType<LiveStackTab>().Single().StackCount, Is.EqualTo(1));
                    await model.ClearCommand.ExecuteAsync(null);
                    Assert.That(model.Files, Is.Empty);
                    Assert.That(play.Command.CanExecute(null), Is.False);
                } finally {
                    TaskCompletionSource closed = new();
                    window.Closed += (_, _) => closed.SetResult();
                    window.Close();
                    await closed.Task;
                }
                Assert.That(Directory.Exists(working), Is.False);
            });
        }

        [TestCase(CalibrationFrameType.BIAS, "bias.fits")]
        [TestCase(CalibrationFrameType.DARK, "bias.fits")]
        [TestCase(CalibrationFrameType.FLAT, "flat_b.fits")]
        public void CalibrationSelectionAndRemovalUseTheRealLibrary(CalibrationFrameType type, string file) {
            RunWithDispatcher(async () => {
                await using ReplayViewModel model = new();
                model.MasterType = type;
                await model.AddMastersAsync(new[] { Sample(file), Sample(file) });
                Assert.That(model.Masters, Has.Count.EqualTo(1), string.Join("\n", model.Log));
                if (type == CalibrationFrameType.FLAT) Assert.That(model.Masters[0].Mean, Is.GreaterThan(0));
                var calibration = NINA.Plugin.Livestack.LivestackMediator.CalibrationVM;
                var library = type switch {
                    CalibrationFrameType.BIAS => calibration.BiasLibrary,
                    CalibrationFrameType.DARK => calibration.DarkLibrary,
                    _ => calibration.FlatLibrary
                };
                Assert.That(library.Single(), Is.SameAs(model.Masters[0]));
                model.SelectedMaster = model.Masters[0];
                model.RemoveMasterCommand.Execute(null);
                Assert.That(model.Masters, Is.Empty);
                Assert.That(library, Is.Empty);
            });
        }

        [TestCase("RGGB")]
        [TestCase("BGGR")]
        [TestCase("GRBG")]
        [TestCase("GBRG")]
        public void BayerReplayCreatesAlignedChannelsAndResetRemovesTheColorTab(string pattern) {
            RunWithDispatcher(async () => {
                await using ReplayViewModel model = new() { SensorMode = pattern };
                // Interpret the fixture as a Bayer mosaic to exercise the complete OSC runtime path.
                // This is a pipeline smoke test, not a color-accuracy fixture.
                await model.AddPathsAsync(new[] { Sample("light_b_1.fits"), Sample("light_b_2.fits") });
                await model.PlayCommand.ExecuteAsync(null);
                Assert.That(model.Files.All(file => file.Result == "Accepted"), Is.True, string.Join("\n", model.Log));
                Assert.That(model.Host.Dockable.Tabs.OfType<LiveStackTab>().Select(tab => tab.StackCount), Is.EquivalentTo(new[] { 2, 2, 2 }));
                ColorCombinationTab color = model.Host.Dockable.Tabs.OfType<ColorCombinationTab>().Single();
                Assert.That(color.StackImage, Is.Not.Null);
                Assert.That(color.StackImage.IsFrozen, Is.True);
                await model.ResetCommand.ExecuteAsync(null);
                Assert.That(model.Host.Dockable.Tabs, Is.Empty);
                await model.StepCommand.ExecuteAsync(null);
                Assert.That(model.Host.Dockable.Tabs.OfType<LiveStackTab>().Select(tab => tab.StackCount), Is.EquivalentTo(new[] { 1, 1, 1 }));
            });
        }

        [Test]
        public void StarlessLightIsRejectedAndItsTemporaryCopyIsRemoved() {
            RunWithDispatcher(async () => {
                await using ReplayViewModel model = new();
                string source = Path.Combine(model.Host.WorkingDirectory, "starless.fits");
                NINA.Plugin.Livestack.CFitsioFITSExtendedWriter writer = new(source, new ushort[256 * 256], 256, 256);
                writer.AddHeader("IMAGETYP", "LIGHT", "");
                writer.Close();
                await model.AddPathsAsync(new[] { source });
                await model.PlayCommand.ExecuteAsync(null);
                Assert.That(model.Files.Single().Result, Is.EqualTo("Skipped"), string.Join("\n", model.Log));
                Assert.That(model.Host.Dockable.Tabs, Is.Empty);
                Assert.That(model.Log.Any(line => line.Contains("not enough stars")), Is.True);
                Assert.That(Directory.GetFiles(model.Host.WorkingDirectory, "*", SearchOption.AllDirectories), Is.EqualTo(new[] { source }));
            });
        }

        [Test]
        public void NonLightAndUnreadableFramesDoNotPreventTheNextLightFromStacking() {
            RunWithDispatcher(async () => {
                string directory = Path.Combine(Path.GetTempPath(), "ReplayInvalid-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try {
                    string invalid = Path.Combine(directory, "invalid.fits");
                    File.WriteAllText(invalid, "not a FITS image");
                    await using ReplayViewModel model = new();
                    await model.AddPathsAsync(new[] { invalid, Sample("bias.fits"), Sample("light_b_1.fits") });
                    await model.PlayCommand.ExecuteAsync(null);
                    Assert.That(model.Files.Single(file => file.Path == invalid).Result, Is.EqualTo("Error"));
                    Assert.That(model.Files.Single(file => file.Name == "bias.fits").Result, Is.EqualTo("Skipped"));
                    Assert.That(model.Files.Single(file => file.Name == "light_b_1.fits").Result, Is.EqualTo("Accepted"), string.Join("\n", model.Log));
                    Assert.That(model.Host.Dockable.Tabs.OfType<LiveStackTab>().Single().StackCount, Is.EqualTo(1));
                    Assert.That(Directory.GetFiles(model.Host.WorkingDirectory, "*", SearchOption.AllDirectories), Is.Empty);
                } finally { Directory.Delete(directory, true); }
            });
        }

        [Test]
        public void ClosingDuringAFrameWaitsForItsCompletionAndDisposalIsIdempotent() {
            RunWithDispatcher(async () => {
                ReplayViewModel model = new();
                await model.AddPathsAsync(new[] { Sample("light_b_1.fits"), Sample("light_b_2.fits") });
                string working = model.Host.WorkingDirectory;
                Task run = model.PlayCommand.ExecuteAsync(null);
                await model.DisposeAsync();
                await model.DisposeAsync();
                Assert.That(run.IsCompletedSuccessfully, Is.True);
                Assert.That(model.Files.Count(file => file.Result == "Accepted"), Is.EqualTo(1));
                Assert.That(model.Files.Count(file => file.Result == "Queued"), Is.EqualTo(1));
                Assert.That(Directory.Exists(working), Is.False);
            });
        }

        [Test]
        public void CanceledInputDoesNotOpenTheSourceOrCreateTemporaryFiles() {
            RunWithDispatcher(async () => {
                await using ReplayViewModel model = new();
                Assert.ThrowsAsync<OperationCanceledException>(() => model.Host.ProcessFileAsync("missing.fits", "Mono", Guid.NewGuid(), new CancellationToken(true)));
                Assert.That(Directory.GetFiles(model.Host.WorkingDirectory, "*", SearchOption.AllDirectories), Is.Empty);
                Assert.That(model.Host.Dockable.Tabs, Is.Empty);
            });
        }

        [TestCase("Mono")]
        [TestCase("RGGB")]
        public void ReplayingReleasesReplacedPreviewsAndResetReleasesStacks(string sensorMode) {
            RunWithDispatcher(async () => {
                await using ReplayViewModel model = new() { SensorMode = sensorMode };
                await model.AddPathsAsync(new[] { Sample("light_b_1.fits"), Sample("light_b_2.fits") });
                await model.StepCommand.ExecuteAsync(null);
                // Color previews refresh lazily when their tab is selected.
                if (sensorMode != "Mono") model.Host.Dockable.SelectedTab = model.Host.Dockable.Tabs.OfType<ColorCombinationTab>().Single();
                WeakReference[] firstPreviews = ObservePreviews(model);
                Assert.That(firstPreviews, Has.Length.EqualTo(sensorMode == "Mono" ? 1 : 4));
                await model.StepCommand.ExecuteAsync(null);
                Assert.That(model.Files.All(file => file.Result == "Accepted"), Is.True, string.Join("\n", model.Log));
                await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                CollectReleasedImages();
                int replacedPreviewsAlive = firstPreviews.Count(reference => reference.IsAlive);
                WeakReference[] currentPreviews = ObservePreviews(model);
                WeakReference[] stacks = ObserveStacks(model);

                await model.ResetCommand.ExecuteAsync(null);
                await application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                CollectReleasedImages();
                Assert.Multiple(() => {
                    Assert.That(replacedPreviewsAlive, Is.Zero, "A new frame must release the previous preview, including any broadcast reference.");
                    Assert.That(currentPreviews.Count(reference => reference.IsAlive), Is.Zero, "Reset must release the last previews.");
                    Assert.That(stacks.Count(reference => reference.IsAlive), Is.Zero, "Reset must release the full-resolution stack pixels.");
                });
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] ObservePreviews(ReplayViewModel model) {
            return model.Host.Dockable.Tabs.Select(tab => new WeakReference(tab.StackImage)).ToArray();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] ObserveStacks(ReplayViewModel model) {
            return model.Host.Dockable.Tabs.OfType<LiveStackTab>().Select(tab => new WeakReference(tab.Stack)).ToArray();
        }

        private static void CollectReleasedImages() {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        private void RunWithDispatcher(Func<Task> action) {
            application.Dispatcher.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(45)).GetAwaiter().GetResult();
        }
    }
}
