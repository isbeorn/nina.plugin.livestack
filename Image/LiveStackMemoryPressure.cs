using NINA.Core.Utility;
using System;

namespace NINA.Plugin.Livestack.Image {
    internal static class LiveStackMemoryPressure {
        private static readonly object syncRoot = new();
        private static DateTimeOffset lastCollection = DateTimeOffset.MinValue;

        public static void CollectIfNeeded(string reason) {
            GCMemoryInfo info = GC.GetGCMemoryInfo();
            if (info.HighMemoryLoadThresholdBytes <= 0 || info.MemoryLoadBytes < info.HighMemoryLoadThresholdBytes * 0.85) {
                return;
            }
            lock (syncRoot) {
                if (DateTimeOffset.UtcNow - lastCollection < TimeSpan.FromSeconds(15)) {
                    return;
                }
                lastCollection = DateTimeOffset.UtcNow;
            }
            ImageBufferPool.Shared.Trim();
            Logger.Info($"Live Stack released cached scratch buffers under memory pressure ({reason}).");
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
        }

        public static void TrimAfterReleasingLargeBuffers(string reason) {
            ImageBufferPool.Shared.Trim();
            Logger.Debug($"Live Stack released cached scratch buffers ({reason}).");
        }
    }
}