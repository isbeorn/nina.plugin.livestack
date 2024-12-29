using System;
using System.Collections.Generic;
using NINA.Core.Utility;

namespace NINA.Plugin.Livestack.Utility {
    public class DisposableList<T> : List<T>, IDisposable where T : IDisposable {

        public void Dispose() {
            foreach (var obj in this) {
                try {
                    obj.Dispose();
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            }
        }
    }
}