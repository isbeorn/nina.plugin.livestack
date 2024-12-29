using NINA.Plugin.Livestack.LivestackDockables;
using NINA.Profile.Interfaces;
using System;

namespace NINA.Plugin.Livestack {

    public static class LivestackMediator {
        public static Livestack Plugin { get; private set; }
        public static IPluginOptionsAccessor PluginSettings { get; private set; }
        public static CalibrationVM CalibrationVM { get; private set; }
        public static LivestackDockable LiveStackDockable { get; private set; }

        public static void RegisterPlugin(Livestack plugin) {
            Plugin = plugin;
        }

        public static void RegisterSettings(IPluginOptionsAccessor pluginSettings) {
            PluginSettings = pluginSettings;
        }

        public static void RegisterCalibrationVM(CalibrationVM calibrationVM) {
            CalibrationVM = calibrationVM;
        }

        internal static void RegisterLivestackDockable(LivestackDockable livestackDockable) {
            LiveStackDockable = livestackDockable;
        }
    }
}