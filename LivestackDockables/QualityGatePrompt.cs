using NINA.Core.Utility;
using NINA.Plugin.Livestack.QualityGate;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace NINA.Plugin.Livestack.LivestackDockables {

    public partial class QualityGatePrompt {

        public QualityGatePrompt() {
            var availableGateTypes = Assembly.GetAssembly(typeof(QualityGatePrompt)).GetTypes().Where(t => t.IsClass && t.Namespace == typeof(IQualityGate).Namespace).ToList();
            AvailableGates = new List<IQualityGate>();
            foreach (var type in availableGateTypes) {
                AvailableGates.Add((IQualityGate)Activator.CreateInstance(type));
            }
            SelectedGate = AvailableGates.First();

            this.ContinueCommand = new GalaSoft.MvvmLight.Command.RelayCommand(() => { Continue = true; });
        }

        public List<IQualityGate> AvailableGates { get; }
        public IQualityGate SelectedGate { get; set; }

        public bool Continue { get; set; } = false;

        public ICommand ContinueCommand { get; }
    }
}