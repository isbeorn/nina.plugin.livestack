using Accord.Statistics;
using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Image.FileFormat.FITS;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.LivestackDockables;
using NINA.Plugin.Livestack.Utility;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.WPF.Base.Interfaces.Mediator;
using Nito.AsyncEx;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugin.Livestack.Instructions {

    [ExportMetadata("Name", "Stack flats")]
    [ExportMetadata("Description", "This instruction will calibrate and stack flat frames that are taken inside the current instruction set (and child instruction sets) and register the stacked flats master for the live stack. Place it after your flats.")]
    [ExportMetadata("Icon", "Livestack_StackFlatsSVG")]
    [ExportMetadata("Category", "Livestack")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public partial class StackFlats : SequenceItem {
        private readonly IProfileService profileService;
        private IImageSaveMediator imageSaveMediator;
        private IApplicationStatusMediator applicationStatusMediator;
        private CancellationTokenSource cts;
        private AsyncProducerConsumerQueue<LiveStackItem> queue = new AsyncProducerConsumerQueue<LiveStackItem>(1000);
        private Dictionary<string, List<string>> FlatsToIntegrate = new Dictionary<string, List<string>>();
        private Task workerTask;

        [ImportingConstructor]
        public StackFlats(IProfileService profileService, IImageSaveMediator imageSaveMediator, IApplicationStatusMediator applicationStatusMediator) {
            this.profileService = profileService;
            this.imageSaveMediator = imageSaveMediator;
            this.applicationStatusMediator = applicationStatusMediator;
        }

        private StackFlats(StackFlats copyMe) : this(copyMe.profileService, copyMe.imageSaveMediator, copyMe.applicationStatusMediator) {
            CopyMetaData(copyMe);
        }

        public override object Clone() {
            var clone = new StackFlats(this) {
                WaitForStack = WaitForStack
            };

            return clone;
        }

        [ObservableProperty]
        private int queueEntries;

        [ObservableProperty]
        [property: JsonProperty]
        private bool waitForStack;

        private string RetrieveTarget(ISequenceContainer parent) {
            if (parent != null) {
                var container = parent as IDeepSkyObjectContainer;
                if (container != null) {
                    if (string.IsNullOrWhiteSpace(container.Target.DeepSkyObject.NameAsAscii)) {
                        return LiveStackBag.NOTARGET;
                    } else {
                        return container.Target.DeepSkyObject.NameAsAscii;
                    }
                } else {
                    return RetrieveTarget(parent.Parent);
                }
            } else {
                return LiveStackBag.NOTARGET;
            }
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(StackFlats)}";
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var workTask = Task.Run(async () => {
                var workingDir = LivestackMediator.Plugin.WorkingDirectory;
                Logger.Info("Stop listening for flat frames to calibrate");
                queue.CompleteAdding();
                imageSaveMediator.BeforeFinalizeImageSaved -= ImageSaveMediator_BeforeFinalizeImageSaved;

                try {
                    if (queueEntries <= 0 && FlatsToIntegrate.Keys.Count == 0) {
                        Logger.Info("No flat frames to stack");
                    } else {
                        Logger.Info("Finishing up remaining flat calibration");
                        progress?.Report(new ApplicationStatus() { Status = $"Waiting for flat calibration to finish" });
                        await workerTask;

                        foreach (var filter in FlatsToIntegrate.Keys) {
                            try {
                                var list = FlatsToIntegrate[filter].ToArray();

                                if (list.Length < 3) {
                                    throw new Exception($"Not enough flats to generate masters for {filter}");
                                }

                                Logger.Info($"Generating flat master for filter {filter}");
                                progress?.Report(new ApplicationStatus() { Status = $"Generating flat master for filter {filter}" });

                                using (var fitsFiles = new DisposableList<CFitsioFITSReader>()) {
                                    foreach (var file in list) {
                                        try {
                                            fitsFiles.Add(new CFitsioFITSReader(file));
                                        } catch (Exception ex) {
                                            Logger.Error($"Failed to open flat frame file {file}", ex);
                                        }
                                    }

                                    Logger.Info($"Stacking flat for filter {filter} using {fitsFiles.Count} frames");
                                    var stack = ImageMath.PercentileClipping(fitsFiles, 0.2, 0.1);

                                    var target = RetrieveTarget(this.Parent);
                                    var outputDir = Path.Combine(workingDir, "stacks");
                                    if (!Directory.Exists(outputDir)) { Directory.CreateDirectory(outputDir); }
                                    var output = Path.Combine(workingDir, "stacks", CoreUtil.ReplaceAllInvalidFilenameChars($"MASTER_FLAT_{target}_{filter}.fits"));
                                    output = CoreUtil.GetUniqueFilePath(output, "{0}_{1}");

                                    Logger.Info($"Writing master flat {output}");
                                    var stackFits = new CFitsioFITSExtendedWriter(output, stack, fitsFiles[0].Width, fitsFiles[0].Height, CfitsioNative.COMPRESSION.NOCOMPRESS);
                                    var metaData = fitsFiles[0].RestoreMetaData();
                                    stackFits.PopulateHeaderCards(metaData);
                                    stackFits.Close();

                                    var calibrationMeta = new CalibrationFrameMeta(CalibrationFrameType.FLAT, output, 0, 0, 0, filter, fitsFiles[0].Width, fitsFiles[0].Height, stack.Mean());
                                    LivestackMediator.CalibrationVM.AddSessionFlatMaster(calibrationMeta);
                                }
                                Logger.Info($"Cleaning up flat files for filter {filter}");
                                foreach (var file in list) {
                                    try {
                                        File.Delete(file);
                                    } catch (Exception ex) {
                                        Logger.Error(ex);
                                    }
                                }
                            } catch (Exception ex) {
                                Logger.Error($"Failed to generate flat master for filter {filter}", ex);
                            }
                        }
                    }
                } finally {
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            });

            if (WaitForStack) {
                await workTask;
            }
        }

        public override void SequenceBlockInitialize() {
            try {
                cts?.Dispose();
            } catch (Exception) { }

            cts = new CancellationTokenSource();

            FlatsToIntegrate.Clear();
            queue = new AsyncProducerConsumerQueue<LiveStackItem>(1000);
            imageSaveMediator.BeforeFinalizeImageSaved += ImageSaveMediator_BeforeFinalizeImageSaved;
            workerTask = StartCalibrationQueueWorker();
        }

        public override void SequenceBlockTeardown() {
            imageSaveMediator.BeforeFinalizeImageSaved -= ImageSaveMediator_BeforeFinalizeImageSaved;
            try {
                cts?.Cancel();
            } catch (Exception) { }
        }

        private async Task ImageSaveMediator_BeforeFinalizeImageSaved(object sender, BeforeFinalizeImageSavedEventArgs e) {
            if (e.Image.RawImageData.MetaData.Image.ImageType == NINA.Equipment.Model.CaptureSequence.ImageTypes.FLAT) {
                await Task.Run(async () => {
                    try {
                        var path = await e.Image.RawImageData.SaveToDisk(
                            new NINA.Image.FileFormat.FileSaveInfo() {
                                FilePath = Path.Combine(LivestackMediator.Plugin.WorkingDirectory, "temp"),
                                FilePattern = Path.GetRandomFileName(),
                                FileType = Core.Enum.FileTypeEnum.FITS
                            },
                            default, true
                        );
                        await queue.EnqueueAsync(new LiveStackItem(path: path,
                                                                   target: e.Image.RawImageData.MetaData.Target.Name,
                                                                   filter: e.Image.RawImageData.MetaData.FilterWheel.Filter,
                                                                   exposureTime: e.Image.RawImageData.MetaData.Image.ExposureTime,
                                                                   gain: e.Image.RawImageData.MetaData.Camera.Gain,
                                                                   offset: e.Image.RawImageData.MetaData.Camera.Offset,
                                                                   width: e.Image.RawImageData.Properties.Width,
                                                                   height: e.Image.RawImageData.Properties.Height,
                                                                   bitDepth: (int)profileService.ActiveProfile.CameraSettings.BitDepth,
                                                                   isBayered: e.Image.RawImageData.Properties.IsBayered,
                                                                   analysis: e.Image.RawImageData.StarDetectionAnalysis));
                        Interlocked.Increment(ref queueEntries);
                        RaisePropertyChanged(nameof(QueueEntries));
                    } catch (Exception) {
                    }
                });
            }
        }

        private async Task StartCalibrationQueueWorker() {
            try {
                var workingDir = LivestackMediator.Plugin.WorkingDirectory;
                if (!Directory.Exists(workingDir)) {
                    Directory.CreateDirectory(workingDir);
                }

                var progress = new Progress<ApplicationStatus>((p) => applicationStatusMediator.StatusUpdate(p));
                var token = cts.Token;

                using var calibrationManager = new CalibrationManager();

                while (await queue.OutputAvailableAsync(token)) {
                    try {
                        token.ThrowIfCancellationRequested();
                        var item = await queue.DequeueAsync(token);

                        Logger.Debug($"Preparing flat frame for stack {item.Path}");

                        var filter = string.IsNullOrWhiteSpace(item.Filter) ? LiveStackBag.NOFILTER : item.Filter;
                        var destinationFolder = Path.Combine(workingDir, "calibrated", "flat", filter);
                        if (!Directory.Exists(destinationFolder)) {
                            Directory.CreateDirectory(destinationFolder);
                        }
                        var destinationFile = Path.Combine(destinationFolder, CoreUtil.ReplaceAllInvalidFilenameChars($"{DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}-{filter}.fits"));
                        destinationFile = CoreUtil.GetUniqueFilePath(destinationFile, "{0}_{1}");

                        foreach (var meta in LivestackMediator.CalibrationVM.BiasLibrary) {
                            calibrationManager.RegisterBiasMaster(meta);
                        }
                        foreach (var meta in LivestackMediator.CalibrationVM.DarkLibrary) {
                            calibrationManager.RegisterDarkMaster(meta);
                        }
                        ushort[] theImageArray;
                        using (CFitsioFITSReader reader = new CFitsioFITSReader(item.Path)) {
                            theImageArray = calibrationManager.ApplyFlatFrameCalibrationInPlace(reader, item.Width, item.Height, item.ExposureTime, item.Gain, item.Offset, item.Filter, item.IsBayered, token);
                        }

                        Logger.Debug("Computing median after calibration");
                        var (median, _) = ImageMath.CalculateMedianAndMAD(theImageArray);

                        Logger.Info($"Saving calibrated flat frame at {destinationFile}");
                        var calibratedFits = new CFitsioFITSExtendedWriter(destinationFile, theImageArray, item.Width, item.Height, CfitsioNative.COMPRESSION.NOCOMPRESS);
                        calibratedFits.AddHeader("GAIN", item.Gain, "");
                        calibratedFits.AddHeader("OFFSET", item.Offset, "");
                        calibratedFits.AddHeader("EXPOSURE", item.ExposureTime, "");
                        calibratedFits.AddHeader("FILTER", item.Filter, "");
                        calibratedFits.AddHeader("MEDIAN", median, "");
                        calibratedFits.Close();

                        if (!FlatsToIntegrate.ContainsKey(filter)) {
                            FlatsToIntegrate[filter] = new List<string>();
                        }
                        FlatsToIntegrate[filter].Add(destinationFile);

                        File.Delete(item.Path);
                    } catch (OperationCanceledException) {
                        throw;
                    } catch (Exception ex) {
                        Logger.Error(ex);
                    }
                    Interlocked.Decrement(ref queueEntries);
                    RaisePropertyChanged(nameof(QueueEntries));
                }
            } catch (OperationCanceledException) { }
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}