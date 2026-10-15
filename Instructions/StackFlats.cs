using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Image.Interfaces;
using NINA.Plugin.Livestack.Image;
using NINA.Plugin.Livestack.LivestackDockables;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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
        private FlatStackSession session;
        private Func<object, BeforeFinalizeImageSavedEventArgs, Task> receive;
        private Task previousWork = Task.CompletedTask;

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

        public int QueueEntries => session?.QueueEntries ?? 0;

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
            Unsubscribe();
            FlatStackSession current = session;
            if (current == null) return;
            Task work = current.CompleteAsync(progress, token);
            previousWork = Task.WhenAll(previousWork, ObserveCompletionAsync(work));
            if (WaitForStack) await work;
        }

        public override void SequenceBlockInitialize() {
            SequenceBlockTeardown();
            string workingDirectory = LivestackMediator.Plugin.WorkingDirectory;
            FlatStackSession current = new(LivestackMediator.CalibrationVM, workingDirectory, RetrieveTarget(Parent),
                LivestackMediator.Plugin.SaveCalibratedFlats, previousWork, () => RaisePropertyChanged(nameof(QueueEntries)));
            session = current;
            receive = (sender, e) => {
                IImageData image = e.Image.RawImageData;
                if (image.MetaData.Image.ImageType != "FLAT") return Task.CompletedTask;
                IList<ImagePattern> patterns = e.Patterns.Select(pattern => new ImagePattern(pattern.Key, pattern.Description, pattern.Category) { Value = pattern.Value }).ToList();
                return current.EnqueueAsync(token => PrepareFlatAsync(image, patterns, workingDirectory, token));
            };
            imageSaveMediator.BeforeFinalizeImageSaved += receive;
        }

        public override void SequenceBlockTeardown() {
            Unsubscribe();
            // Normal background completion must survive block teardown. Its execution token still cancels it.
            if (session != null && !session.IsCompleting) {
                previousWork = Task.WhenAll(previousWork, ObserveCompletionAsync(session.AbortAsync()));
            }
        }

        private void Unsubscribe() {
            if (receive == null) return;
            imageSaveMediator.BeforeFinalizeImageSaved -= receive;
            receive = null;
        }

        private static async Task ObserveCompletionAsync(Task work) {
            try { await work.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Error(ex); }
        }

        private async Task<LiveStackItem> PrepareFlatAsync(IImageData image, IList<ImagePattern> patterns, string workingDirectory, CancellationToken token) {
            string pattern = Path.GetFileName(profileService.ActiveProfile.ImageFileSettings.GetFilePattern(image.MetaData.Image.ImageType));
            string path = await image.SaveToDisk(new NINA.Image.FileFormat.FileSaveInfo {
                FilePath = Path.Combine(workingDirectory, "temp"), FilePattern = pattern, FileType = Core.Enum.FileTypeEnum.FITS
            }, token, true, patterns).ConfigureAwait(false);
            try {
                return new LiveStackItem(path, image.MetaData.Target.Name, image.MetaData.FilterWheel.Filter,
                    image.MetaData.Image.ExposureTime, image.MetaData.Camera.Gain, image.MetaData.Camera.Offset,
                    image.MetaData.Camera.BinX, image.MetaData.Camera.BinY,
                    image.Properties.Width, image.Properties.Height, image.Properties.BitDepth, image.Properties.IsBayered,
                    image.StarDetectionAnalysis, image.MetaData);
            } catch {
                File.Delete(path);
                throw;
            }
        }
    }
}