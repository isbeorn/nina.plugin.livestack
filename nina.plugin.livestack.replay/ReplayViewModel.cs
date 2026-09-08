using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.LivestackDockables;
using System.Collections.ObjectModel;
using System.IO;

namespace NINA.Plugin.Livestack.Replay {
    public partial class ReplayViewModel : ObservableObject, IAsyncDisposable {
        private int nextFrame, accepted, skipped, failed;
        private bool stopRequested;
        private Task running = Task.CompletedTask;
        private Task? disposal;
        private Guid session = Guid.NewGuid();
        public ReplayHost Host { get; }
        public ObservableCollection<ReplayFile> Files { get; } = new();
        public ObservableCollection<string> Log { get; } = new();
        public ObservableCollection<CalibrationFrameMeta> Masters { get; } = new();
        public string[] SensorModes { get; } = { "Mono", "RGGB", "BGGR", "GRBG", "GBRG" };
        public CalibrationFrameType[] MasterTypes { get; } = Enum.GetValues<CalibrationFrameType>();

        [ObservableProperty] private string sensorMode = "Mono";
        [ObservableProperty] private string pathText = "";
        [ObservableProperty] private string status = "Add capture files or folders to begin.";
        [ObservableProperty] private string progress = "0 / 0 processed";
        [ObservableProperty] private ReplayFile? currentFile;
        [ObservableProperty] private CalibrationFrameType masterType = CalibrationFrameType.FLAT;
        [ObservableProperty] private CalibrationFrameMeta? selectedMaster;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIdle))]
        [NotifyCanExecuteChangedFor(nameof(PlayCommand), nameof(StepCommand), nameof(ResetCommand), nameof(ClearCommand), nameof(RemoveMasterCommand))]
        private bool isBusy;
        public bool IsIdle => !IsBusy;

        public ReplayViewModel() {
            Host = new ReplayHost(new Progress<string>(text => Status = text), new Progress<string>(AppendLog));
        }

        private void AppendLog(string message) {
            Log.Add(message);
            while (Log.Count > 300) Log.RemoveAt(0);
        }

        public Task AddPathsAsync(IEnumerable<string> paths) {
            return BeginOperation(async () => {
                Status = "Finding capture files...";
                string[] inputs = Files.Select(file => file.Path).Concat(paths).ToArray();
                string[] ordered = await Task.Run(() => CaptureFiles.Find(inputs));
                await ResetStacksAsync();
                Files.Clear();
                foreach (string path in ordered) Files.Add(new ReplayFile(path));
                Status = $"Ready: {Files.Count} files, ordered by capture timestamp in the filename.";
            });
        }

        [RelayCommand(CanExecute = nameof(CanRun))]
        private Task Play() => BeginOperation(() => RunAsync(false));

        [RelayCommand(CanExecute = nameof(CanRun))]
        private Task Step() => BeginOperation(() => RunAsync(true));

        private bool CanRun() => !IsBusy && nextFrame < Files.Count;

        private Task BeginOperation(Func<Task> operation) {
            if (IsBusy || disposal != null) return Task.CompletedTask;
            IsBusy = true;
            running = RunOperationAsync(operation);
            return running;
        }

        private async Task RunOperationAsync(Func<Task> operation) {
            try {
                await operation();
            } catch (Exception ex) {
                Status = ex.Message;
                AppendLog(ex.Message);
            } finally {
                IsBusy = false;
                UpdateProgress();
            }
        }

        private async Task RunAsync(bool singleFrame) {
            stopRequested = false;
            if (nextFrame < Files.Count) {
                do {
                    ReplayFile file = Files[nextFrame];
                    CurrentFile = file;
                    file.Result = "Processing";
                    Status = "Loading " + file.Name;
                    try {
                        bool added = await Task.Run(() => Host.ProcessFileAsync(file.Path, SensorMode, session, CancellationToken.None));
                        file.Result = added ? "Accepted" : "Skipped";
                        if (added) accepted++; else skipped++;
                    } catch (Exception ex) {
                        file.Result = "Error";
                        failed++;
                        AppendLog(file.Name + ": " + ex.Message);
                    }
                    nextFrame++;
                    UpdateProgress();
                } while (!singleFrame && !stopRequested && nextFrame < Files.Count);
                Status = nextFrame == Files.Count ? "Replay complete." : "Paused. Play continues with the next frame.";
            }
        }

        [RelayCommand]
        private void Stop() {
            stopRequested = true;
            if (IsBusy) Status = "Stopping after the current frame...";
        }

        [RelayCommand(CanExecute = nameof(IsIdle))]
        private Task Reset() {
            return BeginOperation(async () => {
                await ResetStacksAsync();
                Status = "Stacks reset. Ready to replay from the beginning.";
            });
        }

        private async Task ResetStacksAsync() {
            Host.Dockable.SelectedTab = null!;
            foreach (IStackTab tab in Host.Dockable.Tabs.ToArray()) await Host.Dockable.RemoveTabCommand.ExecuteAsync(tab);
            nextFrame = accepted = skipped = failed = 0;
            session = Guid.NewGuid();
            CurrentFile = null;
            foreach (ReplayFile file in Files) file.Result = "Queued";
        }

        [RelayCommand(CanExecute = nameof(IsIdle))]
        private Task Clear() {
            return BeginOperation(async () => {
                await ResetStacksAsync();
                Files.Clear();
                Status = "Add capture files or folders to begin.";
            });
        }

        public Task AddMastersAsync(IEnumerable<string> paths) {
            return BeginOperation(async () => {
                foreach (string path in paths) {
                    if (Masters.Any(master => master.Type == MasterType && string.Equals(master.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                    CalibrationFrameMeta master = await Task.Run(() => Host.ReadMaster(path, MasterType));
                    LivestackMediator.CalibrationVM.AddCalibrationFrame(master);
                    Masters.Add(master);
                }
                Status = "Calibration masters ready. Verify their filter, gain and offset in the table.";
            });
        }

        [RelayCommand(CanExecute = nameof(IsIdle))]
        private void RemoveMaster() {
            if (SelectedMaster == null) return;
            LivestackMediator.CalibrationVM.BiasLibrary.Remove(SelectedMaster);
            LivestackMediator.CalibrationVM.DarkLibrary.Remove(SelectedMaster);
            LivestackMediator.CalibrationVM.FlatLibrary.Remove(SelectedMaster);
            Masters.Remove(SelectedMaster);
        }

        private void UpdateProgress() {
            Progress = $"{nextFrame} / {Files.Count} processed | {accepted} accepted | {skipped} skipped | {failed} errors";
            PlayCommand.NotifyCanExecuteChanged();
            StepCommand.NotifyCanExecuteChanged();
        }

        public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());

        private async Task DisposeCoreAsync() {
            Stop();
            await running;
            await Host.DisposeAsync();
        }
    }

    public partial class ReplayFile(string path) : ObservableObject {
        public string Path { get; } = path;
        public string Name => System.IO.Path.GetFileName(Path);
        [ObservableProperty] private string result = "Queued";
    }
}
