using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace NINA.Plugin.Livestack.LivestackDockables {

    internal class ColorCombinationPrompt : BaseINPC {
        public IList<IStackTab> FilterTabs { get; private set; }
        private IList<IStackTab> allTabs;
        public IList<string> Targets { get; private set; }

        public ColorCombinationPrompt(IList<IStackTab> tabs) {
            this.allTabs = tabs;
            this.ContinueCommand = new GalaSoft.MvvmLight.Command.RelayCommand(() => { Continue = true; });
            this.StackEachNrOfFrames = 1;

            Targets = allTabs.GroupBy(x => x.Target).Where(g => g.All(x => x is not ColorCombinationTab)).Select(x => x.First()).Select(x => x.Target).ToList();

            Target = Targets.FirstOrDefault();
        }

        private void FilterByTarget(string target) {
            if (string.IsNullOrEmpty(target)) { return; }

            FilterTabs = allTabs.Where(x => x is not ColorCombinationTab && x.Target == target).ToList();

            var distanceRed = int.MaxValue;
            var distanceHa = int.MaxValue;
            for (int i = 0; i < FilterTabs.Count; i++) {
                distanceRed = Math.Min(distanceRed, Fastenshtein.Levenshtein.Distance("Red".ToLower(), FilterTabs[i].Filter.ToLower()));
                distanceHa = Math.Min(distanceHa, Fastenshtein.Levenshtein.Distance("HA".ToLower(), FilterTabs[i].Filter.ToLower()));
            }

            if (distanceRed <= distanceHa) {
                RedChannel = GetFilterForChannel("Red");
                GreenChannel = GetFilterForChannel("Green");
                BlueChannel = GetFilterForChannel("Blue");
            } else {
                if (FilterTabs.Count < 3) {
                    RedChannel = GetFilterForChannel("HA");
                    GreenChannel = GetFilterForChannel("OIII");
                    BlueChannel = GetFilterForChannel("OIII");
                } else {
                    RedChannel = GetFilterForChannel("SII");
                    GreenChannel = GetFilterForChannel("HA");
                    BlueChannel = GetFilterForChannel("OIII");
                }
            }

            RaisePropertyChanged(nameof(FilterTabs));
            RaisePropertyChanged(nameof(RedChannel));
            RaisePropertyChanged(nameof(BlueChannel));
            RaisePropertyChanged(nameof(GreenChannel));
        }

        private LiveStackTab GetFilterForChannel(string target) {
            return FilterTabs.OrderBy(x => Fastenshtein.Levenshtein.Distance(target.ToLower(), x.Filter.ToLower())).First() as LiveStackTab;
        }

        private string target;

        public string Target {
            get => target;
            set {
                target = value;
                FilterByTarget(target);
                RaisePropertyChanged();
            }
        }

        public LiveStackTab RedChannel { get; set; }
        public LiveStackTab BlueChannel { get; set; }
        public LiveStackTab GreenChannel { get; set; }

        public int StackEachNrOfFrames { get; set; }

        public bool Continue { get; set; } = false;

        public ICommand ContinueCommand { get; }
    }
}