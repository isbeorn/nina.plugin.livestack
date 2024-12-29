using Newtonsoft.Json;
using NINA.Core.Utility;
using NINA.Plugin.Livestack.Image;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugin.Livestack.QualityGate {

    [JsonObject(MemberSerialization.OptIn)]
    public class StarAbsoluteGate : BaseINPC, IQualityGate {

        [JsonProperty]
        public string Name => "Stars above threshold";

        private double value;

        [JsonProperty]
        public double Value {
            get => value;
            set {
                this.value = value;
                RaisePropertyChanged();
            }
        }

        public bool Passes(LiveStackItem item) {
            return item.StarList.Count > Value;
        }
    }
}