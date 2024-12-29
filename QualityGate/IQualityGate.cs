using NINA.Plugin.Livestack.Image;
using System.ComponentModel;

namespace NINA.Plugin.Livestack.QualityGate {

    public interface IQualityGate : INotifyPropertyChanged {
        string Name { get; }
        double Value { get; }

        bool Passes(LiveStackItem item);
    }
}