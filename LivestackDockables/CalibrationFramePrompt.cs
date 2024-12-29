using NINA.Plugin.Livestack.Image;
using System.Windows.Input;

namespace NINA.Plugin.Livestack.LivestackDockables {

    public class CalibrationFramePrompt {

        public CalibrationFramePrompt(CalibrationFrameMeta context) {
            this.Context = context;
            this.ContinueCommand = new GalaSoft.MvvmLight.Command.RelayCommand(() => { Continue = true; });
        }

        public bool Continue { get; set; } = false;

        public CalibrationFrameMeta Context { get; }
        public ICommand ContinueCommand { get; }
    }
}