using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace NINA.Plugin.Livestack.LivestackDockables {

    public interface IStackTab {
        public string Target { get; }
        public string Filter { get; }
        public bool Locked { get; set; }
        public BitmapSource StackImage { get; }
    }
}