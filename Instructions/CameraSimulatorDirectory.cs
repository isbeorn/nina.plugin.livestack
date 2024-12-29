using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageData;
using NINA.Sequencer.SequenceItem;
using NINA.WPF.Base.Model.Equipment.MyCamera.Simulator;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugin.Livestack.Instructions {
    //#if DEBUG

    [ExportMetadata("Name", "CameraSimulatorDirectory")]
    [ExportMetadata("Description", "")]
    [ExportMetadata("Icon", "")]
    [ExportMetadata("Category", "Livestack")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public partial class CameraSimulatorDirectory : SequenceItem {

        [ImportingConstructor]
        public CameraSimulatorDirectory(ICameraMediator cameraMediator) {
            this.cameraMediator = cameraMediator;
        }

        private CameraSimulatorDirectory(CameraSimulatorDirectory copyMe) : this(copyMe.cameraMediator) {
            CopyMetaData(copyMe);
        }

        public override object Clone() {
            var clone = new CameraSimulatorDirectory(this) {
                Directory = this.Directory
            };

            return clone;
        }

        [ObservableProperty]
        [property: JsonProperty]
        private string directory;

        private readonly ICameraMediator cameraMediator;

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var decorator = cameraMediator.GetDevice() as PersistSettingsCameraDecorator;
            if (decorator == null) { return; }
            var camera = decorator.Camera as SimulatorCamera;
            if (camera == null) { return; }

            var t = camera.GetType();
            var filesProp = t.GetField("files", System.Reflection.BindingFlags.NonPublic | BindingFlags.Instance);
            filesProp.SetValue(camera, System.IO.Directory.GetFiles(this.Directory).Where(BaseImageData.FileIsSupported).ToArray());

            var currentFileProp = t.GetField("currentFile", System.Reflection.BindingFlags.NonPublic | BindingFlags.Instance);
            currentFileProp.SetValue(camera, 0);

            await Task.Delay(1000);
        }
    }

    //#endif
}