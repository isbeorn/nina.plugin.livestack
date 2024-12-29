using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugin.Livestack.Instructions {

    [ExportMetadata("Name", "Start live stacking")]
    [ExportMetadata("Description", "This instruction will start the live stack")]
    [ExportMetadata("Icon", "Livestack_StackSVG")]
    [ExportMetadata("Category", "Livestack")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    internal class StartLivestacking : SequenceItem {

        [ImportingConstructor]
        public StartLivestacking() {
        }

        private StartLivestacking(StartLivestacking copyMe) : this() {
            CopyMetaData(copyMe);
        }

        public override object Clone() {
            var clone = new StartLivestacking(this) {
            };

            return clone;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            Logger.Info("Starting up live stack");
            await Application.Current.Dispatcher.BeginInvoke(() => {
                if (!LivestackMediator.LiveStackDockable.StartLiveStackCommand.IsRunning) {
                    _ = LivestackMediator.LiveStackDockable.StartLiveStackCommand.ExecuteAsync(null);
                }
            });
        }
    }
}