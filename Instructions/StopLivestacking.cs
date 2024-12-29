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

    [ExportMetadata("Name", "Stop live stacking")]
    [ExportMetadata("Description", "This instruction will stop the live stack")]
    [ExportMetadata("Icon", "Livestack_StackStopSVG")]
    [ExportMetadata("Category", "Livestack")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    internal class StopLivestacking : SequenceItem {

        [ImportingConstructor]
        public StopLivestacking() {
        }

        private StopLivestacking(StopLivestacking copyMe) : this() {
            CopyMetaData(copyMe);
        }

        public override object Clone() {
            var clone = new StopLivestacking(this) {
            };

            return clone;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            Logger.Info("Stopping up live stack");
            await Application.Current.Dispatcher.BeginInvoke(() => {
                if (LivestackMediator.LiveStackDockable.StartLiveStackCommand.IsRunning) {
                    LivestackMediator.LiveStackDockable.StartLiveStackCancelCommand.Execute(null);
                }
            });
        }
    }
}