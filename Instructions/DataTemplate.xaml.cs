﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugin.Livestack.Instructions {

    [Export(typeof(ResourceDictionary))]
    public partial class DataTemplate : ResourceDictionary {

        public DataTemplate() {
            InitializeComponent();
        }
    }
}