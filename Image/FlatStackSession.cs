using MathNet.Numerics.Statistics;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Image.FileFormat.FITS;
using NINA.Plugin.Livestack.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugin.Livestack.Image {
    /// <summary>One sequence block's calibrated files, master generation and cancellation lifetime.</summary>
    internal sealed class FlatStackSession {
        private readonly FrameProcessingSession frames;
        private readonly CancellationTokenSource cancellation = new();
        private readonly ICalibrationManager calibration = LivestackMediator.CreateCalibrationManager();
        private readonly Dictionary<string, List<string>> filesByFilter = new();
        private readonly string workingDirectory;
        private readonly string target;
        private readonly bool saveCalibrated;
        private readonly Action<CalibrationFrameMeta> addMaster;
        private readonly Task preceding;
        private Task completion;

        internal FlatStackSession(CalibrationVM library, string workingDirectory, string target, bool saveCalibrated, Task preceding, Action queueChanged) {
            this.workingDirectory = workingDirectory;
            this.target = target;
            this.saveCalibrated = saveCalibrated;
            this.preceding = preceding;
            // Keep publication attached to the originating profile's session library.
            addMaster = library.SessionFlatLibrary.Add;
            foreach (CalibrationFrameMeta master in library.BiasLibrary) calibration.RegisterBiasMaster(master);
            foreach (CalibrationFrameMeta master in library.DarkLibrary) calibration.RegisterDarkMaster(master);
            frames = new FrameProcessingSession(CalibrateAsync, queueChanged, cancellation.Token);
        }

        internal int QueueEntries => frames.QueueEntries;
        internal bool IsCompleting => completion != null;
        internal Task EnqueueAsync(Func<CancellationToken, Task<LiveStackItem>> prepare) => frames.EnqueueAsync(prepare);

        internal Task CompleteAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (completion == null) {
                Task drained = frames.CompleteAsync();
                completion = Task.Run(() => FinishAsync(drained, progress, token));
            }
            return completion;
        }

        internal Task AbortAsync() {
            return CompleteAsync(null, new CancellationToken(true));
        }

        private async Task CalibrateAsync(LiveStackItem item, CancellationToken token) {
            // A subsequent block may save its input, but only one block calibrates or stacks at a time.
            await preceding.WaitAsync(token).ConfigureAwait(false);
            string filter = string.IsNullOrWhiteSpace(item.Filter) ? LiveStackBag.NOFILTER : item.Filter;
            string folder = Path.Combine(workingDirectory, "calibrated", "flat", filter);
            Directory.CreateDirectory(folder);
            string output = CoreUtil.GetUniqueFilePath(Path.Combine(folder, Path.GetFileNameWithoutExtension(item.Path) + "_c.fits"), "{0}_{1}");
            using ImageBufferLease pixels = ImageBufferPool.Shared.Rent(AffineResampler.GetLength(item.Width, item.Height));
            using (CFitsioFITSReader reader = new(item.Path)) {
                calibration.ApplyFlatFrameCalibrationInto(reader, pixels.Buffer, item.Width, item.Height, item.ExposureTime,
                    item.Gain, item.Offset, item.Filter, item.IsBayered, token);
            }
            double median = pixels.Buffer.Median();
            token.ThrowIfCancellationRequested();
            try {
                CFitsioFITSExtendedWriter writer = new(output, pixels.Buffer, item.Width, item.Height);
                try {
                    writer.PopulateHeaderCards(item.MetaData);
                    writer.AddHeader("MEDIAN", median, "");
                } finally {
                    writer.Close();
                }
                if (!filesByFilter.TryGetValue(filter, out List<string> files)) {
                    filesByFilter.Add(filter, files = new List<string>());
                }
                files.Add(output);
            } catch {
                File.Delete(output);
                throw;
            }
        }

        private async Task FinishAsync(Task drained, IProgress<ApplicationStatus> progress, CancellationToken token) {
            using CancellationTokenRegistration registration = token.Register(() => cancellation.Cancel());
            try {
                await drained.ConfigureAwait(false);
                await preceding.WaitAsync(cancellation.Token).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                foreach (KeyValuePair<string, List<string>> filter in filesByFilter) {
                    cancellation.Token.ThrowIfCancellationRequested();
                    try {
                        progress?.Report(new ApplicationStatus { Status = $"Generating flat master for filter {filter.Key}" });
                        GenerateMaster(filter.Key, filter.Value, cancellation.Token);
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Logger.Error($"Failed to generate flat master for filter {filter.Key}", ex); }
                }
            } finally {
                await frames.DisposeAsync().ConfigureAwait(false);
                calibration.Dispose();
                if (!saveCalibrated) {
                    foreach (string path in filesByFilter.Values.SelectMany(files => files)) {
                        try { File.Delete(path); } catch (Exception ex) { Logger.Error(ex); }
                    }
                }
                // Unregister before disposing the source used by the cancellation callback.
                registration.Dispose();
                cancellation.Dispose();
            }
        }

        private void GenerateMaster(string filter, List<string> paths, CancellationToken token) {
            using DisposableList<CFitsioFITSReader> readers = new();
            foreach (string path in paths) {
                token.ThrowIfCancellationRequested();
                try { readers.Add(new CFitsioFITSReader(path)); }
                catch (Exception ex) { Logger.Error($"Failed to open flat frame {path}", ex); }
            }
            if (readers.Count < 3) throw new InvalidOperationException($"Not enough readable flats to generate a master for {filter}.");
            CFitsioFITSReader first = readers[0];
            if (readers.Any(reader => reader.Width != first.Width || reader.Height != first.Height)) {
                throw new InvalidOperationException("Flat dimensions must agree within a master.");
            }
            float[] stack = LivestackMediator.GetImageMath().PercentileClipping(readers, 0.2, 0.1, token);
            token.ThrowIfCancellationRequested();
            string folder = Path.Combine(workingDirectory, "stacks");
            Directory.CreateDirectory(folder);
            string output = CoreUtil.GetUniqueFilePath(Path.Combine(folder, CoreUtil.ReplaceAllInvalidFilenameChars($"MASTER_FLAT_{target}_{filter}.fits")), "{0}_{1}");
            try {
                CFitsioFITSExtendedWriter writer = new(output, stack, first.Width, first.Height);
                try { writer.PopulateHeaderCards(first.RestoreMetaData()); }
                finally { writer.Close(); }
                token.ThrowIfCancellationRequested();
                addMaster(new CalibrationFrameMeta(CalibrationFrameType.FLAT, output, 0, 0, 0, filter, first.Width, first.Height, (float)stack.Mean()));
            } catch {
                File.Delete(output);
                throw;
            }
        }
    }
}
