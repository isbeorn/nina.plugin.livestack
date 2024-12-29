using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace NINA.Plugin.Livestack.LivestackDockables {

    public class TabTemplateSelector : DataTemplateSelector {
        public DataTemplate ColorCombinationTab { get; set; }
        public DataTemplate LiveStackTab { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container) {
            if (item is ColorCombinationTab) {
                return ColorCombinationTab;
            } else if (item is LiveStackTab) {
                return LiveStackTab;
            }

            return LiveStackTab;
        }
    }
}