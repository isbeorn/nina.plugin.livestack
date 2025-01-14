using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace NINA.Plugin.Livestack {

    public class MinusOneToIgnoreStringConverter : IValueConverter {

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value.GetType() == typeof(string)) { return "Ignore"; }
            if (System.Convert.ToInt32(value) == -1) {
                return "Ignore";
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            if (value.ToString() == "Ignore") {
                return -1;
            }
            return value;
        }
    }
}